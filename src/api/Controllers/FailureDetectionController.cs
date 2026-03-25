using Farm.Infrastructure;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Services.Startup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

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
    private readonly IFailureDetectionMonitorStatus _monitorStatus;
    private readonly IStartupStatus _startupStatus;
    private readonly ILogger<FailureDetectionController> _logger;

    public FailureDetectionController(
        IObicoFailureDetectionService failureDetectionService,
        IFailureDetectionMonitorStatus monitorStatus,
        IStartupStatus startupStatus,
        ILogger<FailureDetectionController> logger)
    {
        _failureDetectionService = failureDetectionService ?? throw new ArgumentNullException(nameof(failureDetectionService));
        _monitorStatus = monitorStatus ?? throw new ArgumentNullException(nameof(monitorStatus));
        _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    /// <response code="503">If the system is still initializing</response>
    [HttpPost("analyze/{printerId:guid}")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
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

            _logger.LogInformation(
                "[FailureDetectionController] Manual analysis requested for printer {PrinterId}",
                printerId);

            FailureDetectionResult result = await _failureDetectionService.AnalyzeImageFromUrlAsync(snapshotUrl, ct);

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
    /// Gets recent failure detection events.
    /// Note: This feature requires persistence layer implementation.
    /// </summary>
    /// <returns>List of recent failure detection events</returns>
    /// <response code="200">Returns the event history</response>
    /// <response code="501">Not yet implemented - events are currently transient (SignalR only)</response>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IEnumerable<FailureDetectionDto>), 200)]
    [ProducesResponseType(501)]
    public ActionResult<IEnumerable<FailureDetectionDto>> GetHistory()
    {
        // History would require a database table to persist detection events
        // Currently, events are only broadcast via SignalR in real-time
        return StatusCode(501, new
        {
            message = "Event history not yet implemented. Failure detection events are currently broadcast via SignalR in real-time only.",
            feature = "event_persistence"
        });
    }
}
