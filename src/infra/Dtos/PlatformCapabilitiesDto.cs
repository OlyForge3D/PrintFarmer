using Farm.Infrastructure.Services.OperatorFeatures;

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

    /// <summary>Gets whether model uploads accept client-generated PNG thumbnails.</summary>
    public bool ClientThumbnailUploadEnabled { get; init; } = true;

    /// <summary>Gets whether model uploads support caller-provided idempotency identifiers.</summary>
    public bool IdempotentModelUploadEnabled { get; init; } = true;

    /// <summary>Gets an optional note explaining platform limitations.</summary>
    public string? PlatformNote { get; init; }

    /// <summary>
    /// Effective operator feature flags after resolving persisted settings and environment
    /// hard-disable overrides. See issue #725 and <c>docs/OPERATOR_FEATURE_GATES.md</c>. Clients
    /// (React and iOS) MUST tolerate this field being absent on older servers and fall back to
    /// the defaults documented on <see cref="OperatorFeatureFlagsDto"/>.
    /// </summary>
    public OperatorFeatureFlagsDto OperatorFeatures { get; init; } = new();
}
