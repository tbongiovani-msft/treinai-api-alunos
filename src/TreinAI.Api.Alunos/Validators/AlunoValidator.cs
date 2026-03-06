using FluentValidation;
using TreinAI.Shared.Models;

namespace TreinAI.Api.Alunos.Validators;

public class AlunoValidator : AbstractValidator<Aluno>
{
    public AlunoValidator()
    {
        RuleFor(x => x.Nome)
            .NotEmpty().WithMessage("Nome é obrigatório.")
            .MaximumLength(200).WithMessage("Nome deve ter no máximo 200 caracteres.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email é obrigatório.")
            .EmailAddress().WithMessage("Email deve ser um endereço válido.");

        RuleFor(x => x.DataNascimento)
            .NotEmpty().WithMessage("Data de nascimento é obrigatória.")
            .LessThan(DateTime.UtcNow).WithMessage("Data de nascimento deve ser no passado.");

        RuleFor(x => x.Peso)
            .GreaterThan(0).WithMessage("Peso deve ser maior que zero.")
            .When(x => x.Peso.HasValue);

        RuleFor(x => x.Altura)
            .GreaterThan(0).WithMessage("Altura deve ser maior que zero.")
            .When(x => x.Altura.HasValue);

        RuleFor(x => x.Telefone)
            .MaximumLength(20).WithMessage("Telefone deve ter no máximo 20 caracteres.")
            .When(x => !string.IsNullOrEmpty(x.Telefone));
    }
}
