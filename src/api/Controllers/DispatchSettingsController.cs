using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Web.Api.Infrastructure.Authorization;
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
[RequirePermission(PrintFarmerPermissions.DispatchSettings.Manage)]
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
        if (settings.RowVersion is not { Length: > 0 })
        {
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        WriteEtag(settings.RowVersion);
        return Ok(ToDto(settings));
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
        if (!TryReadRequiredEtag(out byte[]? expected, out IActionResult? error))
        {
            return error!;
        }

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
        db.Entry(settings).Property(candidate => candidate.RowVersion).OriginalValue = expected;

        settings.AutoDispatchEnabled = request.AutoDispatchEnabled;
        settings.AutoDispatchMode = request.AutoDispatchMode;
        settings.IdleThresholdSeconds = request.IdleThresholdSeconds;
        settings.MinimumScoreThreshold = request.MinimumScoreThreshold;
        settings.MaxConcurrentDispatches = request.MaxConcurrentDispatches;
        settings.LoadBalancingStrategy = request.LoadBalancingStrategy;
        settings.UpdatedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StatusCode(
                StatusCodes.Status412PreconditionFailed,
                new { error = "dispatch_settings_revision_conflict" });
        }

        logger.LogInformation(
            "[DispatchSettings] Updated: enabled={Enabled}, mode={Mode}, threshold={Threshold}s, minScore={MinScore}, maxConcurrent={Max}, strategy={Strategy}",
            settings.AutoDispatchEnabled,
            settings.AutoDispatchMode,
            settings.IdleThresholdSeconds,
            settings.MinimumScoreThreshold,
            settings.MaxConcurrentDispatches,
            settings.LoadBalancingStrategy);

        WriteEtag(settings.RowVersion);
        return Ok(ToDto(settings));
    }

    private static DispatchSettingsDto ToDto(DispatchSettings settings) =>
        new()
        {
            ETag = settings.RowVersion is { Length: > 0 }
                ? Convert.ToBase64String(settings.RowVersion)
                : null,
            Revision = settings.Revision,
            AutoDispatchEnabled = settings.AutoDispatchEnabled,
            AutoDispatchMode = settings.AutoDispatchMode,
            IdleThresholdSeconds = settings.IdleThresholdSeconds,
            MinimumScoreThreshold = settings.MinimumScoreThreshold,
            MaxConcurrentDispatches = settings.MaxConcurrentDispatches,
            LoadBalancingStrategy = settings.LoadBalancingStrategy,
            UpdatedAt = settings.UpdatedAt,
        };

    private bool TryReadRequiredEtag(
        out byte[]? expected,
        out IActionResult? error)
    {
        string? supplied = Request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            expected = null;
            error = StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new { error = "precondition_required", detail = "If-Match is required." });
            return false;
        }

        try
        {
            expected = Convert.FromBase64String(
                supplied.Trim().TrimStart('W', '/').Trim('"'));
            error = null;
            return true;
        }
        catch (FormatException)
        {
            expected = null;
            error = BadRequest(new { error = "If-Match must be a base-64 encoded ETag." });
            return false;
        }
    }

    private void WriteEtag(byte[]? rowVersion)
    {
        if (rowVersion is { Length: > 0 })
        {
            Response.Headers.ETag = $"\"{Convert.ToBase64String(rowVersion)}\"";
        }
    }
}
