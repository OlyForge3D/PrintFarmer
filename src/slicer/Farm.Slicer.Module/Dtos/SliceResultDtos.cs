namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Result data returned after a slicing operation completes.
/// </summary>
public class SliceResultDto
{
    public string JobId { get; set; } = string.Empty;

    public string GcodeUrl { get; set; } = string.Empty;

    public int PrintTime { get; set; }

    public double FilamentUsed { get; set; }

    public int LayerCount { get; set; }

    public string Status { get; set; } = string.Empty;

    public int Progress { get; set; }

    public SliceMetadataDto Metadata { get; set; } = new();
}

/// <summary>
/// Metadata about the slicer engine and profile used for a slice operation.
/// </summary>
public class SliceMetadataDto
{
    public string SlicerVersion { get; set; } = string.Empty;

    public string ProfileUsed { get; set; } = string.Empty;

    public double EstimatedCost { get; set; }
}
