using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Spoolman;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>
/// Adapts filament coverage runout warnings into unified operator attention items.
/// </summary>
/// <remarks>
/// Coverage remains the source of truth for threshold, feature-gate, progress, queue,
/// and source-aware Spoolman semantics. This adapter only translates warnings into the
/// #707 feed contract and intentionally does not advertise the #710 guided-swap action.
/// </remarks>
public sealed class FilamentRunoutAttentionSource(
    IFilamentCoverageAttentionSource coverageSource) : IAttentionSource
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

    /// <inheritdoc />
    public string SourceName => AttentionIdPrefixes.Runout;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FilamentRunoutWarningDto> warnings =
            await _coverageSource.GetRunoutWarningsAsync(cancellationToken).ConfigureAwait(false);
        List<AttentionItemDto> items = new(warnings.Count);

        foreach (FilamentRunoutWarningDto warning in warnings)
        {
            AttentionItemDto? item = MapWarning(warning);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static AttentionItemDto? MapWarning(FilamentRunoutWarningDto warning)
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
        int displayTool = warning.ToolheadIndex + 1;

        string title;
        string detail;
        AttentionSeverity severity;
        DateTime? deadline;
        if (activeRunout)
        {
            DateTime runoutAt = warning.PredictedRunoutAt!.Value.ToUniversalTime();
            title = "Filament runout predicted";
            detail = $"{warning.PrinterName} tool {displayTool} has {remaining} of {material} and is predicted to run out at "
                + $"{runoutAt:yyyy-MM-dd HH:mm} UTC. Action: load sufficient filament before the deadline.";
            severity = AttentionSeverity.Critical;
            deadline = runoutAt;
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
