using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Spoolman;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>
/// Adapts filament coverage runout warnings into unified operator attention items.
/// </summary>
/// <remarks>
/// Coverage remains the source of truth for threshold, feature-gate, progress, queue,
/// and source-aware Spoolman semantics. This adapter only translates warnings into the
/// #707 feed contract and intentionally does not advertise the #710 guided-swap action.
/// <para>
/// When the multi-slot-fallback feature is enabled and a switch evaluator is supplied
/// (issue #711, F6, Finding 2), an active runout's <c>Critical</c> severity is downgraded
/// only on real mitigation evidence: telemetry-confirmed switch → <c>Info</c>; a configured,
/// loaded, compatible backup → <c>Warning</c>. Configuration existence alone never downgrades
/// below <c>Warning</c>, and a disabled feature keeps the legacy <c>Critical</c> behaviour.
/// </para>
/// </remarks>
public sealed class FilamentRunoutAttentionSource(
    IFilamentCoverageAttentionSource coverageSource,
    IFilamentRunoutSwitchEvaluator? switchEvaluator = null,
    IOperatorFeatureGate? operatorFeatureGate = null) : IAttentionSource
{
    /// <summary>
    /// Stable timestamp for a continuously-computed runout condition. The same
    /// printer/toolhead condition retains one identity, and a moving read-time timestamp
    /// must not silently bypass an operator's snooze.
    /// </summary>
    public static readonly DateTime StableRunoutOccurredAt =
        DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc);

    private readonly IFilamentCoverageAttentionSource _coverageSource =
        coverageSource ?? throw new ArgumentNullException(nameof(coverageSource));

    private readonly IFilamentRunoutSwitchEvaluator? _switchEvaluator = switchEvaluator;

    private readonly IOperatorFeatureGate? _operatorFeatureGate = operatorFeatureGate;

    /// <inheritdoc />
    public string SourceName => AttentionIdPrefixes.Runout;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FilamentRunoutWarningDto> warnings =
            await _coverageSource.GetRunoutWarningsAsync(cancellationToken).ConfigureAwait(false);
        List<AttentionItemDto> items = new(warnings.Count);

        // The downgrade path is gated behind the multi-slot-fallback feature AND the presence of a
        // switch evaluator; otherwise every active runout retains its legacy Critical severity.
        bool downgradeEnabled = _switchEvaluator is not null
            && (_operatorFeatureGate is null
                || await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, cancellationToken).ConfigureAwait(false));

        foreach (FilamentRunoutWarningDto warning in warnings)
        {
            AttentionItemDto? item = await MapWarningAsync(warning, downgradeEnabled, cancellationToken)
                .ConfigureAwait(false);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private async Task<AttentionItemDto?> MapWarningAsync(
        FilamentRunoutWarningDto warning,
        bool downgradeEnabled,
        CancellationToken cancellationToken)
    {
        bool activeRunout = warning.Reason == "runout-during-active-job";
        bool queuedShortage = warning.Reason == "insufficient-for-assigned-queue";
        if ((!activeRunout && !queuedShortage)
            || (activeRunout && warning.PredictedRunoutAt is null))
        {
            return null;
        }

        string material = string.IsNullOrWhiteSpace(warning.Material) ? "filament" : warning.Material;
        string remaining = warning.RemainingGrams is double grams
            ? $"{Math.Max(0, grams).ToString("0.#", CultureInfo.InvariantCulture)} g"
            : "unknown remaining weight";

        // warning.ToolheadIndex is already the 0-based G-code T-index (issue #711, round-19
        // M19-2 — the coverage DTO now correctly emits the mapped G-code index instead of the raw
        // 1-based stored index for MMU gates). Adding 1 here double-counted that mapping and
        // displayed gate 1 / T0 as "tool 2"; display the value as-is to match gcode T-command
        // convention (T0 = "tool 0").
        int displayTool = warning.ToolheadIndex;

        string title;
        string detail;
        AttentionSeverity severity;
        DateTime? deadline;
        if (activeRunout)
        {
            DateTime runoutAt = warning.PredictedRunoutAt!.Value.ToUniversalTime();
            string runoutBase =
                $"{warning.PrinterName} tool {displayTool} has {remaining} of {material} and is predicted to run out at "
                + $"{runoutAt:yyyy-MM-dd HH:mm} UTC.";

            RunoutSwitchAssessment assessment = downgradeEnabled
                ? await _switchEvaluator!.AssessAsync(warning, cancellationToken).ConfigureAwait(false)
                : RunoutSwitchAssessment.NoBackup;

            switch (assessment)
            {
                case RunoutSwitchAssessment.SwitchConfirmed:
                    title = "Filament auto-switch confirmed";
                    detail = $"{runoutBase} Telemetry confirms printing continued from a configured backup spool of {material}. "
                        + "No action required unless the backup also runs low.";
                    severity = AttentionSeverity.Info;
                    deadline = null;
                    break;
                case RunoutSwitchAssessment.BackupAvailable:
                    title = "Filament runout predicted";
                    detail = $"{runoutBase} A configured backup spool of {material} is available but no switch has been confirmed yet. "
                        + "Action: verify the auto-switch or load sufficient filament before the deadline.";
                    severity = AttentionSeverity.Warning;
                    deadline = runoutAt;
                    break;
                default:
                    title = "Filament runout predicted";
                    detail = $"{runoutBase} Action: load sufficient filament before the deadline.";
                    severity = AttentionSeverity.Critical;
                    deadline = runoutAt;
                    break;
            }
        }
        else
        {
            title = "Queued filament shortage";
            detail = $"{warning.PrinterName} tool {displayTool} has {remaining} of {material}, which does not cover its assigned queue. "
                + "Action: load sufficient filament before dispatching the queued work.";
            severity = AttentionSeverity.Warning;
            deadline = null;
        }

        return new AttentionItemDto(
            Id: BuildItemId(warning.PrinterId, warning.ToolheadIndex),
            Kind: AttentionKind.Runout,
            Severity: severity,
            PrinterId: warning.PrinterId,
            PrinterName: warning.PrinterName,
            Title: title,
            Detail: detail,
            OccurredAt: StableRunoutOccurredAt,
            Actions:
            [
                new AttentionActionDto(
                    AttentionActionKind.Snooze,
                    "Snooze",
                    RequiresConfirmation: false),
            ],
            ToolheadIndex: warning.ToolheadIndex,
            DeadlineAt: deadline,
            AllowFreshOccurrenceBypass: false);
    }

    private static string BuildItemId(Guid printerId, int toolheadIndex)
        => $"{AttentionIdPrefixes.Runout}:{printerId:D}:toolhead:{toolheadIndex}";
}
