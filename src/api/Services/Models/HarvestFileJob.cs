namespace Farm.Web.Api.Services.Models;

/// <summary>
/// Represents a single file processing job in the harvest queue
/// </summary>
public class HarvestFileJob
{
    public Guid OperationId { get; set; }
    public Guid PrinterId { get; set; }
    public string ServerUrl { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    public override string ToString()
    {
        return $"HarvestFileJob(Operation: {OperationId}, File: {FileName}, Size: {FileSize} bytes)";
    }
}
