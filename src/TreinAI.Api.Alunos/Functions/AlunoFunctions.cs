using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using TreinAI.Api.Alunos.Validators;
using TreinAI.Shared.Exceptions;
using TreinAI.Shared.Middleware;
using TreinAI.Shared.Models;
using TreinAI.Shared.Repositories;
using TreinAI.Shared.Validation;

namespace TreinAI.Api.Alunos.Functions;

/// <summary>
/// Azure Functions for Aluno (student) CRUD operations.
/// All endpoints are tenant-scoped via TenantMiddleware.
/// </summary>
public class AlunoFunctions
{
    private readonly IRepository<Aluno> _repository;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<AlunoFunctions> _logger;

    public AlunoFunctions(
        IRepository<Aluno> repository,
        TenantContext tenantContext,
        ILogger<AlunoFunctions> logger)
    {
        _repository = repository;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/alunos — List all students for the current tenant.
    /// Supports pagination via ?pageSize=&continuationToken=
    /// </summary>
    [Function("GetAlunos")]
    public async Task<HttpResponseData> GetAlunos(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "alunos")] HttpRequestData req)
    {
        _logger.LogInformation("Getting alunos for tenant {TenantId}", _tenantContext.TenantId);

        // Check for pagination params
        var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var pageSizeStr = queryParams["pageSize"];
        var continuationToken = queryParams["continuationToken"];

        if (int.TryParse(pageSizeStr, out var pageSize) && pageSize > 0)
        {
            var pagedResult = await _repository.GetPagedAsync(
                _tenantContext.TenantId, pageSize, continuationToken);

            return await ValidationHelper.OkAsync(req, new
            {
                items = pagedResult.Items,
                continuationToken = pagedResult.ContinuationToken,
                hasMore = !string.IsNullOrEmpty(pagedResult.ContinuationToken)
            });
        }

        // If professor, only return their students
        if (_tenantContext.IsProfessor)
        {
            var alunos = await _repository.QueryAsync(
                _tenantContext.TenantId,
                a => a.ProfessorId == _tenantContext.UserId);
            return await ValidationHelper.OkAsync(req, alunos);
        }

        var allAlunos = await _repository.GetAllAsync(_tenantContext.TenantId);
        return await ValidationHelper.OkAsync(req, allAlunos);
    }

    /// <summary>
    /// GET /api/alunos/{id} — Get a specific student by ID.
    /// </summary>
    [Function("GetAlunoById")]
    public async Task<HttpResponseData> GetAlunoById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "alunos/{id}")] HttpRequestData req,
        string id)
    {
        _logger.LogInformation("Getting aluno {AlunoId} for tenant {TenantId}", id, _tenantContext.TenantId);

        var aluno = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (aluno == null)
        {
            throw new NotFoundException("Aluno", id);
        }

        // If professor, verify this student belongs to them
        if (_tenantContext.IsProfessor && aluno.ProfessorId != _tenantContext.UserId)
        {
            throw new ForbiddenException("Você não tem permissão para acessar este aluno.");
        }

        // If aluno, verify they can only see themselves
        if (_tenantContext.IsAluno && aluno.UserId != _tenantContext.UserId)
        {
            throw new ForbiddenException("Você só pode acessar seus próprios dados.");
        }

        return await ValidationHelper.OkAsync(req, aluno);
    }

    /// <summary>
    /// POST /api/alunos — Create a new student.
    /// Only admin and professor roles can create students.
    /// </summary>
    [Function("CreateAluno")]
    public async Task<HttpResponseData> CreateAluno(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "alunos")] HttpRequestData req)
    {
        if (_tenantContext.IsAluno)
        {
            throw new ForbiddenException("Alunos não podem criar outros alunos.");
        }

        var validator = new AlunoValidator();
        var aluno = await ValidationHelper.ValidateRequestAsync(req, validator);

        aluno.TenantId = _tenantContext.TenantId;
        aluno.CreatedBy = _tenantContext.UserId;
        aluno.UpdatedBy = _tenantContext.UserId;

        // If professor creating, auto-assign themselves
        if (_tenantContext.IsProfessor && string.IsNullOrEmpty(aluno.ProfessorId))
        {
            aluno.ProfessorId = _tenantContext.UserId;
        }

        _logger.LogInformation("Creating aluno {AlunoNome} for tenant {TenantId}",
            aluno.Nome, _tenantContext.TenantId);

        var created = await _repository.CreateAsync(aluno);
        return await ValidationHelper.CreatedAsync(req, created);
    }

    /// <summary>
    /// PUT /api/alunos/{id} — Update an existing student.
    /// </summary>
    [Function("UpdateAluno")]
    public async Task<HttpResponseData> UpdateAluno(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "alunos/{id}")] HttpRequestData req,
        string id)
    {
        var existing = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (existing == null)
        {
            throw new NotFoundException("Aluno", id);
        }

        // Authorization checks
        if (_tenantContext.IsProfessor && existing.ProfessorId != _tenantContext.UserId)
        {
            throw new ForbiddenException("Você não tem permissão para editar este aluno.");
        }
        if (_tenantContext.IsAluno && existing.UserId != _tenantContext.UserId)
        {
            throw new ForbiddenException("Você só pode editar seus próprios dados.");
        }

        var validator = new AlunoValidator();
        var aluno = await ValidationHelper.ValidateRequestAsync(req, validator);

        // Preserve system fields
        aluno.Id = id;
        aluno.TenantId = _tenantContext.TenantId;
        aluno.CreatedAt = existing.CreatedAt;
        aluno.CreatedBy = existing.CreatedBy;
        aluno.UpdatedBy = _tenantContext.UserId;

        // Aluno role cannot change their own professor
        if (_tenantContext.IsAluno)
        {
            aluno.ProfessorId = existing.ProfessorId;
        }

        _logger.LogInformation("Updating aluno {AlunoId} for tenant {TenantId}", id, _tenantContext.TenantId);

        var updated = await _repository.UpdateAsync(aluno);
        return await ValidationHelper.OkAsync(req, updated);
    }

    /// <summary>
    /// DELETE /api/alunos/{id} — Soft-delete a student.
    /// Only admin and professor (own students) can delete.
    /// </summary>
    [Function("DeleteAluno")]
    public async Task<HttpResponseData> DeleteAluno(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "alunos/{id}")] HttpRequestData req,
        string id)
    {
        if (_tenantContext.IsAluno)
        {
            throw new ForbiddenException("Alunos não podem excluir registros.");
        }

        var existing = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (existing == null)
        {
            throw new NotFoundException("Aluno", id);
        }

        if (_tenantContext.IsProfessor && existing.ProfessorId != _tenantContext.UserId)
        {
            throw new ForbiddenException("Você não tem permissão para excluir este aluno.");
        }

        _logger.LogInformation("Deleting aluno {AlunoId} for tenant {TenantId}", id, _tenantContext.TenantId);

        await _repository.DeleteAsync(id, _tenantContext.TenantId);
        return ValidationHelper.NoContent(req);
    }

    /// <summary>
    /// GET /api/alunos/me — Get the aluno record for the currently logged-in user.
    /// Used by aluno-role users to discover their aluno record ID.
    /// </summary>
    [Function("GetAlunoMe")]
    public async Task<HttpResponseData> GetAlunoMe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "alunos/me")] HttpRequestData req)
    {
        _logger.LogInformation("Getting aluno record for user {UserId}", _tenantContext.UserId);

        var alunos = await _repository.QueryAsync(
            _tenantContext.TenantId,
            a => a.UserId == _tenantContext.UserId);

        var aluno = alunos.FirstOrDefault();
        if (aluno == null)
        {
            throw new NotFoundException("Aluno record not found for current user.");
        }

        return await ValidationHelper.OkAsync(req, aluno);
    }

    /// <summary>
    /// GET /api/alunos/count — Count students for the current tenant.
    /// </summary>
    [Function("CountAlunos")]
    public async Task<HttpResponseData> CountAlunos(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "alunos/count")] HttpRequestData req)
    {
        int count;
        if (_tenantContext.IsProfessor)
        {
            count = await _repository.CountAsync(
                _tenantContext.TenantId,
                a => a.ProfessorId == _tenantContext.UserId);
        }
        else
        {
            count = await _repository.CountAsync(_tenantContext.TenantId);
        }

        return await ValidationHelper.OkAsync(req, new { count });
    }
}
