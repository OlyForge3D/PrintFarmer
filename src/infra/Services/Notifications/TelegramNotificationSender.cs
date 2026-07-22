using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Sends messages to the Telegram Bot API.
/// </summary>
public interface ITelegramNotificationSender
{
    Task<TelegramDispatchResult> SendMessageAsync(
        string botToken,
        string chatId,
        string text,
        CancellationToken cancellationToken);

    Task<TelegramDispatchResult> SendPhotoAsync(
        string botToken,
        string chatId,
        string caption,
        byte[] photo,
        string contentType,
        CancellationToken cancellationToken);
}

/// <summary>Result of a Telegram Bot API delivery attempt.</summary>
public sealed record TelegramDispatchResult(bool Success, string? Error = null);

/// <summary>
/// HTTP-based Telegram Bot API sender with retry behavior for transient provider failures.
/// </summary>
public sealed class TelegramNotificationSender(
    IHttpClientFactory httpClientFactory,
    ILogger<TelegramNotificationSender> logger,
    IReadOnlyList<TimeSpan>? retryDelays = null) : ITelegramNotificationSender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30)
    ];

    private const int TelegramMaxTextLength = 4096;
    private const int TelegramMaxCaptionLength = 1024;

    private readonly IReadOnlyList<TimeSpan> retryDelays = retryDelays ?? DefaultRetryDelays;

    public Task<TelegramDispatchResult> SendMessageAsync(
        string botToken,
        string chatId,
        string text,
        CancellationToken cancellationToken)
    {
        return SendWithRetriesAsync(
            botToken,
            "sendMessage",
            () =>
            {
                var payload = new
                {
                    chat_id = chatId,
                    text = Truncate(text, TelegramMaxTextLength),
                    disable_web_page_preview = true
                };
                string json = JsonSerializer.Serialize(payload, JsonOptions);
                return new StringContent(json, Encoding.UTF8, "application/json");
            },
            cancellationToken);
    }

    public Task<TelegramDispatchResult> SendPhotoAsync(
        string botToken,
        string chatId,
        string caption,
        byte[] photo,
        string contentType,
        CancellationToken cancellationToken)
    {
        return SendWithRetriesAsync(
            botToken,
            "sendPhoto",
            () =>
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent(chatId, Encoding.UTF8), "chat_id" },
                    { new StringContent(Truncate(caption, TelegramMaxCaptionLength), Encoding.UTF8), "caption" }
                };
                var photoContent = new ByteArrayContent(photo);
                photoContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                form.Add(photoContent, "photo", "snapshot.jpg");
                return form;
            },
            cancellationToken);
    }

    private async Task<TelegramDispatchResult> SendWithRetriesAsync(
        string botToken,
        string method,
        Func<HttpContent> contentFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new TelegramDispatchResult(false, "Telegram bot token is not configured.");
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            return new TelegramDispatchResult(false, "Telegram API method is not configured.");
        }

        int maxAttempts = retryDelays.Count + 1;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using HttpClient client = httpClientFactory.CreateClient("TelegramDelivery");
                using var request = new HttpRequestMessage(HttpMethod.Post, BuildTelegramUri(botToken, method))
                {
                    Content = contentFactory()
                };

                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return new TelegramDispatchResult(true);
                }

                string error = $"HTTP {(int)response.StatusCode}";
                if (!ShouldRetry(response.StatusCode) || attempt == maxAttempts)
                {
                    return new TelegramDispatchResult(false, error);
                }

                logger.LogWarning(
                    "Telegram delivery attempt {Attempt} for method {Method} failed with {StatusCode}; retrying.",
                    attempt,
                    method,
                    (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    return new TelegramDispatchResult(false, "Telegram delivery failed.");
                }

                logger.LogWarning(
                    "Telegram delivery attempt {Attempt} for method {Method} failed with {ErrorType}; retrying.",
                    attempt,
                    method,
                    ex.GetType().Name);
            }

            await Task.Delay(retryDelays[attempt - 1], cancellationToken);
        }

        return new TelegramDispatchResult(false, "Telegram delivery retries exhausted.");
    }

    private static Uri BuildTelegramUri(string botToken, string method)
    {
        return new Uri($"https://api.telegram.org/bot{botToken}/{method}", UriKind.Absolute);
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
