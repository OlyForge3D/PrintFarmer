namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Exposes platform capabilities so the frontend can hide/show features
/// that depend on native libraries (e.g. lib3mf, AssimpNetter) unavailable on ARM64.
/// </summary>
public record PlatformCapabilitiesDto
{
    /// <summary>Gets the runtime processor architecture (e.g. X64, Arm64).</summary>
    public string Architecture { get; init; } = string.Empty;

    /// <summary>Gets whether slicer integration is available.</summary>
    public bool SlicingEnabled { get; init; }

    /// <summary>Gets whether 3D model file support (STL, OBJ, STEP, 3MF) is available.</summary>
    public bool ModelFilesEnabled { get; init; }

    /// <summary>Gets whether server-side thumbnail generation is available.</summary>
    public bool ThumbnailGenerationEnabled { get; init; }

    /// <summary>Gets whether G-code upload is available. Always true (no native deps).</summary>
    public bool GcodeUploadEnabled { get; init; } = true;

    /// <summary>Gets an optional note explaining platform limitations.</summary>
    public string? PlatformNote { get; init; }
}
