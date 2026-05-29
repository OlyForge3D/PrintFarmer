using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Background service that periodically probes camera URLs to monitor health status.
/// Updates health metrics including status, last check time, and failure counts.
/// Supports HTTP snapshot probing and RTSP OPTIONS handshake for stream-only cameras.
/// </summary>
public sealed class CameraHealthMonitorService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<CameraHealthMonitorService> logger) : BackgroundService, ICameraHealthMonitorService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<CameraHealthMonitorService> _logger = logger;

    // Run health checks every 5 minutes
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    // HTTP timeout for camera snapshot requests
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    // TCP/RTSP timeout for stream probing
    private static readonly TimeSpan RtspTimeout = TimeSpan.FromSeconds(10);

    // Failure thresholds for health status transitions
    private const int DegradedThreshold = 1; // 1-2 failures = Degraded
    private const int UnhealthyThreshold = 3; // 3+ failures = Unhealthy

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Camera Health Monitor Service starting");

        // Initial delay to allow database initialization
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunHealthCheckAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Camera Health Monitor Service stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in camera health monitoring loop");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Camera Health Monitor Service stopped");
    }

    /// <inheritdoc/>
    public async Task RunHealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int checkedCount = 0;
        int healthyCount = 0;
        int degradedCount = 0;
        int unhealthyCount = 0;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Get all enabled cameras with a snapshot URL or a stream URL (RTSP cameras may have no snapshot URL)
            List<Camera> cameras = await dbContext.Cameras
                .Where(c => c.IsEnabled && (!string.IsNullOrEmpty(c.SnapshotUrl) || !string.IsNullOrEmpty(c.StreamUrl)))
                .ToListAsync(cancellationToken);

            if (cameras.Count == 0)
            {
                return;
            }

            _logger.LogDebug("Starting health check for {Count} cameras", cameras.Count);

            foreach (Camera camera in cameras)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await CheckCameraHealthAsync(camera, cancellationToken);
                    checkedCount++;

                    // Save after each camera probe to prevent race conditions with concurrent API updates
                    await dbContext.SaveChangesAsync(cancellationToken);

                    // Track health status distribution
                    switch (camera.HealthStatus)
                    {
                        case CameraHealthStatus.Healthy:
                            healthyCount++;
                            break;
                        case CameraHealthStatus.Degraded:
                            degradedCount++;
                            break;
                        case CameraHealthStatus.Unhealthy:
                            unhealthyCount++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking health for camera {CameraId} ({CameraName})",
                        camera.Id, camera.Name);
                }
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "Camera health check completed: {Checked} cameras checked in {Elapsed}ms. " +
                "Status: {Healthy} healthy, {Degraded} degraded, {Unhealthy} unhealthy",
                checkedCount, stopwatch.ElapsedMilliseconds, healthyCount, degradedCount, unhealthyCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete camera health check");
        }
    }

    private async Task CheckCameraHealthAsync(Camera camera, CancellationToken cancellationToken)
    {
        // Determine which URL to probe: prefer SnapshotUrl (HTTP), fall back to StreamUrl (RTSP)
        string? probeUrl = !string.IsNullOrWhiteSpace(camera.SnapshotUrl)
            ? camera.SnapshotUrl
            : camera.StreamUrl;

        if (string.IsNullOrWhiteSpace(probeUrl))
        {
            return;
        }

        // Validate URL before probing to prevent SSRF
        if (!IsUrlSafeForProbing(probeUrl))
        {
            _logger.LogWarning(
                "Camera {CameraId} ({CameraName}) has an unsafe URL, skipping probe: {Url}",
                camera.Id, camera.Name, probeUrl);
            camera.HealthStatus = CameraHealthStatus.Unhealthy;
            camera.LastHealthCheck = DateTime.UtcNow;
            camera.HealthMessage = "URL blocked by safety validation";
            return;
        }

        // Route to the appropriate probe method based on scheme
        if (Uri.TryCreate(probeUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            await CheckRtspHealthAsync(camera, uri, cancellationToken);
        }
        else
        {
            await CheckHttpHealthAsync(camera, probeUrl, cancellationToken);
        }
    }

    private async Task CheckHttpHealthAsync(Camera camera, string probeUrl, CancellationToken cancellationToken)
    {
        CameraHealthStatus previousStatus = camera.HealthStatus;
        DateTime checkTime = DateTime.UtcNow;

        try
        {
            using HttpClient httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = HttpTimeout;

            HttpResponseMessage response = await httpClient.GetAsync(
                probeUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                camera.HealthStatus = CameraHealthStatus.Healthy;
                camera.ConsecutiveFailures = 0;
                camera.HealthMessage = null;
                camera.LastHealthCheck = checkTime;

                if (previousStatus != CameraHealthStatus.Healthy && previousStatus != CameraHealthStatus.Unknown)
                {
                    _logger.LogInformation(
                        "Camera {CameraId} ({CameraName}) recovered: {PrevStatus} → Healthy",
                        camera.Id, camera.Name, previousStatus);
                }
            }
            else
            {
                HandleFailure(camera, checkTime, previousStatus,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }
        }
        catch (HttpRequestException ex)
        {
            HandleFailure(camera, checkTime, previousStatus, $"HTTP error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            HandleFailure(camera, checkTime, previousStatus, "Request timeout");
        }
        catch (Exception ex)
        {
            HandleFailure(camera, checkTime, previousStatus, $"Error: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Probes an RTSP endpoint via TCP connect + RTSP OPTIONS handshake.
    /// Sends "OPTIONS rtsp://{host}:{port}{path} RTSP/1.0\r\nCSeq: 1\r\n\r\n"
    /// and expects a response starting with "RTSP/1.0 200".
    /// </summary>
    private async Task CheckRtspHealthAsync(Camera camera, Uri rtspUri, CancellationToken cancellationToken)
    {
        CameraHealthStatus previousStatus = camera.HealthStatus;
        DateTime checkTime = DateTime.UtcNow;

        try
        {
            int port = rtspUri.Port > 0 ? rtspUri.Port : 554;
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RtspTimeout);

            await tcp.ConnectAsync(rtspUri.Host, port, cts.Token);

            // Build RTSP OPTIONS request
            string request = $"OPTIONS {rtspUri.AbsoluteUri} RTSP/1.0\r\nCSeq: 1\r\n\r\n";
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);

            NetworkStream stream = tcp.GetStream();
            await stream.WriteAsync(requestBytes, cts.Token);

            // Read response (just need the status line)
            byte[] buffer = new byte[512];
            int bytesRead = await stream.ReadAsync(buffer, cts.Token);

            if (bytesRead > 0)
            {
                string responseLine = Encoding.ASCII.GetString(buffer, 0, Math.Min(bytesRead, 256));

                if (responseLine.StartsWith("RTSP/1.0 200", StringComparison.OrdinalIgnoreCase))
                {
                    camera.HealthStatus = CameraHealthStatus.Healthy;
                    camera.ConsecutiveFailures = 0;
                    camera.HealthMessage = null;
                    camera.LastHealthCheck = checkTime;

                    if (previousStatus != CameraHealthStatus.Healthy && previousStatus != CameraHealthStatus.Unknown)
                    {
                        _logger.LogInformation(
                            "Camera {CameraId} ({CameraName}) RTSP recovered: {PrevStatus} → Healthy",
                            camera.Id, camera.Name, previousStatus);
                    }
                }
                else
                {
                    // Extract the RTSP status code from response
                    string statusLine = responseLine.Split(['\r', '\n'])[0];
                    HandleFailure(camera, checkTime, previousStatus, $"RTSP error: {statusLine}");
                }
            }
            else
            {
                HandleFailure(camera, checkTime, previousStatus, "RTSP error: empty response");
            }
        }
        catch (SocketException ex)
        {
            HandleFailure(camera, checkTime, previousStatus, $"RTSP connect failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            HandleFailure(camera, checkTime, previousStatus, "RTSP timeout");
        }
        catch (Exception ex)
        {
            HandleFailure(camera, checkTime, previousStatus, $"RTSP error: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Validates a URL is safe to probe, delegating to the shared <see cref="CameraUrlValidator"/>.
    /// </summary>
    private static bool IsUrlSafeForProbing(string url) => CameraUrlValidator.IsUrlSafeForProbing(url);

    private void HandleFailure(Camera camera, DateTime checkTime, CameraHealthStatus previousStatus, string errorMessage)
    {
        camera.ConsecutiveFailures++;
        camera.LastHealthCheck = checkTime;
        camera.HealthMessage = errorMessage;

        // Update health status based on consecutive failures
        if (camera.ConsecutiveFailures >= UnhealthyThreshold)
        {
            camera.HealthStatus = CameraHealthStatus.Unhealthy;
        }
        else if (camera.ConsecutiveFailures > DegradedThreshold)
        {
            camera.HealthStatus = CameraHealthStatus.Degraded;
        }
        else
        {
            camera.HealthStatus = CameraHealthStatus.Degraded;
        }

        // Log status transitions
        if (camera.HealthStatus != previousStatus)
        {
            _logger.LogWarning(
                "Camera {CameraId} ({CameraName}) health degraded: {PrevStatus} → {NewStatus} " +
                "(failures: {Failures}, message: {Message})",
                camera.Id, camera.Name, previousStatus, camera.HealthStatus,
                camera.ConsecutiveFailures, errorMessage);
        }
    }
}
