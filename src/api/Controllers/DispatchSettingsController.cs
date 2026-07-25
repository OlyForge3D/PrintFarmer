using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages system-wide auto-dispatch settings (singleton configuration).
/// </summary>
[ApiController]
[Route("api/dispatch-settings")]
[Authorize]
public class DispatchSettingsController(
    AppDbContext db,
    ILogger<DispatchSettingsController> logger) : ControllerBase
{
    /// <summary>
    /// Gets current auto-dispatch settings.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(DispatchSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettingsAsync(CancellationToken ct)
    {
        DispatchSettings settings = await db.DispatchSettings.FirstAsync(ct);

        return Ok(new DispatchSettingsDto
        {
            AutoDispatchEnabled = settings.AutoDispatchEnabled,
            AutoDispatchMode = settings.AutoDispatchMode,
            IdleThresholdSeconds = settings.IdleThresholdSeconds,
            MinimumScoreThreshold = settings.MinimumScoreThreshold,
            MaxConcurrentDispatches = settings.MaxConcurrentDispatches,
            LoadBalancingStrategy = settings.LoadBalancingStrategy,
            UpdatedAt = settings.UpdatedAt,
        });
    }

    /// <summary>
    /// Updates auto-dispatch settings.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(DispatchSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSettingsAsync(
        [FromBody] UpdateDispatchSettingsDto request, CancellationToken ct)
    {
        // Validate constraints
        if (request.IdleThresholdSeconds < 0)
        {
            return BadRequest("IdleThresholdSeconds must be non-negative.");
        }

        if (request.MinimumScoreThreshold is < 0 or > 100)
        {
            return BadRequest("MinimumScoreThreshold must be between 0 and 100.");
        }

        if (request.MaxConcurrentDispatches < 1)
        {
            return BadRequest("MaxConcurrentDispatches must be at least 1.");
        }

        DispatchSettings settings = await db.DispatchSettings.FirstAsync(ct);

        settings.AutoDispatchEnabled = request.AutoDispatchEnabled;
        settings.AutoDispatchMode = request.AutoDispatchMode;
        settings.IdleThresholdSeconds = request.IdleThresholdSeconds;
        settings.MinimumScoreThreshold = request.MinimumScoreThreshold;
        settings.MaxConcurrentDispatches = request.MaxConcurrentDispatches;
        settings.LoadBalancingStrategy = request.LoadBalancingStrategy;
        settings.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[DispatchSettings] Updated: enabled={Enabled}, mode={Mode}, threshold={Threshold}s, minScore={MinScore}, maxConcurrent={Max}, strategy={Strategy}",
            settings.AutoDispatchEnabled,
            settings.AutoDispatchMode,
            settings.IdleThresholdSeconds,
            settings.MinimumScoreThreshold,
            settings.MaxConcurrentDispatches,
            settings.LoadBalancingStrategy);

        return Ok(new DispatchSettingsDto
        {
            AutoDispatchEnabled = settings.AutoDispatchEnabled,
            AutoDispatchMode = settings.AutoDispatchMode,
            IdleThresholdSeconds = settings.IdleThresholdSeconds,
            MinimumScoreThreshold = settings.MinimumScoreThreshold,
            MaxConcurrentDispatches = settings.MaxConcurrentDispatches,
            LoadBalancingStrategy = settings.LoadBalancingStrategy,
            UpdatedAt = settings.UpdatedAt,
        });
    }
}
