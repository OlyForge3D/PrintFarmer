using Farm.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using PrinterDiscovery.Services;

namespace PrinterDiscovery.Controllers;

/// <summary>
/// Controller for managing printer discovery operations
/// Supports both manual triggers (pull mode) and status queries
/// </summary>
[ApiController]
[Route("api/discovery")]
[Tags("Discovery")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S6960:This controller has multiple responsibilities", Justification = "Discovery endpoints are logically grouped")]
public class DiscoveryController(
    INetworkDiscoveryService discoveryService,
    IStreamingDiscoveryService streamingDiscoveryService,
    ILogger<DiscoveryController> logger) : ControllerBase
{
    private readonly INetworkDiscoveryService _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
    private readonly IStreamingDiscoveryService _streamingDiscoveryService = streamingDiscoveryService ?? throw new ArgumentNullException(nameof(streamingDiscoveryService));
    private readonly ILogger<DiscoveryController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Start a streaming discovery scan with progress updates via SignalR.
    /// Returns immediately with a session ID that can be used to receive progress via SignalR.
    /// </summary>
    /// <param name="request">Optional request with backend filters and discovery settings</param>
    [HttpPost("stream")]
    [ProducesResponseType(typeof(StreamingDiscoveryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public IActionResult StartStreamingDiscovery([FromBody] StreamingDiscoveryRequest? request)
    {
        try
        {
            string sessionId = Guid.NewGuid().ToString("N");
            _logger.LogInformation(
                "Starting streaming discovery with session {SessionId}, Subnets: {Subnets}, Backends: {Backends}, Timeout: {Timeout}ms, MaxConcurrent: {MaxConcurrent}",
                sessionId,
                request?.Subnets != null ? string.Join(", ", request.Subnets) : "default",
                request?.Backends != null ? string.Join(", ", request.Backends) : "all",
                request?.ProbeTimeoutMs?.ToString() ?? "default",
                request?.MaxConcurrentProbes?.ToString() ?? "default");

            // Start the discovery in the background with a small delay
            // This gives the frontend time to receive the sessionId and join the SignalR group
            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait for frontend to join the SignalR group
                    await Task.Delay(500);

                    await _streamingDiscoveryService.ScanWithProgressAsync(
                        sessionId,
                        request?.Backends,
                        request?.AutoRegister ?? false,
                        request?.Subnets,
                        request?.ProbeTimeoutMs,
                        request?.MaxConcurrentProbes,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Streaming discovery failed for session {SessionId}", sessionId);
                }
            });

            return Ok(new StreamingDiscoveryResponse
            {
                SessionId = sessionId,
                Message = "Discovery started - connect to SignalR hub for progress updates"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start streaming discovery");
            return StatusCode(500, new { error = "Failed to start discovery", message = ex.Message });
        }
    }

    /// <summary>
    /// Cancel an active streaming discovery session.
    /// </summary>
    /// <param name="sessionId">The session ID to cancel</param>
    [HttpPost("stream/{sessionId}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult CancelStreamingDiscovery([FromRoute] string sessionId)
    {
        try
        {
            _logger.LogInformation("Cancelling streaming discovery session {SessionId}", sessionId);
            _streamingDiscoveryService.CancelSession(sessionId);
            return Ok(new { message = "Discovery session cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel discovery session {SessionId}", sessionId);
            return StatusCode(500, new { error = "Failed to cancel discovery", message = ex.Message });
        }
    }

    /// <summary>
    /// Manually trigger a single discovery scan (synchronous).
    /// Returns list of discovered printers without registering them
    /// </summary>
    /// <param name="autoRegister">If true, automatically register discovered printers with API</param>
    [HttpPost("scan")]
    [ProducesResponseType(typeof(List<DiscoveryResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ScanAsync([FromQuery] bool autoRegister = true)
    {
        try
        {
            _logger.LogInformation("Manual discovery scan requested (autoRegister={AutoRegister})", autoRegister);

            // Perform discovery scan
            IReadOnlyList<DiscoveredPrinterDto> discovered = await _discoveryService.ScanOnceAsync(HttpContext.RequestAborted);

            // Optionally register immediately
            if (autoRegister)
            {
                await _discoveryService.RegisterPrintersAsync(discovered, HttpContext.RequestAborted);
                _logger.LogInformation("Registered {Count} discovered printers", discovered.Count);
            }

            // Return results
            List<DiscoveryResult> results = discovered.Select(p => new DiscoveryResult
            {
                Hostname = p.Name,
                IpAddress = p.IpAddress,
                Port = p.BackendPort ?? 80,
                PrinterBackend = p.Backend.ToString().ToLowerInvariant(),
                DiscoveredAt = p.DiscoveredAt,
                Registered = autoRegister
            }).ToList();

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery scan failed");
            return StatusCode(500, new { error = "Discovery scan failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Health check endpoint
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Get service information and configuration
    /// </summary>
    [HttpGet("info")]
    [ProducesResponseType(typeof(ServiceInfo), StatusCodes.Status200OK)]
    public IActionResult GetServiceInfo([FromServices] IConfiguration config)
    {
        return Ok(new ServiceInfo
        {
            ServiceName = "Printer Discovery Service",
            Version = "1.0.0",
            ScanIntervalSeconds = config.GetValue("Discovery:ScanIntervalSeconds", 300),
            PeriodicDiscoveryEnabled = config.GetValue("Discovery:EnablePeriodicDiscovery", true),
            ApiBaseUrl = config["Discovery:ApiBaseUrl"] ?? "http://api:5245",
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Send heartbeat to API to confirm discovery service is alive
    /// Called periodically by background service to update LastHeartbeat timestamp
    /// </summary>
    [HttpPost("heartbeat")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public IActionResult SendHeartbeat()
    {
        try
        {
            _logger.LogDebug("Heartbeat received from discovery service");
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process heartbeat");
            return StatusCode(500, new { error = "Heartbeat failed", message = ex.Message });
        }
    }
}

/// <summary>
/// Result of a discovery scan
/// </summary>
public class DiscoveryResult
{
    public string Hostname { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;

    public int Port { get; set; }

    public string PrinterBackend { get; set; } = string.Empty;

    public DateTime DiscoveredAt { get; set; }

    public bool Registered { get; set; }
}

/// <summary>
/// Service information
/// </summary>
public class ServiceInfo
{
    public string ServiceName { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public int ScanIntervalSeconds { get; set; }

    public bool PeriodicDiscoveryEnabled { get; set; }

    public string ApiBaseUrl { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Request to start a streaming discovery scan.
/// </summary>
public class StreamingDiscoveryRequest
{
    /// <summary>
    /// Optional list of backends to filter discovery.
    /// </summary>
    public PrinterBackend[]? Backends { get; set; }

    /// <summary>
    /// Whether to automatically register discovered printers with the API.
    /// </summary>
    public bool AutoRegister { get; set; } = false;

    /// <summary>
    /// List of subnets to scan (CIDR notation). If not provided, uses configured defaults.
    /// </summary>
    public string[]? Subnets { get; set; }

    /// <summary>
    /// Timeout for each probe in milliseconds. If not provided, uses configured default.
    /// </summary>
    public int? ProbeTimeoutMs { get; set; }

    /// <summary>
    /// Maximum number of concurrent probes. If not provided, uses configured default.
    /// </summary>
    public int? MaxConcurrentProbes { get; set; }
}

/// <summary>
/// Response from starting a streaming discovery.
/// </summary>
public class StreamingDiscoveryResponse
{
    public string SessionId { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
