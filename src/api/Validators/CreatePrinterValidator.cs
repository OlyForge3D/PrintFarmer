using Farm.Infrastructure;
using FluentValidation;

namespace Farm.Web.Api.Validators;

/// <summary>
/// Validates printer creation requests to ensure data integrity and security
/// </summary>
public class CreatePrinterValidator : AbstractValidator<CreatePrinterDto>
{
    public CreatePrinterValidator()
    {
        _ = RuleFor(x => x.CameraStreamUrl)
            .MaximumLength(500).WithMessage("Camera stream URL cannot exceed 500 characters")
            .Must(BeValidUrl).WithMessage("Camera stream URL must be a valid HTTP/HTTPS URL")
            .When(x => !string.IsNullOrWhiteSpace(x.CameraStreamUrl));

        _ = RuleFor(x => x.CameraSnapshotUrl)
            .MaximumLength(500).WithMessage("Camera snapshot URL cannot exceed 500 characters")
            .Must(BeValidUrl).WithMessage("Camera snapshot URL must be a valid HTTP/HTTPS URL")
            .When(x => !string.IsNullOrWhiteSpace(x.CameraSnapshotUrl));

        _ = RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Printer name is required")
            .Length(1, 100).WithMessage("Printer name must be between 1 and 100 characters")
            .Matches("^[a-zA-Z0-9\\s\\-_\\.]+$").WithMessage("Printer name contains invalid characters");

        _ = RuleFor(x => x.ServerUrl)
            .NotEmpty().WithMessage("Server URL is required")
            .Must(BeValidUrl).WithMessage("Server URL must be a valid HTTP/HTTPS URL")
            .Must(NotContainSqlInjectionPatterns).WithMessage("Server URL contains potentially harmful content");

        _ = RuleFor(x => x.ApiKey)
            .Length(0, 500).WithMessage("API Key cannot exceed 500 characters")
            .When(x => !string.IsNullOrEmpty(x.ApiKey) || x.Backend == PrinterBackend.OctoPrint)
            .NotEmpty().WithMessage("API Key is required for OctoPrint printers")
            .When(x => x.Backend == PrinterBackend.OctoPrint);

        _ = RuleFor(x => x.Notes)
            .Length(0, 1000).WithMessage("Notes cannot exceed 1000 characters")
            .When(x => !string.IsNullOrEmpty(x.Notes));

        _ = RuleFor(x => x.Backend)
            .IsInEnum().WithMessage("Invalid printer backend specified");

        _ = RuleFor(x => x.DateAcquired)
            .LessThanOrEqualTo(DateTime.Today.AddDays(1)).WithMessage("Date acquired cannot be in the future")
            .When(x => x.DateAcquired.HasValue);
    }

    private static bool BeValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            Uri uri = new(url);
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

        string[] dangerousPatterns = new[]
        {
            "script", "javascript:", "vbscript:", "onload", "onerror",
            "drop table", "select *", "insert into", "delete from",
            "union select", "exec(", "execute("
        };

        return !Array.Exists(dangerousPatterns, pattern =>
            input.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
