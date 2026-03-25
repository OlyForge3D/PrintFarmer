using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Service for AI-powered print failure detection using Obico-compatible ML APIs.
/// Prefers the upstream self-hosted snapshot URL contract and falls back to the legacy multipart upload contract.
/// </summary>
public sealed class ObicoFailureDetectionService : IObicoFailureDetectionService
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] DetectionConfidencePropertyNames = ["confidence", "score", "probability", "p"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ObicoFailureDetectionService> _logger;
    private readonly ISettingsService _settingsService;

    public ObicoFailureDetectionService(
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService,
        ILogger<ObicoFailureDetectionService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<FailureDetectionResult> AnalyzeImageAsync(byte[] imageData, CancellationToken ct = default)
    {
        string obicoApiUrl = _settingsService.Get<ObicoSettings>().ObicoApiUrl;
        return AnalyzeImageAsync(imageData, obicoApiUrl, apiKey: null, ct);
    }

    /// <inheritdoc/>
    public Task<FailureDetectionResult> AnalyzeImageAsync(byte[] imageData, string obicoServerUrl, string? apiKey = null, CancellationToken ct = default)
    {
        return AnalyzeImageAsync(
            imageData,
            obicoServerUrl,
            apiKey,
            treatLegacyUploadMismatchAsCompatibilityError: false,
            ct: ct);
    }

    /// <summary>
    /// Analyzes image data against the legacy multipart contract and optionally upgrades contract-mismatch failures
    /// into actionable configuration errors when it is being used as a fallback after the upstream contract failed.
    /// </summary>
    private async Task<FailureDetectionResult> AnalyzeImageAsync(
        byte[] imageData,
        string obicoServerUrl,
        string? apiKey,
        bool treatLegacyUploadMismatchAsCompatibilityError,
        CancellationToken ct)
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
            using HttpClient httpClient = CreateObicoClient(obicoServerUrl, apiKey);
            using var content = new MultipartFormDataContent();
            using var imageContent = new ByteArrayContent(imageData);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(imageContent, "img", "snapshot.jpg");

            _logger.LogDebug(
                "[ObicoFailureDetection] Uploading {Size} byte image to legacy contract at {ApiUrl}/p/",
                imageData.Length,
                obicoServerUrl);

            HttpResponseMessage response = await httpClient.PostAsync("p/", content, ct);
            (bool handled, FailureDetectionResult result) parsedResponse = await ParseResponseAsync(
                response,
                obicoServerUrl,
                allowLegacyFallback: false,
                treatLegacyUploadMismatchAsCompatibilityError: treatLegacyUploadMismatchAsCompatibilityError,
                ct: ct);
            return parsedResponse.result;
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
        string obicoApiUrl = _settingsService.Get<ObicoSettings>().ObicoApiUrl;
        return AnalyzeImageFromUrlAsync(snapshotUrl, obicoApiUrl, apiKey: null, ct);
    }

    /// <inheritdoc/>
    public async Task<FailureDetectionResult> AnalyzeImageFromUrlAsync(string snapshotUrl, string obicoServerUrl, string? apiKey = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotUrl))
        {
            return FailureDetectionResult.Error("Snapshot URL is empty");
        }

        if (string.IsNullOrWhiteSpace(obicoServerUrl))
        {
            return FailureDetectionResult.Error("Obico server URL is not configured");
        }

        (bool handled, FailureDetectionResult result) upstreamResult = await TryAnalyzeSnapshotUrlAsync(snapshotUrl, obicoServerUrl, apiKey, ct);
        if (upstreamResult.handled)
        {
            return upstreamResult.result;
        }

        return await FetchSnapshotAndAnalyzeLegacyAsync(snapshotUrl, obicoServerUrl, apiKey, ct);
    }

    /// <summary>
    /// Tries the upstream self-hosted contract (`GET /p/?img=...`) before falling back to the legacy upload flow.
    /// </summary>
    private async Task<(bool handled, FailureDetectionResult result)> TryAnalyzeSnapshotUrlAsync(
        string snapshotUrl,
        string obicoServerUrl,
        string? apiKey,
        CancellationToken ct)
    {
        try
        {
            using HttpClient httpClient = CreateObicoClient(obicoServerUrl, apiKey);
            string requestPath = $"p/?img={Uri.EscapeDataString(snapshotUrl)}";

            _logger.LogDebug(
                "[ObicoFailureDetection] Requesting snapshot URL analysis from {ApiUrl}/{RequestPath}",
                obicoServerUrl.TrimEnd('/'),
                requestPath);

            HttpResponseMessage response = await httpClient.GetAsync(requestPath, ct);
            return await ParseResponseAsync(
                response,
                obicoServerUrl,
                allowLegacyFallback: true,
                treatLegacyUploadMismatchAsCompatibilityError: false,
                ct: ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[ObicoFailureDetection] HTTP request failed for snapshot URL analysis");
            return (true, FailureDetectionResult.Error($"HTTP error: {ex.Message}"));
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[ObicoFailureDetection] Snapshot URL analysis request timeout");
            return (true, FailureDetectionResult.Error("Request timeout"));
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[ObicoFailureDetection] Failed to parse snapshot URL analysis response");
            return (true, FailureDetectionResult.Error("Invalid JSON response"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ObicoFailureDetection] Unexpected error during snapshot URL analysis");
            return (true, FailureDetectionResult.Error($"Unexpected error: {ex.GetType().Name}"));
        }
    }

    /// <summary>
    /// Fetches the snapshot locally and retries the legacy multipart upload contract for backward compatibility.
    /// </summary>
    private async Task<FailureDetectionResult> FetchSnapshotAndAnalyzeLegacyAsync(
        string snapshotUrl,
        string obicoServerUrl,
        string? apiKey,
        CancellationToken ct)
    {
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

            return await AnalyzeImageAsync(
                imageData,
                obicoServerUrl,
                apiKey,
                treatLegacyUploadMismatchAsCompatibilityError: true,
                ct: ct);
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
    /// Parses successful Obico responses and detects when the legacy multipart upload fallback should be attempted.
    /// </summary>
    private async Task<(bool handled, FailureDetectionResult result)> ParseResponseAsync(
        HttpResponseMessage response,
        string obicoServerUrl,
        bool allowLegacyFallback,
        bool treatLegacyUploadMismatchAsCompatibilityError,
        CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct);
            if (allowLegacyFallback && ShouldFallbackToLegacyUpload(response.StatusCode))
            {
                _logger.LogInformation(
                    "[ObicoFailureDetection] Snapshot URL contract unavailable at {ApiUrl}/p/ (HTTP {StatusCode}); falling back to legacy upload",
                    obicoServerUrl,
                    (int)response.StatusCode);
                return (false, FailureDetectionResult.Error("Legacy fallback requested"));
            }

            if (treatLegacyUploadMismatchAsCompatibilityError && ShouldFallbackToLegacyUpload(response.StatusCode))
            {
                _logger.LogWarning(
                    "[ObicoFailureDetection] Legacy upload contract unavailable at {ApiUrl}/p/ (HTTP {StatusCode}): {Error}",
                    obicoServerUrl,
                    response.StatusCode,
                    errorBody);
                return (true, FailureDetectionResult.Error(CreateUnsupportedPredictionContractMessage(response.StatusCode)));
            }

            _logger.LogWarning(
                "[ObicoFailureDetection] API returned {StatusCode}: {Error}",
                response.StatusCode,
                errorBody);
            return (true, FailureDetectionResult.Error($"API error: HTTP {(int)response.StatusCode}"));
        }

        string responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!TryParseConfidence(responseBody, out decimal confidence))
        {
            if (allowLegacyFallback)
            {
                _logger.LogInformation(
                    "[ObicoFailureDetection] Snapshot URL contract response from {ApiUrl}/p/ was not recognized; falling back to legacy upload",
                    obicoServerUrl);
                return (false, FailureDetectionResult.Error("Legacy fallback requested"));
            }

            _logger.LogWarning("[ObicoFailureDetection] Invalid API response: {Response}", responseBody);
            return (true, FailureDetectionResult.Error("Invalid API response format"));
        }

        return (true, CreateSuccessResult(confidence));
    }

    /// <summary>
    /// Builds an actionable user-facing error when neither the upstream snapshot contract nor the legacy upload
    /// fallback are available at the configured Obico URL.
    /// </summary>
    private static string CreateUnsupportedPredictionContractMessage(HttpStatusCode legacyStatusCode)
    {
        return
            $"Configured Obico server is not exposing a supported prediction route (legacy POST /p/ returned HTTP {(int)legacyStatusCode}). " +
            "Check that the URL points to the Obico ML API root that supports upstream GET /p/?img=... or legacy POST /p/.";
    }

    /// <summary>
    /// Builds a configured Obico HTTP client for either upstream GET or legacy multipart requests.
    /// </summary>
    private HttpClient CreateObicoClient(string obicoServerUrl, string? apiKey)
    {
        HttpClient httpClient = _httpClientFactory.CreateClient("ObicoML");
        httpClient.Timeout = HttpTimeout;
        httpClient.BaseAddress = new Uri(obicoServerUrl.TrimEnd('/') + "/");

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        return httpClient;
    }

    /// <summary>
    /// Maps either the upstream `detections` payload or the legacy `result.p` payload to a confidence score.
    /// </summary>
    private static bool TryParseConfidence(string responseBody, out decimal confidence)
    {
        confidence = 0m;
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;

            if (TryGetPropertyIgnoreCase(root, "result", out JsonElement resultElement) &&
                TryGetPropertyIgnoreCase(resultElement, "p", out JsonElement legacyConfidenceElement) &&
                TryReadNormalizedConfidence(legacyConfidenceElement, out confidence))
            {
                return true;
            }

            if (!TryGetPropertyIgnoreCase(root, "detections", out JsonElement detectionsElement) ||
                detectionsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            bool sawConfidence = false;
            decimal maxConfidence = 0m;
            foreach (JsonElement detection in detectionsElement.EnumerateArray())
            {
                if (!TryParseDetectionConfidence(detection, out decimal detectionConfidence))
                {
                    continue;
                }

                sawConfidence = true;
                if (detectionConfidence > maxConfidence)
                {
                    maxConfidence = detectionConfidence;
                }
            }

            if (detectionsElement.GetArrayLength() == 0)
            {
                confidence = 0m;
                return true;
            }

            if (!sawConfidence)
            {
                return false;
            }

            confidence = maxConfidence;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts a confidence score from either tuple-style or object-style detection items.
    /// </summary>
    private static bool TryParseDetectionConfidence(JsonElement detection, out decimal confidence)
    {
        confidence = 0m;

        if (detection.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement element in detection.EnumerateArray())
            {
                if (index == 1)
                {
                    return TryReadNormalizedConfidence(element, out confidence);
                }

                index++;
            }

            return false;
        }

        if (detection.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (string propertyName in DetectionConfidencePropertyNames)
        {
            if (TryGetPropertyIgnoreCase(detection, propertyName, out JsonElement confidenceElement) &&
                TryReadNormalizedConfidence(confidenceElement, out confidence))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a normalized 0-1 confidence value from a JSON number.
    /// </summary>
    private static bool TryReadNormalizedConfidence(JsonElement element, out decimal confidence)
    {
        confidence = 0m;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (!element.TryGetDecimal(out confidence))
        {
            if (!element.TryGetDouble(out double doubleConfidence))
            {
                return false;
            }

            confidence = (decimal)doubleConfidence;
        }

        return confidence is >= 0m and <= 1m;
    }

    /// <summary>
    /// Finds a JSON property without depending on exact casing.
    /// </summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Treats method and media-type mismatches as signals to retry the legacy multipart upload flow.
    /// </summary>
    private static bool ShouldFallbackToLegacyUpload(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotFound
            or HttpStatusCode.UnsupportedMediaType;
    }

    /// <summary>
    /// Applies the configured threshold to a parsed confidence score.
    /// </summary>
    private FailureDetectionResult CreateSuccessResult(decimal confidence)
    {
        decimal confidenceThreshold = _settingsService.Get<ObicoSettings>().ConfidenceThreshold;
        bool isFailure = confidence >= confidenceThreshold;

        _logger.LogInformation(
            "[ObicoFailureDetection] Analysis complete: confidence={Confidence:F3}, threshold={Threshold:F3}, failure={IsFailure}",
            confidence,
            confidenceThreshold,
            isFailure);

        return FailureDetectionResult.Success(confidence, isFailure);
    }
}
