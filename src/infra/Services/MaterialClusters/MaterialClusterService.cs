using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.MaterialClusters;

public class MaterialClusterService(AppDbContext db, ILogger<MaterialClusterService> logger) : IMaterialClusterService
{
    public async Task<List<MaterialClusterDto>> GetAllClustersAsync(CancellationToken ct = default)
    {
        List<MaterialCluster> clusters = await db.MaterialClusters
            .Include(c => c.Members)
                .ThenInclude(m => m.FilamentType)
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        return clusters.Select(ToDto).ToList();
    }

    public async Task<MaterialClusterDto?> GetClusterByIdAsync(Guid id, CancellationToken ct = default)
    {
        MaterialCluster? cluster = await db.MaterialClusters
            .Include(c => c.Members)
                .ThenInclude(m => m.FilamentType)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return cluster is null ? null : ToDto(cluster);
    }

    public async Task<MaterialClusterDto> CreateClusterAsync(CreateMaterialClusterRequest request, CancellationToken ct = default)
    {
        var cluster = new MaterialCluster
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        if (request.FilamentTypeIds is { Count: > 0 })
        {
            List<Guid> validIds = await db.FilamentTypes
                .Where(f => request.FilamentTypeIds.Contains(f.Id) && f.IsActive)
                .Select(f => f.Id)
                .ToListAsync(ct);

            foreach (Guid ftId in validIds)
            {
                cluster.Members.Add(new MaterialClusterMember
                {
                    ClusterId = cluster.Id,
                    FilamentTypeId = ftId,
                    AddedAt = DateTime.UtcNow
                });
            }
        }

        db.MaterialClusters.Add(cluster);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created material cluster '{Name}' with {Count} members", cluster.Name, cluster.Members.Count);

        return (await GetClusterByIdAsync(cluster.Id, ct))!;
    }

    public async Task<MaterialClusterDto?> UpdateClusterAsync(Guid id, UpdateMaterialClusterRequest request, CancellationToken ct = default)
    {
        MaterialCluster? cluster = await db.MaterialClusters.FindAsync([id], ct);
        if (cluster is null)
        {
            return null;
        }

        cluster.Name = request.Name.Trim();
        cluster.Description = request.Description?.Trim();
        cluster.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Updated material cluster '{Name}'", cluster.Name);

        return await GetClusterByIdAsync(id, ct);
    }

    public async Task<bool> DeleteClusterAsync(Guid id, CancellationToken ct = default)
    {
        MaterialCluster? cluster = await db.MaterialClusters.FindAsync([id], ct);
        if (cluster is null)
        {
            return false;
        }

        db.MaterialClusters.Remove(cluster);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deleted material cluster '{Name}'", cluster.Name);
        return true;
    }

    public async Task<MaterialClusterDto?> AddMemberAsync(Guid clusterId, Guid filamentTypeId, CancellationToken ct = default)
    {
        MaterialCluster? cluster = await db.MaterialClusters.FindAsync([clusterId], ct);
        if (cluster is null)
        {
            return null;
        }

        bool exists = await db.MaterialClusterMembers
            .AnyAsync(m => m.ClusterId == clusterId && m.FilamentTypeId == filamentTypeId, ct);
        if (exists)
        {
            return await GetClusterByIdAsync(clusterId, ct);
        }

        bool filamentExists = await db.FilamentTypes.AnyAsync(f => f.Id == filamentTypeId && f.IsActive, ct);
        if (!filamentExists)
        {
            return null;
        }

        db.MaterialClusterMembers.Add(new MaterialClusterMember
        {
            ClusterId = clusterId,
            FilamentTypeId = filamentTypeId,
            AddedAt = DateTime.UtcNow
        });

        cluster.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Added filament type {FilamentTypeId} to cluster '{ClusterName}'", filamentTypeId, cluster.Name);

        return await GetClusterByIdAsync(clusterId, ct);
    }

    public async Task<bool> RemoveMemberAsync(Guid clusterId, Guid filamentTypeId, CancellationToken ct = default)
    {
        MaterialClusterMember? member = await db.MaterialClusterMembers
            .FirstOrDefaultAsync(m => m.ClusterId == clusterId && m.FilamentTypeId == filamentTypeId, ct);

        if (member is null)
        {
            return false;
        }

        db.MaterialClusterMembers.Remove(member);

        MaterialCluster? cluster = await db.MaterialClusters.FindAsync([clusterId], ct);
        if (cluster is not null)
        {
            cluster.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Removed filament type {FilamentTypeId} from cluster {ClusterId}", filamentTypeId, clusterId);
        return true;
    }

    public async Task<HashSet<string>> GetClusterMateNamesAsync(string filamentTypeName, CancellationToken ct = default)
    {
        // Find all clusters that contain a filament type with this name
        List<Guid> clusterIds = await db.MaterialClusterMembers
            .Include(m => m.FilamentType)
            .Where(m => EF.Functions.Like(m.FilamentType.Name, filamentTypeName))
            .Select(m => m.ClusterId)
            .Distinct()
            .ToListAsync(ct);

        if (clusterIds.Count == 0)
        {
            return [];
        }

        // Get all filament type names in those clusters
        List<string> names = await db.MaterialClusterMembers
            .Include(m => m.FilamentType)
            .Where(m => clusterIds.Contains(m.ClusterId))
            .Select(m => m.FilamentType.Name)
            .Distinct()
            .ToListAsync(ct);

        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    private static MaterialClusterDto ToDto(MaterialCluster cluster)
    {
        return new MaterialClusterDto(
            cluster.Id,
            cluster.Name,
            cluster.Description,
            cluster.CreatedAt,
            cluster.UpdatedAt,
            cluster.Members.Select(m => new MaterialClusterMemberDto(
                m.FilamentTypeId,
                m.FilamentType.Name,
                m.AddedAt)).ToList());
    }
}
