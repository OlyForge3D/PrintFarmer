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
public class DiscoveryController : ControllerBase
{
    private readonly INetworkDiscoveryService _discoveryService;
    private readonly ILogger<DiscoveryController> _logger;

    public DiscoveryController(
        INetworkDiscoveryService discoveryService,
        ILogger<DiscoveryController> logger)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Manually trigger a single discovery scan
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
            var discovered = await _discoveryService.ScanOnceAsync(HttpContext.RequestAborted);

            // Optionally register immediately
            if (autoRegister)
            {
                await _discoveryService.RegisterPrintersAsync(discovered, HttpContext.RequestAborted);
                _logger.LogInformation("Registered {Count} discovered printers", discovered.Count);
            }

            // Return results
            var results = discovered.Select(p => new DiscoveryResult
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
            ScanIntervalSeconds = config.GetValue<int>("Discovery:ScanIntervalSeconds", 300),
            PeriodicDiscoveryEnabled = config.GetValue<bool>("Discovery:EnablePeriodicDiscovery", true),
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
