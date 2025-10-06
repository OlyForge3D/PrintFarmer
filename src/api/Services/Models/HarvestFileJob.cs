namespace Farm.Web.Api.Services.Models;

/// <summary>
/// Represents a single file processing job in the harvest queue
/// </summary>
public class HarvestFileJob
{
    public Guid OperationId { get; set; }
    public Guid PrinterId { get; set; }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport job model; bound from JSON/messages; keep string for compatibility")] 
    public string ServerUrl { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    
    // Metadata from API (populated during discovery for backends that support it)
    // This avoids downloading files just to extract metadata during the discovery phase
    public string? SlicerName { get; set; }
    public string? SlicerVersion { get; set; }
    public int? EstimatedTimeSeconds { get; set; }
    public double? FilamentLengthMm { get; set; }
    public double? FilamentWeightGrams { get; set; }
    public double? LayerHeight { get; set; }
    public double? FirstLayerHeight { get; set; }
    public double? ObjectHeight { get; set; }
    public double? FirstLayerBedTemp { get; set; }
    public double? FirstLayerExtrTemp { get; set; }
    public string? ThumbnailRelativePath { get; set; } // Path to largest thumbnail

    public override string ToString()
    {
        return $"HarvestFileJob(Operation: {OperationId}, File: {FileName}, Size: {FileSize} bytes)";
    }
}
