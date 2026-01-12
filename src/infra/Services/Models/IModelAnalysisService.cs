namespace Farm.Infrastructure.Services.Models;

public record ModelAnalysisResult(double? DimensionX, double? DimensionY, double? DimensionZ, int? TriangleCount);

public interface IModelAnalysisService
{
    /// <summary>
    /// Analyze model file to extract basic metadata such as dimensions, and triangle count.
    /// Implementations should be best-effort and tolerate unsupported formats by returning nulls.
    /// </summary>
    Task<ModelAnalysisResult?> AnalyzeModelAsync(string filePath, string extension, CancellationToken cancellationToken = default);
}
