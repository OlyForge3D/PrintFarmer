namespace Farm.Slicer.Module.Domain;

/// <summary>
/// 3D model file metadata. Extends StoredFileBase with model-specific dimensions and validation.
/// Cross-domain references (User) are kept as Guid-only soft refs.
/// </summary>
public class Model3D : StoredFileBase
{
    public ModelFileFormat FileFormat { get; set; }

    public double? DimensionX { get; set; } // in mm

    public double? DimensionY { get; set; } // in mm

    public double? DimensionZ { get; set; } // in mm

    public int? TriangleCount { get; set; }

    public bool IsValid { get; set; } = true;

    public string? ValidationErrors { get; set; } // JSON array of validation issues

    /// <summary>Soft reference to the user who uploaded this model (no FK constraint).</summary>
    public Guid? UploadedByUserId { get; set; }
}
