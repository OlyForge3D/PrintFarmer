namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// Request to perform a dry run of slicer template rendering.
/// </summary>
public class DryRunRequest
{
    /// <summary>Gets or sets the template text to validate.</summary>
    public string? Template { get; set; }

    /// <summary>Gets or sets the slicer engine type.</summary>
    public Models.SlicerEngineType Engine { get; set; } = Models.SlicerEngineType.OrcaSlicer;
}

/// <summary>
/// Result of a slicer template dry run.
/// </summary>
public class DryRunResult
{
    /// <summary>Gets or sets whether the template is valid.</summary>
    public bool IsValid { get; set; }

    private readonly List<string> _issues = new();
    private readonly List<string> _warnings = new();

    /// <summary>Gets the list of validation issues.</summary>
    public IReadOnlyList<string> Issues => _issues;

    /// <summary>Gets the list of validation warnings.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Gets or sets the rendered template output.</summary>
    public string? Rendered { get; set; }

    /// <summary>Gets or sets sample placeholder values used during rendering.</summary>
    public Dictionary<string, string> SamplePlaceholders { get; set; } = new();

    /// <summary>Adds a validation issue.</summary>
    public void AddIssue(string issue)
    {
        if (!string.IsNullOrEmpty(issue))
        {
            _issues.Add(issue);
        }
    }

    /// <summary>Adds a validation warning.</summary>
    public void AddWarning(string warning)
    {
        if (!string.IsNullOrEmpty(warning))
        {
            _warnings.Add(warning);
        }
    }
}

/// <summary>
/// Response DTO for slicer settings.
/// </summary>
public class SlicerSettingsDto
{
    public bool Enabled { get; set; }

    public double JitterPercent { get; set; }

    public string? PerEngineJson { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Request DTO for updating slicer settings.
/// </summary>
public class UpdateSlicerSettingsRequest
{
    public bool Enabled { get; set; } = true;

    public double JitterPercent { get; set; } = 15.0;

    public string? PerEngineJson { get; set; }
}
