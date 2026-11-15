using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// HTTP client for the documented PrintGuard classification endpoint.
/// It only submits frames for scoring and never invokes printer-control routes.
/// </summary>
public sealed class PrintGuardFailureDetectionProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<PrintGuardFailureDetectionProvider> logger) : IPrintGuardFailureDetectionProvider
{
    private const int MaximumImageBytes = 32 * 1024 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ILogger<PrintGuardFailureDetectionProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
    public string ProviderName => "PrintGuard";

    /// <inheritdoc/>
    public async Task<FailureDetectionProviderResult> AnalyzeAsync(
        FailureDetectionProviderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ImageData is not { Length: > 0 and <= MaximumImageBytes })
        {
            return Error("PrintGuard requires a non-empty image no larger than 32 MB.");
        }

        if (!FailureDetectionImageValidator.IsJpeg(request.ImageData))
        {
            return Error("PrintGuard requires a JPEG image.");
        }

        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return Error("PrintGuard classification endpoint is invalid.");
        }

        try
        {
            using HttpClient client = _httpClientFactory.CreateClient("PrintGuard");
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            using var content = new ByteArrayContent(request.ImageData);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            message.Content = content;

            if (!string.IsNullOrWhiteSpace(request.ApiToken))
            {
                if (request.ApiToken.Any(char.IsWhiteSpace))
                {
                    return Error("PrintGuard API token is invalid.");
                }

                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiToken);
            }

            using HttpResponseMessage response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[PrintGuard] Classification request returned HTTP {StatusCode}",
                    (int)response.StatusCode);
                return Error($"PrintGuard API returned HTTP {(int)response.StatusCode}.");
            }

            byte[] responseBody = await BoundedHttpContentReader.ReadAsByteArrayAsync(
                response.Content,
                MaximumResponseBytes,
                ct);
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("defect_score", out JsonElement scoreElement)
                || scoreElement.ValueKind != JsonValueKind.Number
                || !scoreElement.TryGetDecimal(out decimal score)
                || score is < 0m or > 1m)
            {
                return Error("PrintGuard response did not contain a normalized defect_score.");
            }

            return new FailureDetectionProviderResult(
                score,
                IsFailureDetected: null,
                AnalyzedAt: DateTime.UtcNow,
                UsedCapturedImage: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("[PrintGuard] Classification request timed out");
            return Error("PrintGuard classification request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[PrintGuard] Classification request failed");
            return Error("PrintGuard classification request failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[PrintGuard] Classification response was invalid JSON");
            return Error("PrintGuard returned invalid JSON.");
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "[PrintGuard] Classification response exceeded the size limit");
            return Error("PrintGuard classification response exceeded the 64 KB maximum size.");
        }
    }

    private static FailureDetectionProviderResult Error(string message)
    {
        return new FailureDetectionProviderResult(
            Score: 0m,
            IsFailureDetected: null,
            AnalyzedAt: DateTime.UtcNow,
            ErrorMessage: message);
    }
}
