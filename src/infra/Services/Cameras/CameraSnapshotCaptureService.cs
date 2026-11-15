using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.FailureDetection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

public sealed record CameraSnapshotCaptureResult(byte[]? ImageData, string? ErrorMessage)
{
    public static CameraSnapshotCaptureResult Succeeded(byte[] imageData) => new(imageData, null);

    public static CameraSnapshotCaptureResult Failed(string message) => new(null, message);
}

public interface ICameraSnapshotCaptureService
{
    Task<CameraSnapshotCaptureResult> CaptureAsync(
        string snapshotUrl,
        int timeoutSeconds,
        CancellationToken ct = default);
}

public sealed class CameraSnapshotCaptureService : ICameraSnapshotCaptureService
{
    private const int MaximumSnapshotBytes = 32 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CameraSnapshotCaptureService> _logger;
    private readonly IEgressGuard _egressGuard;

    public CameraSnapshotCaptureService(
        IHttpClientFactory httpClientFactory,
        ILogger<CameraSnapshotCaptureService> logger,
        IEgressGuard egressGuard)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _egressGuard = egressGuard ?? throw new ArgumentNullException(nameof(egressGuard));
    }

    public async Task<CameraSnapshotCaptureResult> CaptureAsync(
        string snapshotUrl,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotUrl))
        {
            return CameraSnapshotCaptureResult.Failed("Snapshot URL is empty");
        }

        if (timeoutSeconds is < 1 or > 60)
        {
            return CameraSnapshotCaptureResult.Failed("Snapshot capture timeout is invalid");
        }

        try
        {
            EgressCheckResult egressCheck = await _egressGuard.CheckAsync(snapshotUrl, ct);
            if (!egressCheck.IsAllowed || egressCheck.Uri is null || egressCheck.ResolvedAddress is null)
            {
                return CameraSnapshotCaptureResult.Failed(
                    $"Snapshot URL is not allowed for local fallback: {egressCheck.DenyReason ?? "Destination could not be pinned to a vetted address"}");
            }

            using HttpClient httpClient = _httpClientFactory.CreateClient("VettedEgress");
            httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

            _logger.LogDebug("[CameraSnapshotCapture] Fetching image from {SnapshotUrl}", LogSanitizer.Sanitize(snapshotUrl));

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                EgressGuard.CreatePinnedUri(egressCheck.Uri, egressCheck.ResolvedAddress));
            request.Headers.Host = egressCheck.Uri.Authority;
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[CameraSnapshotCapture] Failed to fetch snapshot: {StatusCode}", response.StatusCode);
                return CameraSnapshotCaptureResult.Failed($"Failed to fetch snapshot: HTTP {(int)response.StatusCode}");
            }

            byte[] imageData = await BoundedHttpContentReader.ReadAsByteArrayAsync(
                response.Content,
                MaximumSnapshotBytes,
                ct);

            if (imageData.Length == 0)
            {
                return CameraSnapshotCaptureResult.Failed("Fetched image is empty");
            }

            return CameraSnapshotCaptureResult.Succeeded(imageData);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[CameraSnapshotCapture] Failed to fetch snapshot from {SnapshotUrl}", LogSanitizer.Sanitize(snapshotUrl));
            return CameraSnapshotCaptureResult.Failed($"PrintFarmer could not download the camera snapshot: {ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[CameraSnapshotCapture] Snapshot fetch timed out from {SnapshotUrl}", LogSanitizer.Sanitize(snapshotUrl));
            return CameraSnapshotCaptureResult.Failed("Snapshot fetch timeout. PrintFarmer could not download the camera snapshot in time.");
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "[CameraSnapshotCapture] Snapshot exceeds the maximum supported size");
            return CameraSnapshotCaptureResult.Failed("Fetched image exceeds the 32 MB maximum size.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CameraSnapshotCapture] Unexpected error fetching snapshot");
            return CameraSnapshotCaptureResult.Failed($"Unexpected error: {ex.GetType().Name}");
        }
    }
}
