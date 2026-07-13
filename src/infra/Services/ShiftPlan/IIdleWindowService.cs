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
}
