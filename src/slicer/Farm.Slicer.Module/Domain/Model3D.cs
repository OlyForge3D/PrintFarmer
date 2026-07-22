using Farm.Infrastructure.Domain;

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

    /// <summary>JSON-serialized 3MF metadata extracted during upload.</summary>
    public string? ExtractedMetadataJson { get; set; }

    // Attribution fields — populated for imported models (e.g., from Printables)

    /// <summary>Original source URL for imported models (e.g., https://www.printables.com/model/12345).</summary>
    public string? SourceUrl { get; set; }

    /// <summary>License name for imported models (e.g., "CC BY 4.0").</summary>
    public string? SourceLicense { get; set; }

    /// <summary>Creator/author handle for imported models.</summary>
    public string? SourceCreator { get; set; }

    /// <summary>Timestamp when the model was imported from an external source. Null for locally uploaded models.</summary>
    public DateTime? ImportedAt { get; set; }
}
