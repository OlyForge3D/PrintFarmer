using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Discovery progress events for SignalR streaming
/// <summary>
/// Periodic progress update for an active network discovery session.
/// </summary>
public record DiscoveryProgressDto(
    string SessionId,
    string CurrentNetwork,
    string CurrentIp,
    int TotalIps,
    int ScannedIps,
    int PrintersFound,
    int PrintersExcluded,
    double ProgressPercentage,
    DiscoveryStatus Status,
    string? Message = null,
    IReadOnlyList<string>? NetworkRanges = null,
    bool AutoDetectedNetworks = false);

/// <summary>
/// Event published when a printer is found during discovery.
/// </summary>
public record DiscoveryPrinterFoundDto(
    string SessionId,
    DiscoveredPrinterDto Printer);
#pragma warning restore CA1002
