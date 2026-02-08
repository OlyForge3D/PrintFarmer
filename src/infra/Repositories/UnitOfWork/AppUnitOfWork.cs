using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Repositories.Folder;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.Harvest;
using Farm.Infrastructure.Repositories.Locations;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services.Security;

namespace Farm.Infrastructure.Repositories.UnitOfWork;

/// <summary>
/// Unit of Work implementation providing coordinated access to all repositories.
/// Ensures all repositories share a single DbContext instance for atomic transactions.
/// </summary>
public class AppUnitOfWork(AppDbContext db, ISensitiveDataProtector sensitiveDataProtector) : IUnitOfWork
{
#pragma warning disable CA2213 // DbContext is injected and managed by DI container lifetime
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ISensitiveDataProtector _sensitiveDataProtector = sensitiveDataProtector ?? throw new ArgumentNullException(nameof(sensitiveDataProtector));
#pragma warning restore CA2213
    private ICameraRepository? _cameraRepository;
    private IGcodeRepository? _gcodeRepository;
    private IHarvestRepository? _harvestRepository;
#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    private IPrintersRepository? _printersRepository;
#pragma warning restore CA1859 // Use concrete types when possible for improved performance
    private IFolderRepository? _folderRepository;
    private IModel3DFileRepository? _model3dFileRepository;
    private ILocationRepository? _locationRepository;
    private IQueueRepository? _queueRepository;
    private ITagRepository? _tagRepository;

    /// <summary>
    /// Lazy-initializes the Camera repository, reusing the same DbContext.
    /// For standalone cameras not attached to printers.
    /// </summary>
    public ICameraRepository Cameras => _cameraRepository ??= new EfCameraRepository(_db);

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
    public IPrintersRepository Printers => _printersRepository ??= new EfPrintersRepository(_db, _sensitiveDataProtector);

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
    /// Lazy-initializes the Queue repository, reusing the same DbContext.
    /// Coordinated with printer and gcode operations for job queue management.
    /// </summary>
    public IQueueRepository Queue => _queueRepository ??= new EfQueueRepository(_db);

    /// <summary>
    /// Lazy-initializes the Tag repository, reusing the same DbContext.
    /// Coordinated with tag operations for generic tagging support.
    /// Tag mappings are now managed via EF Core skip-navigation on StoredFile.Tags.
    /// </summary>
    public ITagRepository Tags => _tagRepository ??= new EfTagRepository(_db);

    /// <summary>
    /// Persists all pending changes from both repositories in a single atomic transaction.
    /// Automatically encrypts sensitive fields on Printer entities before saving.
    /// </summary>
    public async Task<int> SaveChangesAsync(CancellationToken ct)
    {
        // Encrypt sensitive fields on any modified Printer entities before saving.
        // This is the encryption counterpart to PopulateCredential/DecryptIfNeeded.
        // Only runs if the Printers repository was accessed (meaning Printer entities may have been modified).
        _printersRepository?.EncryptSensitiveFieldsOnTrackedEntities();

        return await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Disposes the Unit of Work.
    /// Note: DbContext is injected and managed by DI container, not disposed here.
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously disposes the Unit of Work.
    /// Note: DbContext is injected and managed by DI container, not disposed here.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask;
        GC.SuppressFinalize(this);
    }
}
