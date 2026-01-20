using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

// 3D Model Management System
public class Model3D : StoredFile
{
    public ModelFileFormat FileFormat { get; set; }

    public double? DimensionX { get; set; } // in mm

    public double? DimensionY { get; set; } // in mm

    public double? DimensionZ { get; set; } // in mm

    public int? TriangleCount { get; set; }

    public bool IsValid { get; set; } = true;

    public string? ValidationErrors { get; set; } // JSON array of validation issues

    public Guid? UploadedByUserId { get; set; }

    public User? UploadedByUser { get; set; }
}
