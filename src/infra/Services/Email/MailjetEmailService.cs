using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Email;

public sealed class MailjetEmailService : IEmailService
{
    private const string MailjetApiBaseUrl = "https://api.mailjet.com/v3.1/send";

    private readonly IUnifiedLoggingService _logger;
    private readonly EmailOptions _options;
    private readonly IEmailTemplateRenderer _renderer;
    private readonly IHttpClientFactory _httpClientFactory;

    public MailjetEmailService(IUnifiedLoggingService logger, EmailOptions options, IEmailTemplateRenderer renderer, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _options = options;
        _renderer = renderer;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Mailjet?.ApiKey) || string.IsNullOrWhiteSpace(_options.Mailjet?.ApiSecret))
        {
            _logger.LogWarning("Mailjet API keys missing. Email logged only.");
            _logger.LogInformation($"[EMAIL:FALLBACK] To={message.To} Subject={message.Subject}");
            return new EmailDispatchResult(Success: true, ProviderMessage: "Missing API keys - logged only");
        }

        using HttpClient http = _httpClientFactory.CreateClient("Mailjet");

        string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.Mailjet.ApiKey + ":" + _options.Mailjet.ApiSecret));

        // Build payload per Mailjet v3.1 API
        var payload = new
        {
            Messages = new[]
            {
                new
                {
                    From = new { Email = _options.Mailjet.FromEmail ?? "noreply@printfarmer.local", Name = _options.Mailjet.FromName ?? "PrintFarmer" },
                    To = new[] { new { Email = message.To } },
                    Subject = message.Subject,
                    TextPart = message.PlainBody,
                    HTMLPart = message.HtmlBody
                }
            }
        };

        string json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, MailjetApiBaseUrl)
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

            HttpResponseMessage response = await http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation($"Email sent successfully to {message.To}");
                return new EmailDispatchResult(Success: true, ProviderMessage: "Email sent");
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError($"Mailjet API error: {response.StatusCode} - {errorContent}", null, null);
                return new EmailDispatchResult(Success: false, Error: $"API error: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send email to {message.To}: {ex.Message}");
            return new EmailDispatchResult(Success: false, Error: $"Exception: {ex.Message}");
        }
    }

    public async Task<bool> SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default)
    {
        (string? subject, string? plain, string? html) = _renderer.Render("PasswordReset", new Dictionary<string, string>
        {
            ["ResetLink"] = resetLink,
            ["ExpirationMinutes"] = "60"
        });

        var message = new EmailMessage(email, subject, plain, html);
        EmailDispatchResult result = await SendAsync(message, ct);
        return result.Success;
    }

    public async Task<bool> SendEmailConfirmationAsync(string email, string confirmationLink, CancellationToken ct = default)
    {
        (string? subject, string? plain, string? html) = _renderer.Render("EmailConfirmation", new Dictionary<string, string>
        {
            ["ConfirmationLink"] = confirmationLink,
            ["ExpirationHours"] = "24"
        });

        var message = new EmailMessage(email, subject, plain, html);
        EmailDispatchResult result = await SendAsync(message, ct);
        return result.Success;
    }
}
