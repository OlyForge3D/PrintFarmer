using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Gcode.PrinterFiles;

/// <summary>Current database-backed authority for catalog and deferred actions.</summary>
public interface IPrinterFileAccessService
{
    Task<bool> CanReadAsync(Guid actorId, Guid printerId, CancellationToken ct = default);

    Task<bool> CanAccessLocalAsync(
        Guid actorId,
        Guid gcodeFileId,
        PrinterGroupAccessLevel minimumAccess = PrinterGroupAccessLevel.View,
        CancellationToken ct = default);

    Task<bool> CanActAsync(
        Guid actorId,
        Guid printerId,
        PrinterFileAction action,
        Guid? destinationPrinterId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Set-based catalog authority over candidate printers, equal per printer to
    /// <see cref="CanReadAsync"/>, Manage group access on readable printers, and
    /// <see cref="CanActAsync"/> for Copy and Delete. Uses a bounded number of queries
    /// independent of the candidate count and never caches authority across calls.
    /// </summary>
    Task<PrinterFileCatalogAuthority> GetCatalogAuthorityAsync(
        Guid actorId,
        IReadOnlyCollection<Guid> printerIds,
        CancellationToken ct = default);

    /// <summary>Set-based equivalent of <see cref="CanAccessLocalAsync"/>.</summary>
    Task<IReadOnlySet<Guid>> FilterAccessibleLocalAsync(
        Guid actorId,
        IReadOnlyCollection<Guid> gcodeFileIds,
        PrinterGroupAccessLevel minimumAccess = PrinterGroupAccessLevel.View,
        CancellationToken ct = default);
}

/// <summary>Per-printer catalog authority for one actor at one point in time.</summary>
public sealed record PrinterFileCatalogAuthority(
    IReadOnlySet<Guid> Readable,
    IReadOnlySet<Guid> Manageable,
    IReadOnlySet<Guid> Copyable,
    IReadOnlySet<Guid> Deletable)
{
    public static PrinterFileCatalogAuthority None { get; } =
        new(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());
}

/// <summary>Shared finite I/O slots and one exclusive feature owner per printer.</summary>
public interface IPrinterFileLeaseCoordinator
{
    Task<IPrinterFileLease?> TryAcquireAsync(
        Guid printerId,
        bool interactive,
        CancellationToken ct = default);

    /// <summary>Bypasses discovery scheduling but requires confirmed idle throughout ownership.</summary>
    Task<IPrinterFileLease?> TryAcquireForRefreshAsync(Guid printerId, CancellationToken ct = default);
}

/// <summary>
/// Renewable ownership. Lease loss cancels Token; callers must also validate
/// current ownership immediately before committing publication or mutation.
/// </summary>
public interface IPrinterFileLease : IAsyncDisposable
{
    Guid PrinterId { get; }

    Guid OwnerId { get; }

    long Fence { get; }

    long ConfigurationGeneration { get; }

    long MutationGeneration { get; }

    CancellationToken Token { get; }

    Task<bool> IsCurrentAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes database write barriers for current configuration and ownership in
    /// the caller's transaction. Publication must commit in that same transaction.
    /// </summary>
    Task<bool> FencePublicationAsync(AppDbContext db, CancellationToken ct = default);
}
