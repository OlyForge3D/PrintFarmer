using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Service for AI-powered print failure detection using the Obico ML API.
/// Submits camera snapshots to the Obico ML server and interprets confidence scores.
/// </summary>
public sealed class ObicoFailureDetectionService : IObicoFailureDetectionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ObicoFailureDetectionService> _logger;
    private readonly ObicoSettings _settings;

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);

    public ObicoFailureDetectionService(
        IHttpClientFactory httpClientFactory,
        IOptions<ObicoSettings> settings,
        ILogger<ObicoFailureDetectionService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<FailureDetectionResult> AnalyzeImageAsync(byte[] imageData, CancellationToken ct = default)
    {
        return AnalyzeImageAsync(imageData, _settings.ObicoApiUrl, ct);
    }

    /// <inheritdoc/>
    public async Task<FailureDetectionResult> AnalyzeImageAsync(byte[] imageData, string obicoServerUrl, CancellationToken ct = default)
    {
        if (imageData == null || imageData.Length == 0)
        {
            return FailureDetectionResult.Error("Image data is empty");
        }

        if (string.IsNullOrWhiteSpace(obicoServerUrl))
        {
            return FailureDetectionResult.Error("Obico server URL is not configured");
        }

        try
        {
            using HttpClient httpClient = _httpClientFactory.CreateClient("ObicoML");
            httpClient.Timeout = HttpTimeout;
            httpClient.BaseAddress = new Uri(obicoServerUrl);

            using var content = new MultipartFormDataContent();
            using var imageContent = new ByteArrayContent(imageData);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(imageContent, "img", "snapshot.jpg");

            _logger.LogDebug(
                "[ObicoFailureDetection] Submitting {Size} byte image to {ApiUrl}/p/",
                imageData.Length, obicoServerUrl);

            HttpResponseMessage response = await httpClient.PostAsync("/p/", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "[ObicoFailureDetection] API returned {StatusCode}: {Error}",
                    response.StatusCode, errorBody);
                return FailureDetectionResult.Error($"API error: HTTP {(int)response.StatusCode}");
            }

            string responseBody = await response.Content.ReadAsStringAsync(ct);
            var apiResponse = JsonSerializer.Deserialize<ObicoApiResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (apiResponse?.Result?.P == null)
            {
                _logger.LogWarning("[ObicoFailureDetection] Invalid API response: {Response}", responseBody);
                return FailureDetectionResult.Error("Invalid API response format");
            }

            decimal confidence = (decimal)apiResponse.Result.P;
            bool isFailure = confidence >= _settings.ConfidenceThreshold;

            _logger.LogInformation(
                "[ObicoFailureDetection] Analysis complete: confidence={Confidence:F3}, threshold={Threshold:F3}, failure={IsFailure}",
                confidence, _settings.ConfidenceThreshold, isFailure);

            return FailureDetectionResult.Success(confidence, isFailure);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[ObicoFailureDetection] HTTP request failed");
            return FailureDetectionResult.Error($"HTTP error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[ObicoFailureDetection] Request timeout");
            return FailureDetectionResult.Error("Request timeout");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[ObicoFailureDetection] Failed to parse API response");
            return FailureDetectionResult.Error("Invalid JSON response");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ObicoFailureDetection] Unexpected error during analysis");
            return FailureDetectionResult.Error($"Unexpected error: {ex.GetType().Name}");
        }
    }

    /// <inheritdoc/>
    public Task<FailureDetectionResult> AnalyzeImageFromUrlAsync(string snapshotUrl, CancellationToken ct = default)
    {
        return AnalyzeImageFromUrlAsync(snapshotUrl, _settings.ObicoApiUrl, ct);
    }

    /// <inheritdoc/>
    public async Task<FailureDetectionResult> AnalyzeImageFromUrlAsync(string snapshotUrl, string obicoServerUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotUrl))
        {
            return FailureDetectionResult.Error("Snapshot URL is empty");
        }

        if (string.IsNullOrWhiteSpace(obicoServerUrl))
        {
            return FailureDetectionResult.Error("Obico server URL is not configured");
        }

        try
        {
            using HttpClient httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = HttpTimeout;

            _logger.LogDebug("[ObicoFailureDetection] Fetching image from {SnapshotUrl}", snapshotUrl);

            HttpResponseMessage response = await httpClient.GetAsync(snapshotUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[ObicoFailureDetection] Failed to fetch snapshot: {StatusCode}",
                    response.StatusCode);
                return FailureDetectionResult.Error($"Failed to fetch snapshot: HTTP {(int)response.StatusCode}");
            }

            byte[] imageData = await response.Content.ReadAsByteArrayAsync(ct);

            if (imageData.Length == 0)
            {
                return FailureDetectionResult.Error("Fetched image is empty");
            }

            return await AnalyzeImageAsync(imageData, obicoServerUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[ObicoFailureDetection] Failed to fetch snapshot from {SnapshotUrl}", snapshotUrl);
            return FailureDetectionResult.Error($"Failed to fetch snapshot: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[ObicoFailureDetection] Snapshot fetch timeout from {SnapshotUrl}", snapshotUrl);
            return FailureDetectionResult.Error("Snapshot fetch timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ObicoFailureDetection] Unexpected error fetching snapshot");
            return FailureDetectionResult.Error($"Unexpected error: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Obico ML API response format.
    /// Example: {"result": {"p": 0.85}}
    /// </summary>
#pragma warning disable S3459, S1144 // JSON deserialization DTOs — properties populated by System.Text.Json
    private sealed class ObicoApiResponse
    {
        public ObicoResult? Result { get; init; }
    }

    private sealed class ObicoResult
    {
        public double? P { get; init; }
    }
#pragma warning restore S3459, S1144
}
