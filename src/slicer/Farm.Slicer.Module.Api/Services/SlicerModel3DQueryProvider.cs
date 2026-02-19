using Farm.Infrastructure.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Provides Model3D ID queries backed by <see cref="SlicerDbContext"/>.
/// Registered by <see cref="SlicerApiExtensions.AddSlicerApiServices"/>.
/// </summary>
public class SlicerModel3DQueryProvider(SlicerDbContext db) : IModel3DQueryProvider
{
    private readonly SlicerDbContext _db = db;

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct)
        => await _db.Set<Model3D>().AnyAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<Guid>> GetAllIdsAsync(CancellationToken ct)
        => await _db.Set<Model3D>().Select(m => m.Id).ToListAsync(ct);

    public async Task<DateTime?> GetLatestUpdatedAtAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return null;
        }

        return await _db.Set<Model3D>()
            .Where(m => idList.Contains(m.Id))
            .MaxAsync(m => (DateTime?)m.UpdatedAt, ct);
    }
}
