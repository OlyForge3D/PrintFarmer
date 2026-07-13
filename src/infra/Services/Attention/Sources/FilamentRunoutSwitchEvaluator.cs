using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>
/// Default <see cref="IFilamentRunoutSwitchEvaluator"/>. Combines the configured-backup resolver
/// (<see cref="IFilamentFallbackGroupService.FindAvailableFallbackAsync"/>) with live printer
/// telemetry to grade a runout's mitigation evidence (issue #711, F6 remediation, Finding 2).
/// </summary>
/// <remarks>
/// Severity policy realised here:
/// <list type="bullet">
///   <item><b>SwitchConfirmed</b> — the printer's live loaded spool has moved off the runout spool
///     onto the backup member's spool, same material. This is telemetry, not configuration, so it
///     is the only tier that permits an informational downgrade.</item>
///   <item><b>BackupAvailable</b> — a configured fallback member currently holds a loaded
///     compatible spool, but live telemetry does not (yet) prove the switch happened.</item>
///   <item><b>NoBackup</b> — no configured, loaded, compatible backup exists (or the warning is
///     not an active runout), so the runout is unmitigated.</item>
/// </list>
/// The gate check lives in <see cref="FilamentRunoutAttentionSource"/>; this evaluator is only
/// invoked when the multi-slot-fallback feature is enabled.
/// </remarks>
public sealed class FilamentRunoutSwitchEvaluator(
    AppDbContext dbContext,
    IFilamentFallbackGroupService fallbackService) : IFilamentRunoutSwitchEvaluator
{
    private const string ActiveRunoutReason = "runout-during-active-job";

    private readonly AppDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IFilamentFallbackGroupService _fallbackService =
        fallbackService ?? throw new ArgumentNullException(nameof(fallbackService));

    /// <inheritdoc />
    public async Task<RunoutSwitchAssessment> AssessAsync(
        FilamentRunoutWarningDto warning,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(warning);

        // Only active runouts are downgrade candidates; queued shortages are handled elsewhere.
        if (warning.Reason != ActiveRunoutReason || string.IsNullOrWhiteSpace(warning.Material))
        {
            return RunoutSwitchAssessment.NoBackup;
        }

        // Fallback groups key on the physical toolhead id; the warning only carries the index.
        Toolhead? source = await _dbContext.Toolheads
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.PrinterId == warning.PrinterId && t.Index == warning.ToolheadIndex,
                cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return RunoutSwitchAssessment.NoBackup;
        }

        AvailableFallbackMember? backup = await _fallbackService
            .FindAvailableFallbackAsync(warning.PrinterId, source.Id, warning.Material!, cancellationToken)
            .ConfigureAwait(false);
        if (backup is null)
        {
            return RunoutSwitchAssessment.NoBackup;
        }

        // Telemetry-confirmed switch requires the printer's live loaded spool to have moved OFF
        // the runout spool onto the backup member's spool, still the same material. Presence of a
        // configured backup (backup != null) is NOT sufficient — that only proves an available
        // slot, never that a switch occurred (issue #711, F6: "never infer a successful switch
        // from configuration alone").
        Printer? printer = await _dbContext.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == warning.PrinterId, cancellationToken)
            .ConfigureAwait(false);

        if (printer?.CurrentSpoolId is int liveSpoolId
            && backup.LoadedSpoolId is int backupSpoolId
            && liveSpoolId == backupSpoolId
            && (warning.SpoolId is not int runoutSpoolId || liveSpoolId != runoutSpoolId)
            && !string.IsNullOrWhiteSpace(printer.CurrentMaterial)
            && string.Equals(printer.CurrentMaterial, warning.Material, StringComparison.OrdinalIgnoreCase))
        {
            return RunoutSwitchAssessment.SwitchConfirmed;
        }

        return RunoutSwitchAssessment.BackupAvailable;
    }
}
