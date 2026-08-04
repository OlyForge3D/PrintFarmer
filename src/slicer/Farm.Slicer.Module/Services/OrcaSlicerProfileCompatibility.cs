namespace Farm.Slicer.Module.Services;

/// <summary>
/// Defines OrcaSlicer versions whose upstream profile catalogs can be consumed by PrintFarmer.
/// </summary>
public static class OrcaSlicerProfileCompatibility
{
    private static readonly string[] SupportedVersions =
        ["2.3.1", "2.4.0", "2.4.1", "2.4.2"];

    /// <summary>
    /// Determines whether a worker version is supported for general profile browsing and import.
    /// </summary>
    /// <param name="version">The worker's registered OrcaSlicer version.</param>
    /// <returns><see langword="true"/> when the version is a supported stable version or build of one.</returns>
    public static bool IsSupportedVersion(string? version)
    {
        return SupportedVersions.Any(supportedVersion =>
            string.Equals(version, supportedVersion, StringComparison.Ordinal) ||
            (version?.StartsWith($"{supportedVersion}+", StringComparison.Ordinal) ?? false));
    }
}
