namespace Farm.Infrastructure.Contracts.Slicing.Libraries;

/// <summary>
/// Provides access to official and system profiles for a slicer.
/// </summary>
public interface ISlicerProfilesProvider
{
    /// <summary>
    /// Lists all official profiles available in this slicer version.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<IEnumerable<SlicerProfileMetadata>> ListOfficialProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the full profile JSON for a specific profile ID.
    /// </summary>
    /// <param name="profileId">The profile ID to retrieve</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<string?> GetProfileJsonAsync(string profileId, CancellationToken ct = default);

    /// <summary>
    /// Gets the semantic version of the profiles bundled with this library.
    /// </summary>
    string GetProfilesVersion();
}
