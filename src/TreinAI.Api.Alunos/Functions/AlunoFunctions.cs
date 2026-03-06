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
    private readonly IRepository<HistoricoPeso> _historicoPesoRepository;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<AlunoFunctions> _logger;

    public AlunoFunctions(
        IRepository<Aluno> repository,
        IRepository<HistoricoPeso> historicoPesoRepository,
        TenantContext tenantContext,
        ILogger<AlunoFunctions> logger)
    {
        _repository = repository;
        _historicoPesoRepository = historicoPesoRepository;
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
    /// Special case: when id == "me", resolve the Aluno record by the current user's UserId.
    /// </summary>
    [Function("GetAlunoById")]
    public async Task<HttpResponseData> GetAlunoById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "alunos/{id}")] HttpRequestData req,
        string id)
    {
        // Special case: /api/alunos/me — resolve by current user
        if (id.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Getting aluno record for user {UserId} in tenant {TenantId}",
                _tenantContext.UserId, _tenantContext.TenantId);

            var alunos = await _repository.QueryAsync(
                _tenantContext.TenantId,
                a => a.UserId == _tenantContext.UserId);

            var myAluno = alunos.FirstOrDefault();
            if (myAluno == null)
            {
                throw new NotFoundException("Aluno", $"userId={_tenantContext.UserId}");
            }

            return await ValidationHelper.OkAsync(req, myAluno);
        }

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
    /// PATCH /api/alunos/{id}/peso — Update the weight of a student and record history.
    /// Body: { "peso": 70.5, "observacao": "optional note" }
    /// Both aluno (own data) and professor (own students) can update.
    /// </summary>
    [Function("UpdateAlunoPeso")]
    public async Task<HttpResponseData> UpdateAlunoPeso(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "alunos/{id}/peso")] HttpRequestData req,
        string id)
    {
        // Resolve "me" for alunos
        string resolvedId = id;
        if (id.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            var meList = await _repository.QueryAsync(
                _tenantContext.TenantId,
                a => a.UserId == _tenantContext.UserId);
            var me = meList.FirstOrDefault();
            if (me == null) throw new NotFoundException("Aluno", $"userId={_tenantContext.UserId}");
            resolvedId = me.Id;
        }

        var existing = await _repository.GetByIdAsync(resolvedId, _tenantContext.TenantId);
        if (existing == null) throw new NotFoundException("Aluno", resolvedId);

        // Authorization
        if (_tenantContext.IsProfessor && existing.ProfessorId != _tenantContext.UserId)
            throw new ForbiddenException("Você não tem permissão para atualizar este aluno.");
        if (_tenantContext.IsAluno && existing.UserId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode atualizar seus próprios dados.");

        var body = await req.ReadFromJsonAsync<PesoUpdateRequest>();
        if (body == null || body.Peso <= 0)
            throw new BusinessValidationException("Peso deve ser maior que zero.");

        var pesoAnterior = existing.Peso ?? 0;

        _logger.LogInformation("Updating peso for aluno {AlunoId}: {PesoAnterior} → {PesoNovo}",
            resolvedId, pesoAnterior, body.Peso);

        // Create history record
        var historico = new HistoricoPeso
        {
            TenantId = _tenantContext.TenantId,
            AlunoId = resolvedId,
            PesoAnterior = pesoAnterior,
            PesoNovo = body.Peso,
            DataRegistro = DateTime.UtcNow,
            Observacao = body.Observacao,
            CreatedBy = _tenantContext.UserId,
            UpdatedBy = _tenantContext.UserId,
        };
        await _historicoPesoRepository.CreateAsync(historico);

        // Update the aluno's current peso
        existing.Peso = body.Peso;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = _tenantContext.UserId;
        var updated = await _repository.UpdateAsync(existing);

        return await ValidationHelper.OkAsync(req, updated);
    }

    /// <summary>
    /// GET /api/alunos/{id}/historico-peso — Get weight history for a student, sorted desc.
    /// Both aluno (own data) and professor (own students) can view.
    /// </summary>
    [Function("GetHistoricoPeso")]
    public async Task<HttpResponseData> GetHistoricoPeso(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "alunos/{id}/historico-peso")] HttpRequestData req,
        string id)
    {
        // Resolve "me" for alunos
        string resolvedId = id;
        if (id.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            var meList = await _repository.QueryAsync(
                _tenantContext.TenantId,
                a => a.UserId == _tenantContext.UserId);
            var me = meList.FirstOrDefault();
            if (me == null) throw new NotFoundException("Aluno", $"userId={_tenantContext.UserId}");
            resolvedId = me.Id;
        }

        var aluno = await _repository.GetByIdAsync(resolvedId, _tenantContext.TenantId);
        if (aluno == null) throw new NotFoundException("Aluno", resolvedId);

        // Authorization
        if (_tenantContext.IsProfessor && aluno.ProfessorId != _tenantContext.UserId)
            throw new ForbiddenException("Você não tem permissão para ver este aluno.");
        if (_tenantContext.IsAluno && aluno.UserId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode acessar seus próprios dados.");

        _logger.LogInformation("Getting historico-peso for aluno {AlunoId}", resolvedId);

        var historico = await _historicoPesoRepository.QueryAsync(
            _tenantContext.TenantId,
            h => h.AlunoId == resolvedId);

        var sorted = historico.OrderByDescending(h => h.DataRegistro).ToList();

        return await ValidationHelper.OkAsync(req, sorted);
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

/// <summary>
/// Request body for PATCH /api/alunos/{id}/peso
/// </summary>
public class PesoUpdateRequest
{
    public double Peso { get; set; }
    public string? Observacao { get; set; }
}
