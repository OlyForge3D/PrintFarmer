using System;
using System.Collections.Generic;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Centralizes printer-state set classification used across the infrastructure.
/// Keeps related-but-distinct definitions (e.g., "busy for control gating" vs.
/// "currently running an active print job") in one place so they cannot silently
/// diverge.
/// </summary>
public static class PrinterStateClassifier
{
    private static readonly HashSet<string> ActivePrintingJobStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Printing",
        "Heating",
        "Pausing",
        "Paused",
        "Resuming",
    };

    /// <summary>
    /// True when the printer is in any state where an active print job is in
    /// progress (the toolhead may be moving, recently moved, or about to move
    /// again). Used by failure-detection / spaghetti monitoring to decide
    /// whether a snapshot is worth analyzing.
    /// </summary>
    /// <remarks>
    /// This set is intentionally narrower than <see cref="PrinterControlGate"/>'s
    /// busy-for-control set: <c>Cancelling</c> blocks user commands but does not
    /// represent an in-progress print worth monitoring for spaghetti.
    /// </remarks>
    public static bool IsActivePrintingJob(string? state)
        => !string.IsNullOrWhiteSpace(state) && ActivePrintingJobStates.Contains(state.Trim());
}
