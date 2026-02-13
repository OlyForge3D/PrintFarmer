using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Diagnostics endpoint for printer connection health monitoring.
/// Returns per-printer connection state, transition history, uptime, and failure counts.
/// </summary>
[ApiController]
[Route("api/diagnostics")]
[Tags("Diagnostics")]
[Authorize(Roles = "farm_admin")]
public class ConnectionDiagnosticsController(
    IEnumerable<IPrinterConnectionHealthProvider> healthProviders,
    IUnifiedLoggingService logger) : ControllerBase
{
    private readonly IEnumerable<IPrinterConnectionHealthProvider> _healthProviders = healthProviders;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Gets connection health data for all printers across all backends.
    /// </summary>
    [HttpGet("connections")]
    [ProducesResponseType(typeof(ConnectionDiagnosticsResponse), 200)]
    public IActionResult GetConnectionHealth()
    {
        var allHealth = new List<PrinterConnectionHealth>();

        foreach (var provider in _healthProviders)
        {
            try
            {
                var health = provider.GetConnectionHealth();
                allHealth.AddRange(health.Values);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve connection health from provider {Provider}", provider.GetType().Name);
            }
        }

        var response = new ConnectionDiagnosticsResponse
        {
            Printers = allHealth.OrderBy(h => h.PrinterName).ToList(),
            TotalPrinters = allHealth.Count,
            ConnectedCount = allHealth.Count(h => h.ConnectionState == Farm.Infrastructure.Domain.PrinterConnectionState.Connected),
            ReconnectingCount = allHealth.Count(h => h.ConnectionState == Farm.Infrastructure.Domain.PrinterConnectionState.Reconnecting),
            OfflineCount = allHealth.Count(h => h.ConnectionState == Farm.Infrastructure.Domain.PrinterConnectionState.Offline),
            DegradedCount = allHealth.Count(h => h.ConnectionState == Farm.Infrastructure.Domain.PrinterConnectionState.Degraded),
            TimestampUtc = DateTime.UtcNow
        };

        return Ok(response);
    }
}

/// <summary>
/// Response DTO for connection diagnostics endpoint.
/// </summary>
public sealed class ConnectionDiagnosticsResponse
{
    public required List<PrinterConnectionHealth> Printers { get; init; }

    public int TotalPrinters { get; init; }

    public int ConnectedCount { get; init; }

    public int ReconnectingCount { get; init; }

    public int OfflineCount { get; init; }

    public int DegradedCount { get; init; }

    public DateTime TimestampUtc { get; init; }
}
