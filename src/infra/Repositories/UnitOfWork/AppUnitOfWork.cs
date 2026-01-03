using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Folder;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.Harvest;
using Farm.Infrastructure.Repositories.Locations;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Repositories.Printers;

namespace Farm.Infrastructure.Repositories.UnitOfWork
{
    /// <summary>
    /// Unit of Work implementation providing coordinated access to all repositories.
    /// Ensures all repositories share a single DbContext instance for atomic transactions.
    /// </summary>
    public class AppUnitOfWork : IUnitOfWork
    {
        private readonly AppDbContext _db;
        private IGcodeRepository? _gcodeRepository;
        private IHarvestRepository? _harvestRepository;
        private IPrintersRepository? _printersRepository;
        private IFolderRepository? _folderRepository;
        private IModel3DFileRepository? _model3dFileRepository;
        private ILocationRepository? _locationRepository;

        public AppUnitOfWork(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Lazy-initializes the G-code repository, reusing the same DbContext.
        /// </summary>
        public IGcodeRepository GcodeFiles => _gcodeRepository ??= new EfGcodeRepository(_db);

        /// <summary>
        /// Lazy-initializes the Harvest repository, reusing the same DbContext.
        /// This ensures both repositories work with the same context, preventing FK constraint issues.
        /// </summary>
        public IHarvestRepository HarvestOperations => _harvestRepository ??= new EfHarvestRepository(_db);

        /// <summary>
        /// Lazy-initializes the Printers repository, reusing the same DbContext.
        /// Coordinated with harvest operations for cascading updates.
        /// </summary>
        public IPrintersRepository Printers => _printersRepository ??= new EfPrintersRepository(_db);

        /// <summary>
        /// Lazy-initializes the Folders repository, reusing the same DbContext.
        /// Coordinated with gcode file operations for file hierarchy consistency.
        /// </summary>
        public IFolderRepository Folders => _folderRepository ??= new EfFolderRepository(_db);

        /// <summary>
        /// Lazy-initializes the 3D Model File repository, reusing the same DbContext.
        /// Coordinated with folder operations for model file organization.
        /// </summary>
        public IModel3DFileRepository Model3dFiles => _model3dFileRepository ??= new EfModel3DFileRepository(_db);

        /// <summary>
        /// Lazy-initializes the Location repository, reusing the same DbContext.
        /// Coordinated with printer operations for location-based organization.
        /// </summary>
        public ILocationRepository Locations => _locationRepository ??= new EfLocationRepository(_db);

        /// <summary>
        /// Persists all pending changes from both repositories in a single atomic transaction.
        /// </summary>
        public async Task<int> SaveChangesAsync(CancellationToken ct)
        {
            return await _db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Disposes the Unit of Work and its shared DbContext synchronously.
        /// Note: DbContext is injected via dependency injection and should be managed by DI container.
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Asynchronously disposes the Unit of Work and its shared DbContext.
        /// Note: DbContext is injected via dependency injection and should be managed by DI container.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await ValueTask.CompletedTask;
            GC.SuppressFinalize(this);
        }
    }
}
