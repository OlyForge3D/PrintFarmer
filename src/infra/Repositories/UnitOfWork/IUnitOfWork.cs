using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.Folder;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.Harvest;
using Farm.Infrastructure.Repositories.Locations;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Repositories.Printers;

namespace Farm.Infrastructure.Repositories.UnitOfWork
{
    /// <summary>
    /// Unit of Work pattern coordinator for database operations.
    /// Provides access to all repositories while ensuring they share the same DbContext,
    /// enabling atomic transactions across multiple repository operations.
    /// </summary>
    /// <remarks>
    /// The Unit of Work pattern solves multi-context issues by ensuring all repositories
    /// work with the same database context instance. This guarantees:
    /// - Atomic operations across multiple repositories (all-or-nothing transactions)
    /// - Proper entity tracking by a single DbContext
    /// - No FK constraint violations from entities tracked in different contexts
    /// - Clean separation of repository concerns while maintaining transactional integrity
    /// </remarks>
    public interface IUnitOfWork : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Repository for G-code file persistence and retrieval operations.
        /// </summary>
        IGcodeRepository GcodeFiles { get; }

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

        /// <summary>
        /// Repository for 3D model file persistence and retrieval.
        /// Coordinated with folder operations via shared DbContext.
        /// </summary>
        IModel3DFileRepository Model3dFiles { get; }

        /// <summary>
        /// Repository for location (farm site/facility) persistence and retrieval.
        /// Coordinated with printer and location-based operations via shared DbContext.
        /// </summary>
        ILocationRepository Locations { get; }

        /// <summary>
        /// Persists all changes made to entities tracked by any repository in this Unit of Work.
        /// This is a single atomic transaction affecting all repository changes.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>The number of state entries written to the database</returns>
        Task<int> SaveChangesAsync(CancellationToken ct);
    }
}
