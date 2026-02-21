using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Settings for automatic stale worker cleanup.
/// </summary>
public class StaleWorkerCleanupSettings
{
    /// <summary>Configuration section name in appsettings.</summary>
    public const string SectionName = "StaleWorkerCleanup";

    /// <summary>Enable automatic cleanup of stale workers.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Interval in seconds between cleanup scans.</summary>
    [JsonPropertyName("intervalSeconds")]
    public int IntervalSeconds { get; set; } = 3600;

    /// <summary>Minutes of inactivity before a worker is considered stale.</summary>
    [JsonPropertyName("staleAfterMinutes")]
    public int StaleAfterMinutes { get; set; } = 1440;

    /// <summary>Automatically delete stale workers (if false, just marks them offline).</summary>
    [JsonPropertyName("autoDelete")]
    public bool AutoDelete { get; set; }
}
