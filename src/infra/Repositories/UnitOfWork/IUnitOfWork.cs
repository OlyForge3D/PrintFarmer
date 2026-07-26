using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Repositories.Folder;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.Harvest;
using Farm.Infrastructure.Repositories.Locations;
using Farm.Infrastructure.Repositories.PartsInventory;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Repositories.Tags;
using Microsoft.EntityFrameworkCore.Storage;

namespace Farm.Infrastructure.Repositories.UnitOfWork;

/// <summary>
/// Unit of Work pattern coordinator for database operations.
/// Provides access to all repositories while ensuring they share the same DbContext.
/// </summary>
/// <remarks>
/// <para>
/// The Unit of Work pattern solves multi-context issues by ensuring all repositories
/// work with the same database context instance. This guarantees:
/// </para>
/// <list type="bullet">
///   <item>Proper entity tracking by a single DbContext across repositories</item>
///   <item>No FK constraint violations from entities tracked in different contexts</item>
///   <item>A single call to <see cref="SaveChangesAsync"/> flushes every repository's tracked change set as one atomic <c>SaveChanges</c> write</item>
///   <item>Clean separation of repository concerns while maintaining transactional integrity</item>
/// </list>
/// <para>
/// <b>Atomicity boundaries — read carefully.</b> Repository code that mixes
/// <c>ExecuteDeleteAsync</c> (or any other bulk-update / raw-SQL statement) with tracked
/// entity work does NOT get all-or-nothing behavior by default: <c>ExecuteDeleteAsync</c>
/// commits its own statement immediately in the provider's implicit transaction, while
/// tracked-entity writes wait for <c>SaveChanges</c>. To make a compensating-delete +
/// parent-delete pair truly transactional, the caller MUST open an explicit
/// <see cref="IDbContextTransaction"/> via <see cref="BeginOwnedTransactionAsync"/> around
/// the whole sequence. See the Dallas cascade adjudication for #953 (which introduced
/// this primitive) for the reference pattern.
/// </para>
/// </remarks>
public interface IUnitOfWork : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Repository for G-code file persistence and retrieval operations.
    /// </summary>
    IGcodeRepository GcodeFiles { get; }

    /// <summary>
    /// Repository for standalone camera persistence and retrieval.
    /// Cameras not attached to printers, managed separately.
    /// </summary>
    ICameraRepository Cameras { get; }

    /// <summary>
    /// Repository for harvest operation and discovered file persistence and retrieval.
    /// </summary>
    IHarvestRepository HarvestOperations { get; }

    /// <summary>
    /// Repository for printer configuration and persistence.
    /// Coordinated with harvest operations via shared DbContext.
    /// </summary>
    IPrintersRepository Printers { get; }

    /// <summary>
    /// Repository for gcode folder organization and file hierarchy.
    /// Coordinated with gcode file operations via shared DbContext.
    /// </summary>
    IFolderRepository Folders { get; }

    // Note: IModel3DFileRepository removed — Model3D repos are now in Farm.Slicer.Module.
    // Use IModel3DFileRepository from Farm.Slicer.Module.Data.Repositories instead.

    /// <summary>
    /// Repository for location (farm site/facility) persistence and retrieval.
    /// Coordinated with printer and location-based operations via shared DbContext.
    /// </summary>
    ILocationRepository Locations { get; }

    /// <summary>
    /// Repository for print job queue and job persistence.
    /// Coordinated with printer and gcode operations via shared DbContext.
    /// </summary>
    IQueueRepository Queue { get; }

    /// <summary>
    /// Repository for guided filament-swap override audit records (issue #710).
    /// Shares the DbContext so an override audit commits atomically with the spool binding.
    /// </summary>
    IFilamentSwapOverrideRepository FilamentSwapOverrides { get; }

    /// <summary>
    /// Repository for job-output → SKU mappings (PartOutputMappings). Shares the DbContext
    /// so direct-mapping deletions commit atomically with the parent GcodeFile deletion,
    /// which the Dallas cascade adjudication for #953 now requires because the direct
    /// GcodeFile → PartOutputMapping FK is Restrict rather than Cascade.
    /// </summary>
    IPartOutputMappingRepository PartOutputMappings { get; }

    /// <summary>
    /// Begins a database transaction owned by the caller when the provider is relational
    /// AND no outer transaction is already in progress; otherwise returns <c>null</c> so
    /// the caller rides on the existing outer transaction. Callers must commit/rollback
    /// (and dispose) ONLY when the returned handle is non-null.
    ///
    /// This is the coordination primitive the Dallas cascade fix for #953 uses to make
    /// compensating deletes (schedules, PartOutputMappings, harvest-import mappings, etc.)
    /// atomic with the parent SaveChanges — a later failure rolls back the earlier
    /// <c>ExecuteDeleteAsync</c> writes. It cooperates safely with outer transactions
    /// opened by callers such as <c>DataImportService.ImportFullBackupAsync</c> Replace mode.
    /// Returns <c>null</c> on in-memory / non-relational providers where transactions are
    /// not required (or supported).
    /// </summary>
    Task<IDbContextTransaction?> BeginOwnedTransactionAsync(CancellationToken ct);

    /// <summary>
    /// Repository for tag persistence and retrieval (generic tags).
    /// Tag mappings are now managed via EF Core skip-navigation on StoredFile.Tags.
    /// </summary>
    ITagRepository Tags { get; }

    /// <summary>
    /// Persists all changes tracked by any repository in this Unit of Work with a single
    /// <c>SaveChanges</c> write. This IS all-or-nothing for tracked-entity changes; note
    /// that <c>ExecuteDeleteAsync</c> and other bulk-update statements are NOT participants
    /// in this batch and commit immediately in the provider's implicit transaction unless
    /// wrapped in an outer <see cref="IDbContextTransaction"/> via
    /// <see cref="BeginOwnedTransactionAsync"/>.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The number of state entries written to the database</returns>
    Task<int> SaveChangesAsync(CancellationToken ct);
}
