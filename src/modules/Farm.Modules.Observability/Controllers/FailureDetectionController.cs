using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Startup;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Observability.Controllers;

/// <summary>
/// Provides endpoints for AI-powered print failure detection monitoring and history.
/// </summary>
[ApiController]
[Route("api/failure-detection")]
[Tags("Failure Detection")]
[Authorize]
public class FailureDetectionController : ControllerBase
{
    private readonly IObicoFailureDetectionService _failureDetectionService;
    private readonly IFailureDetectionIncidentHistoryService _incidentHistoryService;
    private readonly IFailureDetectionMonitorStatus _monitorStatus;
    private readonly IStartupStatus _startupStatus;
    private readonly ILogger<FailureDetectionController> _logger;
    private readonly IQueueResourceAuthorizationService _resourceAuthorization;
    private readonly IObicoServerAssignmentService _routing;
    private readonly ISettingsService _settings;

    public FailureDetectionController(
        IObicoFailureDetectionService failureDetectionService,
        IFailureDetectionIncidentHistoryService incidentHistoryService,
        IFailureDetectionMonitorStatus monitorStatus,
        IStartupStatus startupStatus,
        ILogger<FailureDetectionController> logger,
        IQueueResourceAuthorizationService resourceAuthorization,
        IObicoServerAssignmentService routing,
        ISettingsService settings)
    {
        _failureDetectionService = failureDetectionService ?? throw new ArgumentNullException(nameof(failureDetectionService));
        _incidentHistoryService = incidentHistoryService ?? throw new ArgumentNullException(nameof(incidentHistoryService));
        _monitorStatus = monitorStatus ?? throw new ArgumentNullException(nameof(monitorStatus));
        _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resourceAuthorization = resourceAuthorization ?? throw new ArgumentNullException(nameof(resourceAuthorization));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Gets the current monitoring status for all printers.
    /// </summary>
    /// <returns>Monitoring status summary</returns>
    /// <response code="200">Returns the monitoring status</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpGet("status")]
    [ProducesResponseType(typeof(FailureDetectionMonitorStatusDto), 200)]
    [ProducesResponseType(503)]
    public ActionResult<FailureDetectionMonitorStatusDto> GetStatus()
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            return Ok(_monitorStatus.GetSnapshot());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FailureDetectionController] Exception in GetStatus");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Triggers a manual failure analysis for a specific printer using its camera snapshot.
    /// </summary>
    /// <param name="printerId">The printer ID to analyze</param>
    /// <param name="snapshotUrl">Optional: Override snapshot URL. If not provided, uses the printer's configured camera.</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Failure detection result</returns>
    /// <response code="200">Returns the analysis result</response>
    /// <response code="400">If the printer has no camera configured</response>
    /// <response code="404">If the printer is not visible to the caller</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpPost("analyze/{printerId:guid}")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<object>> AnalyzePrinterAsync(
        Guid printerId,
        [FromQuery] string? snapshotUrl = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (string.IsNullOrWhiteSpace(snapshotUrl))
            {
                return BadRequest(new { error = "snapshotUrl query parameter is required for manual analysis." });
            }

            if (!await _resourceAuthorization.CanAccessPrinterAsync(User, printerId, PrinterGroupAccessLevel.View, ct))
            {
                return NotFound($"Printer with ID {printerId} not found");
            }

            ObicoPrintRoute route = await _routing.GetRouteAsync(printerId, _settings.Get<ObicoSettings>(), ct);
            if (route.Url is null)
            {
                _logger.LogWarning(
                    "[FailureDetectionController] Manual monitoring unavailable for printer {PrinterId}: {Reason}",
                    printerId, route.UnavailableReason);
                return StatusCode(503, new { error = route.UnavailableReason });
            }

            _logger.LogInformation(
                "[FailureDetectionController] Manual analysis requested for printer {PrinterId}",
                printerId);

            FailureDetectionResult result = await _failureDetectionService.AnalyzeImageFromUrlAsync(snapshotUrl, route.Url, route.ApiKey, ct);

            if (result.ErrorMessage != null)
            {
                return BadRequest(new
                {
                    error = result.ErrorMessage,
                    analyzedAt = result.AnalyzedAt
                });
            }

            return Ok(new
            {
                printerId,
                isFailureDetected = result.IsFailureDetected,
                confidence = result.Confidence,
                analyzedAt = result.AnalyzedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FailureDetectionController] Exception in AnalyzePrinter for printer {PrinterId}", printerId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets recent persisted failure-detection incidents.
    /// </summary>
    /// <param name="printerId">Optional printer filter.</param>
    /// <param name="take">Maximum incidents to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of recent failure detection events</returns>
    /// <response code="200">Returns the event history</response>
    /// <response code="404">If the requested printer is not visible to the caller</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IEnumerable<FailureDetectionDto>), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<FailureDetectionDto>>> GetHistoryAsync(
        [FromQuery] Guid? printerId = null,
        [FromQuery] int take = FailureDetectionIncidentHistoryService.DefaultTake,
        CancellationToken ct = default)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (printerId.HasValue &&
                !await _resourceAuthorization.CanAccessPrinterAsync(User, printerId.Value, PrinterGroupAccessLevel.View, ct))
            {
                return NotFound($"Printer with ID {printerId.Value} not found");
            }

            List<FailureDetectionDto> incidents = await _incidentHistoryService.GetRecentAsync(User, printerId, take, ct);
            return Ok(incidents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FailureDetectionController] Exception in GetHistory");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
