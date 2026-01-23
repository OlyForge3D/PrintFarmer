using Farm.Web.Api.Services.Background;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for monitoring background service status
/// </summary>
[ApiController]
[Route("api/services")]
[Authorize(Roles = "farm_admin")]
public class BackgroundServicesController(IBackgroundServiceMonitor serviceMonitor) : ControllerBase
{
    private readonly IBackgroundServiceMonitor _serviceMonitor = serviceMonitor;

    /// <summary>
    /// Get status of all background services
    /// </summary>
    /// <returns>List of background service statuses</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<BackgroundServiceStatus>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<BackgroundServiceStatus>> GetAllServices()
    {
        IReadOnlyList<BackgroundServiceStatus> statuses = _serviceMonitor.GetAllStatuses();
        return Ok(statuses);
    }

    /// <summary>
    /// Get status of a specific background service
    /// </summary>
    /// <param name="serviceId">The service identifier</param>
    /// <returns>The service status or 404 if not found</returns>
    [HttpGet("{serviceId}")]
    [ProducesResponseType(typeof(BackgroundServiceStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BackgroundServiceStatus> GetServiceStatus(string serviceId)
    {
        BackgroundServiceStatus? status = _serviceMonitor.GetStatus(serviceId);
        if (status == null)
        {
            return NotFound($"Service '{serviceId}' not found");
        }

        return Ok(status);
    }

    /// <summary>
    /// Get summary counts of background services by status
    /// </summary>
    /// <returns>Summary of service counts</returns>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(BackgroundServicesSummary), StatusCodes.Status200OK)]
    public ActionResult<BackgroundServicesSummary> GetServicesSummary()
    {
        IReadOnlyList<BackgroundServiceStatus> statuses = _serviceMonitor.GetAllStatuses();

        BackgroundServicesSummary summary = new()
        {
            TotalServices = statuses.Count,
            RunningServices = statuses.Count(s => s.IsRunning),
            EnabledServices = statuses.Count(s => s.IsEnabled),
            DisabledServices = statuses.Count(s => !s.IsEnabled),
            ServicesWithErrors = statuses.Count(s => s.LastError != null),
            ByCategory = statuses
                .Where(s => s.Category != null)
                .GroupBy(s => s.Category!)
                .ToDictionary(
                    g => g.Key,
                    g => new CategorySummary
                    {
                        Total = g.Count(),
                        Running = g.Count(s => s.IsRunning),
                        WithErrors = g.Count(s => s.LastError != null)
                    })
        };

        return Ok(summary);
    }
}

/// <summary>
/// Summary of background services
/// </summary>
public record BackgroundServicesSummary
{
    public int TotalServices { get; init; }
    public int RunningServices { get; init; }
    public int EnabledServices { get; init; }
    public int DisabledServices { get; init; }
    public int ServicesWithErrors { get; init; }
    public Dictionary<string, CategorySummary> ByCategory { get; init; } = new();
}

/// <summary>
/// Summary for a category of services
/// </summary>
public record CategorySummary
{
    public int Total { get; init; }
    public int Running { get; init; }
    public int WithErrors { get; init; }
}
