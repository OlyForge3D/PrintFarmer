using Farm.Infrastructure.Contracts.Slicing.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_1;

/// <summary>
/// Null implementation of ISlicerProfilesProvider.
/// 
/// OrcaSlicer profiles are loaded dynamically from the OrcaSlicer worker service (/api/profiles),
/// which parses them from the official OrcaSlicer installation at /opt/orcaslicer/resources/profiles/.
/// 
/// Bundled profiles are not used as they don't match the OrcaSlicer folder structure and would
/// be outdated. Use the worker service for all profile operations.
/// </summary>
internal class NullProfilesProvider : ISlicerProfilesProvider
{
    public Task<IEnumerable<SlicerProfileMetadata>> ListOfficialProfilesAsync(CancellationToken ct = default)
    {
        // Profiles are loaded from the worker, not from bundled resources
        return Task.FromResult(Enumerable.Empty<SlicerProfileMetadata>());
    }

    public Task<string?> GetProfileJsonAsync(string profileId, CancellationToken ct = default)
    {
        // Profiles are loaded from the worker, not from bundled resources
        return Task.FromResult((string?)null);
    }

    public Task<string?> GetUniversalFilamentsAsync(CancellationToken ct = default)
    {
        // Filaments are loaded from the worker, not from bundled resources
        return Task.FromResult((string?)null);
    }

    public string GetProfilesVersion() => "2.3.1";
}
