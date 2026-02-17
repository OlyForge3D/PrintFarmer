namespace Farm.Slicer.Module.Services;

/// <summary>
/// Result of analyzing a 3D model file, containing dimensions and mesh statistics.
/// </summary>
/// <param name="DimensionX">The X-axis dimension in millimeters, or null if unavailable.</param>
/// <param name="DimensionY">The Y-axis dimension in millimeters, or null if unavailable.</param>
/// <param name="DimensionZ">The Z-axis dimension in millimeters, or null if unavailable.</param>
/// <param name="TriangleCount">The number of triangles in the mesh, or null if unavailable.</param>
#pragma warning disable SA1402
public record ModelAnalysisResult(double? DimensionX, double? DimensionY, double? DimensionZ, int? TriangleCount);
#pragma warning restore SA1402

/// <summary>
/// Service for analyzing 3D model files to extract metadata such as dimensions and triangle count.
/// </summary>
public interface IModelAnalysisService
{
    /// <summary>
    /// Analyze model file to extract basic metadata such as dimensions and triangle count.
    /// Implementations should be best-effort and tolerate unsupported formats by returning nulls.
    /// </summary>
    /// <param name="filePath">The path to the model file to analyze.</param>
    /// <param name="extension">The file extension indicating the model format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ModelAnalysisResult?> AnalyzeModelAsync(string filePath, string extension, CancellationToken cancellationToken = default);
}
