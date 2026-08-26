using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Reads resolved profiles from and installs generated bundles into an OrcaSlicer worker.
/// </summary>
public interface IProfileFamilyWorkerClient
{
    /// <summary>Selects a fresh worker for the requested version and downloads its resolved catalog.</summary>
    Task<(ProfileFamilyWorkerTarget Target, AllProfilesResponseDto Catalog)> GetCatalogAsync(
        string sourceManufacturer,
        string? orcaVersion,
        CancellationToken ct);

    /// <summary>Atomically writes or replaces one family inside the worker's Custom bundle.</summary>
    Task WriteBundleAsync(
        ProfileFamilyWorkerTarget target,
        ProfileFamilyBundleDto bundle,
        CancellationToken ct);
}
