namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Provides access to official and system profiles for a slicer.
/// </summary>
public interface ISlicerProfilesProvider
{
    /// <summary>
    /// Lists all official/system profiles available from this slicer.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of profile metadata entries.</returns>
    Task<IEnumerable<SlicerProfileMetadata>> ListOfficialProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the raw JSON for a specific profile by identifier.
    /// </summary>
    /// <param name="profileId">The profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The raw profile JSON, or <c>null</c> if not found.</returns>
    Task<string?> GetProfileJsonAsync(string profileId, CancellationToken ct = default);

    /// <summary>
    /// Gets the profiles bundle version string.
    /// </summary>
    /// <returns>The version string for this profile set.</returns>
    string GetProfilesVersion();
}
