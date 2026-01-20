using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Result summary returned after importing selected harvested files.
/// </summary>
public class GcodeHarvestResultDto
{
    // Constructor for backward compatibility
    public GcodeHarvestResultDto()
    {
    }

    public GcodeHarvestResultDto(Guid operationId, bool success, string message, int discoveredFiles = 0, int importedFiles = 0, string[]? errors = null)
    {
        OperationId = operationId;
        Success = success;
        Message = message;
        DiscoveredFiles = discoveredFiles;
        ImportedFiles = importedFiles;
        Errors = errors;
    }

    public Guid OperationId { get; set; }

    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public int DiscoveredFiles { get; set; }

    public int ImportedFiles { get; set; }

    public string[]? Errors { get; set; }

    public string[] ImportedFileIds { get; set; } = Array.Empty<string>();

    public string[] SkippedFileIds { get; set; } = Array.Empty<string>();

    public string[] FailedFileIds { get; set; } = Array.Empty<string>();

    public Dictionary<string, string>? ErrorDetails { get; set; }
}
