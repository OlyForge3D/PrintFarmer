using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.Email;

public sealed class MailjetEmailService : IEmailService, IDisposable
{
    private const string MailjetApiBaseUrl = "https://api.mailjet.com/v3.1/send";
    
    private readonly IUnifiedLoggingService _logger;
    private readonly EmailOptions _options;
    private readonly IEmailTemplateRenderer _renderer;
    private readonly HttpClient _http;
    private bool _disposed;

    public MailjetEmailService(IUnifiedLoggingService logger, EmailOptions options, IEmailTemplateRenderer renderer)
    {
        _logger = logger;
        _options = options;
        _renderer = renderer;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Mailjet?.ApiKey) || string.IsNullOrWhiteSpace(_options.Mailjet?.ApiSecret))
        {
            _logger.LogWarning("Mailjet API keys missing. Email logged only.");
            _logger.LogInformation($"[EMAIL:FALLBACK] To={message.To} Subject={message.Subject}");
            return new EmailDispatchResult(true, "Missing API keys - logged only");
        }

        string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.Mailjet.ApiKey + ":" + _options.Mailjet.ApiSecret));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

        // Build payload per Mailjet v3.1 API
        var payload = new
        {
            Messages = new[]
            {
                new {
                    From = new { Email = _options.FromAddress, Name = _options.FromName },
                    To = new[] { new { Email = message.To } },
                    Subject = message.Subject,
                    TextPart = message.PlainBody ?? string.Empty,
                    HTMLPart = message.HtmlBody ?? string.Empty,
                    SandboxMode = _options.Mailjet!.Sandbox
                }
            }
        };

        string json = JsonSerializer.Serialize(payload);
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        try
        {
            // Mailjet v3.1 send endpoint
            using HttpResponseMessage resp = await _http.PostAsync(MailjetApiBaseUrl, content, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation($"Mailjet email sent to {message.To}. Status={(int)resp.StatusCode}");
                return new EmailDispatchResult(true, ProviderMessage: body);
            }
            _logger.LogWarning($"Mailjet email failure Status={(int)resp.StatusCode} Body={body}");
            return new EmailDispatchResult(false, Error: body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mailjet send exception", null, null);
            return new EmailDispatchResult(false, Error: ex.Message);
        }
    }

    public async Task<bool> SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default)
    {
        var model = new Dictionary<string, string>
        {
            ["ResetLink"] = resetLink,
            ["ExpirationMinutes"] = "60"
        };
        var (subject, plain, html) = _renderer.Render("PasswordReset", model);
        var result = await SendAsync(new EmailMessage(email, subject, plain, html, "PasswordReset"), ct);
        return result.Success;
    }

    public async Task<bool> SendEmailConfirmationAsync(string email, string confirmationLink, CancellationToken ct = default)
    {
        var model = new Dictionary<string, string>
        {
            ["ConfirmationLink"] = confirmationLink,
            ["ExpirationHours"] = "24"
        };
        var (subject, plain, html) = _renderer.Render("EmailConfirmation", model);
        var result = await SendAsync(new EmailMessage(email, subject, plain, html, "EmailConfirmation"), ct);
        return result.Success;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _http.Dispose();
        _disposed = true;
    }
}
