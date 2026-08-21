namespace Farm.Infrastructure.Services.Models;

/// <summary>
/// Result of analyzing a 3D model file, containing dimensions and mesh statistics.
/// </summary>
/// <param name="DimensionX">The X-axis dimension in millimeters, or null if unavailable.</param>
/// <param name="DimensionY">The Y-axis dimension in millimeters, or null if unavailable.</param>
/// <param name="DimensionZ">The Z-axis dimension in millimeters, or null if unavailable.</param>
/// <param name="TriangleCount">The number of triangles in the mesh, or null if unavailable.</param>
/// <param name="IsValid">
/// Whether the file could actually be read as a 3D model: the archive/binary structure opened
/// and at least one triangle of real geometry was found. This is deliberately narrow — it is
/// <b>not</b> a printability, orientability, or slicing pre-flight check (see issue #1811); a
/// model can be geometrically valid here and still fail to slice for reasons like footprint or
/// orientation. False is reserved for files that are structurally unreadable as a model (empty,
/// truncated, corrupt archive, or a mesh with zero triangles).
/// </param>
/// <param name="ValidationErrors">Human-readable reasons the geometry could not be fully read, or null when none.</param>
public record ModelAnalysisResult(
    double? DimensionX,
    double? DimensionY,
    double? DimensionZ,
    int? TriangleCount,
    bool IsValid = true,
    IReadOnlyList<string>? ValidationErrors = null);

/// <summary>
/// Service for analyzing 3D model files to extract metadata such as dimensions and triangle count.
/// </summary>
public interface IModelAnalysisService
{
    /// <summary>
    /// Analyze model file to extract basic metadata such as dimensions, and triangle count.
    /// Implementations should be best-effort and tolerate unsupported formats by returning nulls.
    /// </summary>
    /// <param name="filePath">The path to the model file to analyze.</param>
    /// <param name="extension">The file extension indicating the model format.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<ModelAnalysisResult?> AnalyzeModelAsync(string filePath, string extension, CancellationToken cancellationToken = default);
}
