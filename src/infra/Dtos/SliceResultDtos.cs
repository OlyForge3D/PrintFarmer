namespace Farm.Infrastructure;

/// <summary>
/// Summary of a slicing job and produced G-code artifact once available.
/// </summary>
public class SliceResultDto
{
    public string JobId { get; set; } = string.Empty;

    public string GcodeUrl { get; set; } = string.Empty;

    public int PrintTime { get; set; } // in seconds

    public double FilamentUsed { get; set; } // in grams

    public int LayerCount { get; set; }

    // Added for contract tests: current status and progress of the job
    public string Status { get; set; } = string.Empty; // Queued, Slicing, Completed, Error, Cancelled

    public int Progress { get; set; } // 0-100

    public SliceMetadataDto Metadata { get; set; } = new();
}

public class SliceMetadataDto
{
    public string SlicerVersion { get; set; } = string.Empty;

    public string ProfileUsed { get; set; } = string.Empty;

    public double EstimatedCost { get; set; }
}
