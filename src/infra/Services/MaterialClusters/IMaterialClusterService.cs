using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.MaterialClusters;

public interface IMaterialClusterService
{
    Task<List<MaterialClusterDto>> GetAllClustersAsync(CancellationToken ct = default);

    Task<MaterialClusterDto?> GetClusterByIdAsync(Guid id, CancellationToken ct = default);

    Task<MaterialClusterDto> CreateClusterAsync(CreateMaterialClusterRequest request, CancellationToken ct = default);

    Task<MaterialClusterDto?> UpdateClusterAsync(Guid id, UpdateMaterialClusterRequest request, CancellationToken ct = default);

    Task<bool> DeleteClusterAsync(Guid id, CancellationToken ct = default);

    Task<MaterialClusterDto?> AddMemberAsync(Guid clusterId, Guid filamentTypeId, CancellationToken ct = default);

    Task<bool> RemoveMemberAsync(Guid clusterId, Guid filamentTypeId, CancellationToken ct = default);

    /// <summary>
    /// Returns the names of all filament types that share a cluster with the given filament type name.
    /// Used by the dispatch scorer for cluster-based material matching.
    /// </summary>
    Task<HashSet<string>> GetClusterMateNamesAsync(string filamentTypeName, CancellationToken ct = default);
}

public record MaterialClusterDto(
    Guid Id,
    string Name,
    string? Description,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<MaterialClusterMemberDto> Members);

public record MaterialClusterMemberDto(
    Guid FilamentTypeId,
    string FilamentTypeName,
    DateTime AddedAt);

public record CreateMaterialClusterRequest(
    string Name,
    string? Description,
    List<Guid>? FilamentTypeIds);

public record UpdateMaterialClusterRequest(
    string Name,
    string? Description);
