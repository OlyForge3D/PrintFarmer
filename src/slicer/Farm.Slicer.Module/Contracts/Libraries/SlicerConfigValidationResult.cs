namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Result of slicer configuration validation.
/// </summary>
public record SlicerConfigValidationResult(
    bool IsValid,
    string[] Errors = default!,
    string[] Warnings = default!)
{
    public SlicerConfigValidationResult()
        : this(true, [], [])
    {
    }
}
