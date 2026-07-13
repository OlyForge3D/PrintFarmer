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
/// (<see cref="IFilamentFallbackGroupService.GetAvailableFallbacksAsync"/>) with live printer
/// telemetry to grade a runout's mitigation evidence (issue #711, F6 remediation, Finding 2).
/// </summary>
/// <remarks>
/// Severity policy realised here:
/// <list type="bullet">
///   <item><b>SwitchConfirmed</b> — fresh MMU telemetry identifies a configured compatible
///     fallback member as the active tool/gate. This is the only tier that permits an
///     informational downgrade.</item>
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
    IFilamentFallbackGroupService fallbackService,
    IPrinterStatusCacheReader printerStatusCache) : IFilamentRunoutSwitchEvaluator
{
    private const string ActiveRunoutReason = "runout-during-active-job";

    private readonly AppDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IFilamentFallbackGroupService _fallbackService =
        fallbackService ?? throw new ArgumentNullException(nameof(fallbackService));

    private readonly IPrinterStatusCacheReader _printerStatusCache =
        printerStatusCache ?? throw new ArgumentNullException(nameof(printerStatusCache));

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

        // The warning carries a stored toolhead index but fallback chains key on stable IDs.
        List<Toolhead> toolheads = await _dbContext.Toolheads
            .AsNoTracking()
            .Where(t => t.PrinterId == warning.PrinterId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<Toolhead> sourceCandidates =
            [.. toolheads.Where(t => t.Index == warning.ToolheadIndex)];
        if (sourceCandidates.Count == 0)
        {
            return RunoutSwitchAssessment.NoBackup;
        }

        IReadOnlyDictionary<FilamentFallbackLookupKey, FilamentFallbackResolution> resolutions =
            await _fallbackService
            .GetAvailableFallbacksAsync([warning.PrinterId], cancellationToken)
            .ConfigureAwait(false);

        List<FilamentFallbackChainMember> configuredBackups = [];
        foreach (Toolhead source in sourceCandidates)
        {
            FilamentFallbackLookupKey key = FilamentFallbackLookupKey.Create(
                warning.PrinterId,
                source.Id,
                warning.Material);
            if (!resolutions.TryGetValue(key, out FilamentFallbackResolution? resolution))
            {
                continue;
            }

            configuredBackups.AddRange(resolution.Members.Where(member =>
                !string.IsNullOrWhiteSpace(member.LoadedMaterial)
                && string.Equals(
                    member.LoadedMaterial,
                    warning.Material,
                    StringComparison.OrdinalIgnoreCase)));
        }

        if (configuredBackups.Count == 0)
        {
            return RunoutSwitchAssessment.NoBackup;
        }

        PrinterStatusCacheSnapshot? snapshot = _printerStatusCache.GetSnapshot(warning.PrinterId);
        MmuStatusDto? mmuStatus = PrinterStatusFreshness.IsFreshOnline(snapshot, DateTime.UtcNow)
            ? snapshot!.Status.MmuStatus
            : null;
        if (mmuStatus is not { Enabled: true })
        {
            return RunoutSwitchAssessment.BackupAvailable;
        }

        Dictionary<Guid, Toolhead> toolheadsById = toolheads.ToDictionary(t => t.Id);
        foreach (FilamentFallbackChainMember backup in configuredBackups)
        {
            if (toolheadsById.TryGetValue(backup.ToolheadId, out Toolhead? toolhead)
                && IsActiveFallback(toolhead, mmuStatus)
                && LiveMaterialMatches(toolhead, mmuStatus, warning.Material))
            {
                return RunoutSwitchAssessment.SwitchConfirmed;
            }
        }

        return RunoutSwitchAssessment.BackupAvailable;
    }

    private static bool IsActiveFallback(Toolhead toolhead, MmuStatusDto status)
    {
        int? mappedIndex = ToolheadIndexMapper.ToGcodeToolIndex(toolhead);
        if (!mappedIndex.HasValue)
        {
            return false;
        }

        return toolhead.ToolheadType == ToolheadType.MmuGate
            ? status.ActiveGate == mappedIndex.Value
            : status.ActiveTool == mappedIndex.Value || status.ActiveGate == mappedIndex.Value;
    }

    private static bool LiveMaterialMatches(
        Toolhead toolhead,
        MmuStatusDto status,
        string requiredMaterial)
    {
        int? mappedIndex = ToolheadIndexMapper.ToGcodeToolIndex(toolhead);
        MmuGateDto? liveGate = mappedIndex.HasValue
            ? status.Gates.FirstOrDefault(gate => gate.Index == mappedIndex.Value)
            : null;
        return string.IsNullOrWhiteSpace(liveGate?.Material)
            || string.Equals(liveGate.Material, requiredMaterial, StringComparison.OrdinalIgnoreCase);
    }
}
