namespace Farm.Web.Api.Domain;

public class SlicerSettings
{
    // Singleton table row - use Id = 1 for simplicity
    public int Id { get; set; } = 1;

    // Whether the local worker is enabled
    public bool Enabled { get; set; } = true;

    // Per-engine runtime settings serialized as JSON (keys are SlicerEngineType enum names)
    public string? PerEngineJson { get; set; }

    // Last update timestamp for auditing/diagnostics
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Jitter percent (+/-) applied to retry backoff scheduling
    public double JitterPercent { get; set; } = 15.0;
}
