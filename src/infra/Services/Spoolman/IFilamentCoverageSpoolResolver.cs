using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Resolves assigned spool state within each printer's owning Spoolman source.
/// </summary>
public interface IFilamentCoverageSpoolResolver
{
    /// <summary>
    /// Resolves all assigned spool IDs, batching and caching independently by source.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>> ResolveAsync(
        IReadOnlyList<Printer> printers,
        CancellationToken ct);
}

/// <summary>
/// Source-scoped spool state used by coverage computation.
/// </summary>
/// <param name="Spool">Resolved spool, or null when unavailable/missing.</param>
/// <param name="TracksLiveConsumption">Whether the source updates remaining weight during an active print.</param>
/// <param name="ErrorReason">Machine-readable unknown reason when <paramref name="Spool"/> is null.</param>
public sealed record FilamentCoverageSpoolSnapshot(
    SpoolmanSpoolDto? Spool,
    bool TracksLiveConsumption,
    string? ErrorReason);
