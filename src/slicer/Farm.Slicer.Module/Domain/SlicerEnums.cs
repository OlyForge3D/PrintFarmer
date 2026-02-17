namespace Farm.Slicer.Module.Domain;

// ===========================================================================
// Slicer Module Enumerations
// ===========================================================================
#region File Formats

/// <summary>
/// Represents the format of a 3D model file.
/// </summary>
public enum ModelFileFormat
{
    /// <summary>STereoLithography format - widely supported mesh format.</summary>
    STL = 0,

    /// <summary>3D Manufacturing Format - supports colors, materials, and metadata.</summary>
    TMF = 1,

    /// <summary>Wavefront OBJ format - text-based geometry format.</summary>
    OBJ = 2,

    /// <summary>Polygon File Format - supports point clouds and meshes.</summary>
    PLY = 3,

    /// <summary>STEP format - CAD interchange format for solid models.</summary>
    STEP = 4,
}

/// <summary>
/// Health status of a stored file on disk.
/// </summary>
public enum FileHealthStatus
{
    /// <summary>File has never been checked or status is unknown.</summary>
    Unknown = 0,

    /// <summary>File exists and integrity verification passed (hash and size match).</summary>
    Healthy = 1,

    /// <summary>File was not found at the expected storage location.</summary>
    Missing = 2,

    /// <summary>File exists but integrity check failed (hash or size mismatch).</summary>
    Corrupted = 3,

    /// <summary>File exists but cannot be read due to permission or access issues.</summary>
    Inaccessible = 4,
}

#endregion

#region Slicing and Profiles

/// <summary>
/// Supported slicer engine types for G-code generation.
/// </summary>
public enum SlicerType
{
    /// <summary>Prusa Research's PrusaSlicer.</summary>
    PrusaSlicer = 0,

    /// <summary>OrcaSlicer - Bambu Lab's fork with advanced features.</summary>
    OrcaSlicer = 1,

    /// <summary>Ultimaker Cura slicer.</summary>
    Cura = 2,

    /// <summary>SuperSlicer - community fork of PrusaSlicer.</summary>
    SuperSlicer = 3,
}

/// <summary>
/// Print quality presets affecting layer height and detail level.
/// </summary>
public enum ProfileQuality
{
    /// <summary>Fast printing with lower detail - suitable for prototypes.</summary>
    Draft = 0,

    /// <summary>Balanced speed and quality - suitable for most prints.</summary>
    Standard = 1,

    /// <summary>High detail with slower print speeds - for display models.</summary>
    Fine = 2,
}

#endregion
