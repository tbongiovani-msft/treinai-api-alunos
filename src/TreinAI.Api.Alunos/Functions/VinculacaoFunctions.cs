using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using TreinAI.Shared.Exceptions;
using TreinAI.Shared.Middleware;
using TreinAI.Shared.Models;
using TreinAI.Shared.Repositories;
using TreinAI.Shared.Services;
using TreinAI.Shared.Validation;

namespace TreinAI.Api.Alunos.Functions;

/// <summary>
/// Azure Functions for professor-aluno linking (E13-13, E13-14).
/// Allows professors to send invites and alunos to accept them.
/// </summary>
public class VinculacaoFunctions
{
    private readonly IRepository<Aluno> _alunoRepository;
    private readonly IRepository<Usuario> _usuarioRepository;
    private readonly INotificationService _notificationService;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<VinculacaoFunctions> _logger;

    public VinculacaoFunctions(
        IRepository<Aluno> alunoRepository,
        IRepository<Usuario> usuarioRepository,
        INotificationService notificationService,
        TenantContext tenantContext,
        ILogger<VinculacaoFunctions> logger)
    {
        _alunoRepository = alunoRepository;
        _usuarioRepository = usuarioRepository;
        _notificationService = notificationService;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/alunos/vincular — Professor sends a linking invite to an aluno.
    /// Body: { "alunoEmail": "aluno@email.com" }  or  { "alunoId": "u-aluno-xxx" }
    /// Creates a notification of type "convite_vinculacao" for the aluno.
    /// </summary>
    [Function("EnviarConviteVinculacao")]
    public async Task<HttpResponseData> EnviarConvite(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "alunos/vincular")] HttpRequestData req)
    {
        if (!_tenantContext.IsProfessor && !_tenantContext.IsAdmin)
        {
            throw new ForbiddenException("Apenas professores podem enviar convites de vinculação.");
        }

        var body = await req.ReadFromJsonAsync<ConviteVinculacaoRequest>();
        if (body == null)
        {
            throw new BusinessValidationException("Body inválido.");
        }

        // Find the aluno by email or ID
        Aluno? aluno = null;

        if (!string.IsNullOrWhiteSpace(body.AlunoId))
        {
            aluno = await _alunoRepository.GetByIdAsync(body.AlunoId, _tenantContext.TenantId);
        }
        else if (!string.IsNullOrWhiteSpace(body.AlunoEmail))
        {
            var alunos = await _alunoRepository.QueryAsync(
                _tenantContext.TenantId,
                a => a.Email == body.AlunoEmail);
            aluno = alunos.FirstOrDefault();
        }

        if (aluno == null)
        {
            throw new NotFoundException("Aluno", body.AlunoId ?? body.AlunoEmail ?? "");
        }

        if (aluno.ProfessorId == _tenantContext.UserId)
        {
            throw new BusinessValidationException("Este aluno já está vinculado a você.");
        }

        // Find the aluno's user record to send notification
        var targetUserId = aluno.UserId;
        if (string.IsNullOrEmpty(targetUserId))
        {
            throw new BusinessValidationException("Este aluno não possui uma conta de usuário vinculada.");
        }

        _logger.LogInformation(
            "Professor {ProfessorId} sending linking invite to aluno {AlunoId} (user {UserId})",
            _tenantContext.UserId, aluno.Id, targetUserId);

        // Get professor name for the notification message
        var professorName = _tenantContext.UserName;
        if (string.IsNullOrEmpty(professorName))
        {
            professorName = "Seu professor";
        }

        await _notificationService.CreateAsync(
            _tenantContext.TenantId,
            targetUserId,
            "Convite de vinculação",
            $"{professorName} quer ser seu professor/personal. Aceite o convite para começar a receber treinos personalizados.",
            "convite_vinculacao",
            $"/alunos/vincular/aceitar?professorId={_tenantContext.UserId}&alunoId={aluno.Id}",
            _tenantContext.UserId);

        return await ValidationHelper.OkAsync(req, new
        {
            message = "Convite de vinculação enviado com sucesso.",
            alunoId = aluno.Id,
            alunoNome = aluno.Nome
        });
    }

    /// <summary>
    /// POST /api/alunos/vincular/aceitar — Aluno accepts the linking invite.
    /// Body: { "professorId": "u-prof-001", "alunoId": "aluno-xxx" }
    /// Updates the aluno's ProfessorId and notifies the professor.
    /// </summary>
    [Function("AceitarConviteVinculacao")]
    public async Task<HttpResponseData> AceitarConvite(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "alunos/vincular/aceitar")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<AceitarConviteRequest>();
        if (body == null || string.IsNullOrEmpty(body.ProfessorId) || string.IsNullOrEmpty(body.AlunoId))
        {
            throw new BusinessValidationException("professorId e alunoId são obrigatórios.");
        }

        var aluno = await _alunoRepository.GetByIdAsync(body.AlunoId, _tenantContext.TenantId);
        if (aluno == null)
        {
            throw new NotFoundException("Aluno", body.AlunoId);
        }

        // Authorization: only the aluno themselves or admin can accept
        if (_tenantContext.IsAluno && aluno.UserId != _tenantContext.UserId)
        {
            throw new ForbiddenException("Você só pode aceitar convites para seu próprio perfil.");
        }

        if (!_tenantContext.IsAluno && !_tenantContext.IsAdmin)
        {
            throw new ForbiddenException("Apenas o aluno pode aceitar o convite.");
        }

        // Verify professor exists
        var professors = await _usuarioRepository.QueryAsync(
            _tenantContext.TenantId,
            u => u.Id == body.ProfessorId);
        var professor = professors.FirstOrDefault();

        if (professor == null || professor.Role != "professor")
        {
            throw new NotFoundException("Professor", body.ProfessorId);
        }

        _logger.LogInformation(
            "Aluno {AlunoId} accepting linking from professor {ProfessorId}",
            aluno.Id, body.ProfessorId);

        // Update aluno's professor
        aluno.ProfessorId = body.ProfessorId;
        aluno.UpdatedAt = DateTime.UtcNow;
        aluno.UpdatedBy = _tenantContext.UserId;
        await _alunoRepository.UpdateAsync(aluno);

        // Notify the professor
        await _notificationService.CreateAsync(
            _tenantContext.TenantId,
            body.ProfessorId,
            "Convite aceito!",
            $"{aluno.Nome} aceitou seu convite de vinculação. Agora você pode criar treinos personalizados.",
            "convite_aceito",
            $"/alunos/{aluno.Id}",
            _tenantContext.UserId);

        return await ValidationHelper.OkAsync(req, new
        {
            message = "Vinculação aceita com sucesso.",
            alunoId = aluno.Id,
            professorId = body.ProfessorId,
            professorNome = professor.Nome
        });
    }
}

/// <summary>
/// Request body for POST /api/alunos/vincular
/// </summary>
public class ConviteVinculacaoRequest
{
    public string? AlunoId { get; set; }
    public string? AlunoEmail { get; set; }
}

/// <summary>
/// Request body for POST /api/alunos/vincular/aceitar
/// </summary>
public class AceitarConviteRequest
{
    public string ProfessorId { get; set; } = string.Empty;
    public string AlunoId { get; set; } = string.Empty;
}
