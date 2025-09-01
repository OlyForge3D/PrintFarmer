using Farm.Web.Shared;
using FluentValidation;

namespace Farm.Web.Api.Validators;

/// <summary>
/// Validates printer creation requests to ensure data integrity and security
/// </summary>
public class CreatePrinterValidator : AbstractValidator<CreatePrinterDto>
{
    public CreatePrinterValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Printer name is required")
            .Length(1, 100).WithMessage("Printer name must be between 1 and 100 characters")
            .Matches("^[a-zA-Z0-9\\s\\-_\\.]+$").WithMessage("Printer name contains invalid characters");

        RuleFor(x => x.ServerUrl)
            .NotEmpty().WithMessage("Server URL is required")
            .Must(BeValidUrl).WithMessage("Server URL must be a valid HTTP/HTTPS URL")
            .Must(NotContainSqlInjectionPatterns).WithMessage("Server URL contains potentially harmful content");

        RuleFor(x => x.ApiKey)
            .Length(0, 500).WithMessage("API Key cannot exceed 500 characters")
            .When(x => !string.IsNullOrEmpty(x.ApiKey));

        RuleFor(x => x.Notes)
            .Length(0, 1000).WithMessage("Notes cannot exceed 1000 characters")
            .When(x => !string.IsNullOrEmpty(x.Notes));

        RuleFor(x => x.Backend)
            .IsInEnum().WithMessage("Invalid printer backend specified");
    }

    private static bool BeValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            var uri = new Uri(url);
            return uri.Scheme is "http" or "https";
        }
        catch
        {
            return false;
        }
    }

    private static bool NotContainSqlInjectionPatterns(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return true;
        }

        var dangerousPatterns = new[]
        {
            "script", "javascript:", "vbscript:", "onload", "onerror",
            "drop table", "select *", "insert into", "delete from",
            "union select", "exec(", "execute("
        };

        return !dangerousPatterns.Any(pattern =>
            input.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
