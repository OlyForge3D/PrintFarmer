using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Completion summary for a network discovery session.
/// </summary>
public record DiscoveryCompletedDto(
    string SessionId,
    int TotalPrintersFound,
    int TotalPrintersExcluded,
    TimeSpan Duration,
    bool WasCancelled = false,
    IReadOnlyList<string>? NetworkRanges = null,
    bool AutoDetectedNetworks = false);
#pragma warning restore CA1002
