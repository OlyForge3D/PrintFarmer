using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.ShiftPlan;

/// <summary>
/// A projected idle window on a printer's queue timeline.
/// </summary>
/// <param name="PrinterId">Owning printer.</param>
/// <param name="PrinterName">Display name (denormalized for UI convenience).</param>
/// <param name="StartUtc">Window start (UTC).</param>
/// <param name="EndUtc">Window end (UTC), or <c>DateTime.MaxValue</c> when unbounded.</param>
/// <param name="IsDispatchEligibleNow">
/// True when the printer currently has an eligible unassigned shared-queue job
/// awaiting dispatch (dispatch threshold met). Callers use this to enforce the
/// "a printer is not idle when a dispatchable job is waiting" invariant per
/// Dallas's F8 acceptance addendum on #713.
/// </param>
public sealed record IdleWindow(
    Guid PrinterId,
    string PrinterName,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsDispatchEligibleNow);

/// <summary>
/// Outcome of an idle-window computation that, in addition to the conclusively
/// determined <see cref="Windows"/>, surfaces the printers whose dispatch
/// eligibility could not be determined this pass (every evaluated candidate's
/// scoring threw). Those printers are ABSENT from <see cref="Windows"/> — a
/// caller that must fail closed on an ambiguous eligibility signal (e.g. the
/// maintenance source, which would otherwise let the compiler auto-complete a
/// still-active task) inspects <see cref="IndeterminatePrinterIds"/> to detect
/// the outage instead of silently observing "no window" (issue #713 Fix R4-1).
/// </summary>
/// <param name="Windows">Idle windows conclusively determined this pass.</param>
/// <param name="IndeterminatePrinterIds">
/// Printers excluded from <paramref name="Windows"/> specifically because their
/// dispatch eligibility was indeterminate (a scorer outage), NOT because they are
/// busy, have no projected window, or were conclusively found ineligible.
/// </param>
public sealed record IdleWindowResult(
    IReadOnlyList<IdleWindow> Windows,
    IReadOnlySet<Guid> IndeterminatePrinterIds);

/// <summary>
/// Computes projected idle windows across printers using the same eligibility
/// signals the dispatcher uses, so the shift-plan compiler cannot suggest work
/// during a slot the dispatcher would fill.
/// </summary>
public interface IIdleWindowService
{
    /// <summary>
    /// Returns idle windows per printer given the current queue state. Windows
    /// shorter than <paramref name="minWindow"/> are skipped. Printers with a
    /// dispatchable unassigned shared-queue job are reported with a
    /// <see cref="IdleWindow.IsDispatchEligibleNow"/> flag so callers can
    /// exclude those from "idle" reasoning.
    /// </summary>
    Task<IReadOnlyList<IdleWindow>> GetIdleWindowsAsync(TimeSpan minWindow, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="GetIdleWindowsAsync"/> but additionally reports, via
    /// <see cref="IdleWindowResult.IndeterminatePrinterIds"/>, the printers whose
    /// dispatch eligibility was indeterminate this pass (every evaluated candidate's
    /// scoring threw). Such printers are excluded from
    /// <see cref="IdleWindowResult.Windows"/> exactly as they are from
    /// <see cref="GetIdleWindowsAsync"/>, but a caller that must not fail open — one
    /// that would otherwise treat a scorer outage as "printer has no maintenance
    /// window" and let the compiler auto-complete a still-active task — can now
    /// distinguish an outage from a genuinely absent window (issue #713 Fix R4-1).
    /// </summary>
    Task<IdleWindowResult> GetIdleWindowsWithIndeterminateAsync(TimeSpan minWindow, CancellationToken ct = default);
}
