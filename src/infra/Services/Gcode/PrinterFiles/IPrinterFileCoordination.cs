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
}

/// <summary>Shared finite I/O slots and one exclusive feature owner per printer.</summary>
public interface IPrinterFileLeaseCoordinator
{
    Task<IPrinterFileLease?> TryAcquireAsync(
        Guid printerId,
        bool interactive,
        CancellationToken ct = default);
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
