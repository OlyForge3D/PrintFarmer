namespace Farm.Infrastructure.Domain;

// ===========================================================================
// Shared File Enumerations (used by both GcodeFile and Model3D)
// ===========================================================================

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
