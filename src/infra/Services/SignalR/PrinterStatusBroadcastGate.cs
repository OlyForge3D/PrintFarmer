namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// Determines whether a "printerupdated" SignalR broadcast should be suppressed because its
/// payload is identical (full payload-complete equality, per the #1242 spike) to the last
/// broadcast sent for that printer.
/// </summary>
/// <remarks>
/// This is a client-render-churn reduction, not a server bandwidth/CPU optimization — see
/// docs/spike-1242-signalr-broadcast-volume.md. Idle-state updates are byte-identical between
/// polls (100% suppressible); active-print updates change progress/temperature on nearly every
/// poll (only ~9.9% suppressible). Callers must never suppress the first update for a printer —
/// i.e. when there is no cached "last sent" value, such as immediately after a backend restart
/// (in-memory cache cleared) or a printer reconnect (the reconnect update differs from the
/// last-sent offline update, so it is naturally not suppressed by value equality).
/// Relies on <see cref="PrinterStatusUpdate"/> record equality being a true full-payload
/// comparison; this holds only while no populated field contains reference/array-typed data
/// (e.g. <c>MmuStatus.Gates</c>), since record equality compares arrays by reference, not value.
/// </remarks>
public static class PrinterStatusBroadcastGate
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="update"/> should be broadcast: either
    /// there is no previously sent update to compare against, or <paramref name="update"/> differs
    /// from <paramref name="lastSent"/> in any field.
    /// </summary>
    /// <param name="lastSent">The last update actually broadcast for this printer, or <see langword="null"/> if none has been sent yet.</param>
    /// <param name="update">The candidate update to broadcast.</param>
    public static bool ShouldBroadcast(PrinterStatusUpdate? lastSent, PrinterStatusUpdate update) =>
        lastSent is null || !lastSent.Equals(update);
}
