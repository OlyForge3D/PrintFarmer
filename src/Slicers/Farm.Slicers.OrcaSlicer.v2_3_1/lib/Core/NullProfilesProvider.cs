using Farm.Slicer.Module.Contracts.Libraries;

namespace Farm.Slicers.OrcaSlicer.v2_3_1;

/// <summary>
/// Null profiles provider — profiles are loaded at runtime from the
/// version-matched OrcaSlicer worker's /opt/orcaslicer/resources/profiles/
/// tree to avoid data drift with the actual engine.
/// </summary>
internal class NullProfilesProvider : ISlicerProfilesProvider
{
    public Task<IEnumerable<SlicerProfileMetadata>> ListOfficialProfilesAsync(CancellationToken ct = default)
        => Task.FromResult(Enumerable.Empty<SlicerProfileMetadata>());

    public Task<string?> GetProfileJsonAsync(string profileId, CancellationToken ct = default)
        => Task.FromResult((string?)null);

    public Task<string?> GetUniversalFilamentsAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult((string?)null);
    }

    public string GetProfilesVersion() => "2.3.1";
}
