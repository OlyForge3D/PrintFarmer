using Farm.Infrastructure.Services.Tasks;
using FluentValidation;

namespace Farm.Web.Api.Validators;

/// <summary>
/// Validates manual task creation requests.
/// </summary>
public class CreateManualTaskValidator : AbstractValidator<CreateManualTaskDto>
{
    public CreateManualTaskValidator()
    {
        _ = RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Task title is required")
            .Length(1, 200).WithMessage("Task title must be between 1 and 200 characters");

        _ = RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Task description cannot exceed 1000 characters")
            .When(x => !string.IsNullOrEmpty(x.Description));

        _ = RuleFor(x => x.Priority)
            .IsInEnum().WithMessage("Invalid priority specified");
    }
}
