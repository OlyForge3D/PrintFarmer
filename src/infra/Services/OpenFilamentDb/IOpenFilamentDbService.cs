using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.OpenFilamentDb;

namespace Farm.Infrastructure.Services.OpenFilamentDb;

/// <summary>
/// Fetches filament data from the Open Filament Database (openfilamentdatabase.org).
/// Results are cached in-memory.
/// </summary>
public interface IOpenFilamentDbService
{
    /// <summary>Gets all brands (cached).</summary>
    Task<IReadOnlyList<OfdBrand>> GetBrandsAsync(CancellationToken ct);

    /// <summary>Gets materials for a specific brand.</summary>
    Task<OfdBrandDetailResponse> GetBrandDetailAsync(string brandSlug, CancellationToken ct);

    /// <summary>Gets filaments with all variants and sizes for a brand + material.</summary>
    Task<IReadOnlyList<OfdFlattenedEntry>> GetFlattenedEntriesAsync(
        string brandSlug, string brandName,
        string materialSlug, string materialName,
        CancellationToken ct);
}
