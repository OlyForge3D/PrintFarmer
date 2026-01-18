using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Email;

public sealed class ConsoleEmailService(IUnifiedLoggingService logger) : IEmailService
{
    private readonly IUnifiedLoggingService _logger = logger;

    public Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        _logger.LogInformation(
            $"[EMAIL:CONSOLE] To={message.To} Subject={message.Subject}",
            null,
            null
        );

#pragma warning disable CA1303 // Console logging strings don't require localization
        Console.WriteLine("[EMAIL:CONSOLE]");
        Console.WriteLine($"  To: {message.To}");
        Console.WriteLine($"  Subject: {message.Subject}");
        Console.WriteLine($"  Body (plain): {message.PlainBody}");
        Console.WriteLine($"  Body (html): {message.HtmlBody}");
#pragma warning restore CA1303

        return Task.FromResult(new EmailDispatchResult(true, "Email logged to console"));
    }

    public async Task<bool> SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default)
    {
        var renderer = new EmailTemplateRenderer();
        var (subject, plain, html) = renderer.Render("PasswordReset", new Dictionary<string, string>
        {
            ["ResetLink"] = resetLink,
            ["ExpirationMinutes"] = "60"
        });

        var message = new EmailMessage(email, subject, plain, html);
        var result = await SendAsync(message, ct);
        return result.Success;
    }

    public async Task<bool> SendEmailConfirmationAsync(string email, string confirmationLink, CancellationToken ct = default)
    {
        var renderer = new EmailTemplateRenderer();
        var (subject, plain, html) = renderer.Render("EmailConfirmation", new Dictionary<string, string>
        {
            ["ConfirmationLink"] = confirmationLink,
            ["ExpirationHours"] = "24"
        });

        var message = new EmailMessage(email, subject, plain, html);
        var result = await SendAsync(message, ct);
        return result.Success;
    }
}
