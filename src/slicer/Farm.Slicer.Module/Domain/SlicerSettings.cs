namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Global slicer module settings (singleton row).
/// </summary>
public class SlicerSettings
{
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public string? PerEngineJson { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public double JitterPercent { get; set; } = 15.0;
}
