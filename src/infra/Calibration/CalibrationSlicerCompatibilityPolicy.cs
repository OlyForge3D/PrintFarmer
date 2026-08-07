namespace Farm.Infrastructure.PrinterCalibration;

/// <summary>
/// Defines the bounded set of upstream OrcaSlicer versions accepted for calibration.
/// </summary>
public sealed class CalibrationSlicerCompatibilityPolicy
{
    /// <summary>Configuration key containing the bounded version allow-list.</summary>
    public const string ConfigurationKey = "Calibration:SupportedSlicerVersions";

    /// <summary>Maximum number of versions accepted in the configured allow-list.</summary>
    public const int MaximumSupportedVersionCount = 32;

    /// <summary>Default policy used when no explicit allow-list is configured.</summary>
    public static CalibrationSlicerCompatibilityPolicy Default { get; } =
        new([CalibrationContractConstants.SlicerVersion]);

    /// <summary>Creates a validated bounded allow-list.</summary>
    /// <param name="configuredVersions">Configured upstream OrcaSlicer versions.</param>
    public CalibrationSlicerCompatibilityPolicy(IEnumerable<string?>? configuredVersions)
    {
        string[] versions = configuredVersions?
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

        if (versions.Length == 0)
        {
            versions = [CalibrationContractConstants.SlicerVersion];
        }

        if (versions.Length > MaximumSupportedVersionCount)
        {
            throw new ArgumentException(
                $"Calibration supports at most {MaximumSupportedVersionCount} configured slicer versions.",
                nameof(configuredVersions));
        }

        string? invalidVersion = versions.FirstOrDefault(version => !IsValidPolicyVersion(version));
        if (invalidVersion is not null)
        {
            throw new ArgumentException(
                $"Calibration slicer version '{invalidVersion}' must use numeric major.minor.patch format.",
                nameof(configuredVersions));
        }

        SupportedVersions = versions;
    }

    /// <summary>Gets the distinct versions in configured order.</summary>
    public IReadOnlyList<string> SupportedVersions { get; }

    /// <summary>Gets the legacy primary required version retained for client compatibility.</summary>
    public string RequiredVersion => SupportedVersions[0];

    /// <summary>Determines whether an observed worker or profile version is allow-listed.</summary>
    /// <param name="observedVersion">Observed upstream OrcaSlicer version.</param>
    /// <returns><see langword="true"/> when the version is explicitly allowed.</returns>
    public bool IsSupported(string? observedVersion) =>
        !string.IsNullOrWhiteSpace(observedVersion) &&
        SupportedVersions.Any(supportedVersion =>
            string.Equals(observedVersion, supportedVersion, StringComparison.Ordinal) ||
            observedVersion.StartsWith($"{supportedVersion}+", StringComparison.Ordinal));

    private static bool IsValidPolicyVersion(string version)
    {
        string[] components = version.Split('.', StringSplitOptions.None);
        return components.Length == 3 &&
               components.All(component =>
                   component.Length > 0 &&
                   component.All(char.IsAsciiDigit));
    }
}
