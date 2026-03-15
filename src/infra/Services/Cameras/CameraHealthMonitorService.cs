using System.Diagnostics;
using System.Net;
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

            // Get all enabled cameras with snapshot URLs
            List<Camera> cameras = await dbContext.Cameras
                .Where(c => c.IsEnabled && !string.IsNullOrEmpty(c.SnapshotUrl))
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
        if (string.IsNullOrWhiteSpace(camera.SnapshotUrl))
        {
            return;
        }

        // Validate URL before probing to prevent SSRF
        if (!IsUrlSafeForProbing(camera.SnapshotUrl))
        {
            _logger.LogWarning(
                "Camera {CameraId} ({CameraName}) has an unsafe snapshot URL, skipping probe: {Url}",
                camera.Id, camera.Name, camera.SnapshotUrl);
            camera.HealthStatus = CameraHealthStatus.Unhealthy;
            camera.LastHealthCheck = DateTime.UtcNow;
            camera.HealthMessage = "URL blocked by safety validation";
            return;
        }

        CameraHealthStatus previousStatus = camera.HealthStatus;
        DateTime checkTime = DateTime.UtcNow;

        try
        {
            using HttpClient httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = HttpTimeout;

            HttpResponseMessage response = await httpClient.GetAsync(
                camera.SnapshotUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                // Success - mark as healthy
                camera.HealthStatus = CameraHealthStatus.Healthy;
                camera.ConsecutiveFailures = 0;
                camera.HealthMessage = null;
                camera.LastHealthCheck = checkTime;

                // Log transition from unhealthy/degraded to healthy
                if (previousStatus != CameraHealthStatus.Healthy && previousStatus != CameraHealthStatus.Unknown)
                {
                    _logger.LogInformation(
                        "Camera {CameraId} ({CameraName}) recovered: {PrevStatus} → Healthy",
                        camera.Id, camera.Name, previousStatus);
                }
            }
            else
            {
                // Non-200 response - increment failure count
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
    /// Validates a URL is safe to probe, blocking loopback, link-local, and non-HTTP schemes.
    /// Private network IPs (10.x, 192.168.x, 172.16-31.x) are allowed since this is a local network app.
    /// </summary>
    private bool IsUrlSafeForProbing(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        // Block non-HTTP(S) schemes
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string host = uri.Host;

        // Block loopback addresses
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
            string.Equals(host, "::1", StringComparison.Ordinal) ||
            string.Equals(host, "[::1]", StringComparison.Ordinal))
        {
            return false;
        }

        // Check IP address ranges
        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            byte[] bytes = ip.GetAddressBytes();

            // Block IPv4 loopback range (127.0.0.0/8)
            if (bytes.Length == 4 && bytes[0] == 127)
            {
                return false;
            }

            // Block link-local (169.254.x.x — cloud metadata endpoint range)
            if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
            {
                return false;
            }

            // Private IPs are ALLOWED: 10.x, 192.168.x, 172.16-31.x
            // (this is a local network printer management app)
        }

        return true;
    }

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
