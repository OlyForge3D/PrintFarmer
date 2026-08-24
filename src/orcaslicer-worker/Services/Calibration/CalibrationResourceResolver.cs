using Farm.Slicer.Module.Models;

namespace Farm.OrcaSlicer.Worker.Services.Calibration;

/// <summary>
/// Resolves the bundled OrcaSlicer calibration resource file for a given
/// <see cref="CalibrationMethod"/> (issue #1938). Mirrors <c>OrcaProfilesService</c>'s
/// runtime-resolved-path pattern: the resources ship inside the OrcaSlicer installation, not this
/// repository, so the root directory is configurable for local/testing scenarios.
/// </summary>
public class CalibrationResourceResolver
{
    /// <summary>
    /// Creates a resolver rooted at the given directory, or (when <paramref name="calibResourcesPath"/>
    /// is null/blank/missing) the <c>ORCA_CALIB_PATH</c> environment variable, falling back to the
    /// container-standard OrcaSlicer AppImage extraction path.
    /// </summary>
    public CalibrationResourceResolver(string? calibResourcesPath)
    {
        if (!string.IsNullOrWhiteSpace(calibResourcesPath) && Directory.Exists(calibResourcesPath))
        {
            RootDirectory = calibResourcesPath;
            return;
        }

        string? envPath = Environment.GetEnvironmentVariable("ORCA_CALIB_PATH");
        RootDirectory = !string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath)
            ? envPath
            : "/opt/orcaslicer/resources/calib";
    }

    /// <summary>The resolved root of the bundled calibration resources.</summary>
    public string RootDirectory { get; }

    /// <summary>The absolute path of the calibration model file for <paramref name="method"/>.</summary>
    public string ResolveModelPath(CalibrationMethod method) =>
        Path.Combine(RootDirectory, CalibrationMethods.RelativeResourcePath(method));
}
