using Farm.Infrastructure.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Database projection used to build the lightweight 3D model picker response.
/// </summary>
public sealed record Model3DPickerProjection(
    Guid Id,
    string? Name,
    string FileName,
    ModelFileFormat FileFormat,
    DateTime UploadedAt);
