using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Email;

public sealed class MailjetEmailService : IEmailService
{
    private const string MailjetApiBaseUrl = "https://api.mailjet.com/v3.1/send";

    private readonly ILogger<MailjetEmailService> _logger;
    private readonly EmailOptions _options;
    private readonly IEmailTemplateRenderer _renderer;
    private readonly IHttpClientFactory _httpClientFactory;

    public MailjetEmailService(ILogger<MailjetEmailService> logger, EmailOptions options, IEmailTemplateRenderer renderer, IHttpClientFactory httpClientFactory)
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
            _logger.LogInformation("[EMAIL:FALLBACK] To={MessageTo} Subject={MessageSubject}", SensitiveDataMasking.MaskEmail(message.To), message.Subject);
            return new EmailDispatchResult(Success: true, ProviderMessage: "Missing API keys - logged only");
        }

        using HttpClient http = _httpClientFactory.CreateClient("Mailjet");

        string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.Mailjet.ApiKey + ":" + _options.Mailjet.ApiSecret));

        string fromEmail = _options.FromAddress ?? _options.Mailjet.FromEmail ?? "noreply@printfarmer.local";
        string fromName = _options.FromName ?? _options.Mailjet.FromName ?? "PrintFarmer";
        bool sandbox = _options.Mailjet.Sandbox;

        if (sandbox)
        {
            _logger.LogInformation("Sending email to {MessageTo} in Mailjet sandbox mode (not actually delivered)", SensitiveDataMasking.MaskEmail(message.To));
        }

        // Build payload per Mailjet v3.1 API
        var payload = new
        {
            SandboxMode = sandbox,
            Messages = new[]
            {
                new
                {
                    From = new { Email = fromEmail, Name = fromName },
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
                _logger.LogInformation("Email sent successfully to {MessageTo}", SensitiveDataMasking.MaskEmail(message.To));
                return new EmailDispatchResult(Success: true, ProviderMessage: "Email sent");
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Mailjet API error: {StatusCode} - {ErrorContent}", response.StatusCode, errorContent);
                return new EmailDispatchResult(Success: false, Error: $"API error: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {MessageTo}: {Message}", SensitiveDataMasking.MaskEmail(message.To), ex.Message);
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
