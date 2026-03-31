using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Dispatch-related state for a printer, stored separately from the Printer entity
/// to avoid RowVersion contention between user edits and background dispatch operations.
/// AutoDispatchService writes these fields ~14× per cycle; isolating them in their own
/// table with their own RowVersion prevents DbUpdateConcurrencyException on the Printer row.
/// </summary>
public class PrinterDispatchState
{
    /// <summary>
    /// PK and FK — mirrors <see cref="Printer.Id"/> (1:1 relationship).
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Navigation back to the parent printer.
    /// </summary>
    public Printer Printer { get; set; } = null!;

    /// <summary>
    /// Current auto-dispatch ready-gate workflow state.
    /// </summary>
    public AutoDispatchState AutoDispatchState { get; set; } = AutoDispatchState.None;

    /// <summary>
    /// Indicates the operator has pre-confirmed the bed is clear.
    /// </summary>
    public bool BedPreConfirmed { get; set; }

    /// <summary>
    /// Independent concurrency token — bumps only when dispatch state changes,
    /// leaving the Printer.RowVersion undisturbed.
    /// </summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
