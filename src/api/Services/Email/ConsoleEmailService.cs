using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.Email;

public sealed class ConsoleEmailService(IUnifiedLoggingService logger, IEmailTemplateRenderer renderer) : IEmailService
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IEmailTemplateRenderer _renderer = renderer;

    public Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var meta = new { message.HtmlBody, message.PlainBody, message.TemplateKey };
        _logger.LogInformation($"[EMAIL:CONSOLE] To={message.To} Subject={message.Subject} Template={message.TemplateKey}", null, meta);
        return Task.FromResult(new EmailDispatchResult(true, "Logged"));
    }

    public async Task<bool> SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default)
    {
        var model = new Dictionary<string, string>
        {
            ["ResetLink"] = resetLink,
            ["ExpirationMinutes"] = "60"
        };
        var (subject, plain, html) = _renderer.Render("PasswordReset", model);
        await SendAsync(new EmailMessage(email, subject, plain, html, "PasswordReset"), ct);
        return true;
    }

    public async Task<bool> SendEmailConfirmationAsync(string email, string confirmationLink, CancellationToken ct = default)
    {
        var model = new Dictionary<string, string>
        {
            ["ConfirmationLink"] = confirmationLink,
            ["ExpirationHours"] = "24"
        };
        var (subject, plain, html) = _renderer.Render("EmailConfirmation", model);
        await SendAsync(new EmailMessage(email, subject, plain, html, "EmailConfirmation"), ct);
        return true;
    }
}
