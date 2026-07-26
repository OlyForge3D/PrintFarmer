namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Canonical artifact kinds accepted by the slice artifact contract.
/// </summary>
/// <remarks>
/// The runtime allowlist is configuration driven (<c>ArtifactStorage:AllowedKinds</c>); these
/// constants keep producers and the default configuration in agreement.
/// </remarks>
public static class SlicerArtifactKinds
{
    /// <summary>Sliced G-code output.</summary>
    public const string Gcode = "gcode";

    /// <summary>Rendered thumbnail of the sliced plate.</summary>
    public const string Thumbnail = "thumbnail";

    /// <summary>Preview render of the sliced plate.</summary>
    public const string Preview = "preview";

    /// <summary>Slicer log output.</summary>
    public const string Log = "log";

    /// <summary>Generated or resolved calibration geometry.</summary>
    public const string Geometry = "geometry";

    /// <summary>Manifest describing the generated calibration geometry.</summary>
    public const string GeometryManifest = "geometry-manifest";

    /// <summary>Manifest describing annotated calibration segments.</summary>
    public const string CalibrationManifest = "calibration-manifest";

    /// <summary>Effective native machine profile used for the slice.</summary>
    public const string MachineProfile = "machine-profile";

    /// <summary>Effective native process profile used for the slice.</summary>
    public const string ProcessProfile = "process-profile";

    /// <summary>Effective native filament profile used for the slice.</summary>
    public const string FilamentProfile = "filament-profile";

    /// <summary>Exported normalized settings patch derived from a calibration observation.</summary>
    public const string ProfilePatch = "profile-patch";

    /// <summary>The complete default allowlist, in configuration order.</summary>
    public static readonly string[] All =
    [
        Gcode,
        Thumbnail,
        Preview,
        Log,
        Geometry,
        GeometryManifest,
        CalibrationManifest,
        MachineProfile,
        ProcessProfile,
        FilamentProfile,
        ProfilePatch,
    ];

    /// <summary>The default configuration value for <c>ArtifactStorage:AllowedKinds</c>.</summary>
    public const string DefaultAllowedKinds =
        "gcode,thumbnail,preview,log,geometry,geometry-manifest,calibration-manifest," +
        "machine-profile,process-profile,filament-profile,profile-patch";

    /// <summary>
    /// Returns the MIME types accepted for a canonical artifact kind.
    /// </summary>
    /// <param name="kind">The canonical artifact kind.</param>
    /// <returns>The accepted MIME types, or an empty array when the kind places no MIME restriction.</returns>
    public static IReadOnlyList<string> AcceptedMimeTypes(string kind) => kind switch
    {
        Gcode => ["text/x.gcode", "text/plain", "application/gcode", "application/octet-stream"],
        Thumbnail or Preview => ["image/png", "image/jpeg", "image/webp"],
        Log => ["text/plain", "application/octet-stream"],
        Geometry => ["model/stl", "model/3mf", "application/octet-stream", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml"],
        GeometryManifest or CalibrationManifest or MachineProfile or ProcessProfile or FilamentProfile or ProfilePatch =>
            ["application/json", "text/json", "text/plain", "application/octet-stream"],
        _ => [],
    };
}
