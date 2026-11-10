using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Monitoring intent never exempts active work or a backend-reported fault.</summary>
public static class PrinterMonitoringPolicy
{
    /// <summary>Whether a disconnected snapshot is covered by explicit shutdown intent.</summary>
    public static bool IsExpectedOffline(
        Printer printer,
        PrinterStatusCacheSnapshot? snapshot,
        bool hasActiveJob)
    {
        return printer.ExpectedOfflineSinceUtc is DateTime since
            && !hasActiveJob
            && snapshot?.HasActivePrintAtDisconnect != true
            && !HasReconnected(since, snapshot)
            && snapshot?.Status.IsOnline != true
            && IsOfflineState(snapshot?.Status.State);
    }

    /// <summary>Only a producer observation begun after acknowledgement proves reconnect.</summary>
    public static bool HasReconnected(DateTime since, PrinterStatusCacheSnapshot? snapshot) =>
        snapshot?.LastOnlineObservationStartedAtUtc is DateTime observed && observed > since;

    /// <summary>Unknown and error states are not evidence of an intentional shutdown.</summary>
    public static bool IsOfflineState(string? state) =>
        state is null
        || state.Equals("Offline", StringComparison.OrdinalIgnoreCase)
        || state.Equals("Disconnected", StringComparison.OrdinalIgnoreCase)
        || state.Equals("Unreachable", StringComparison.OrdinalIgnoreCase);
}
