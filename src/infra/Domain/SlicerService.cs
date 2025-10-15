using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

public class SlicerService
{
    [Key]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SlicerType { get; set; } = 0; // maps to SlicerType enum

    public string? Version { get; set; }

    public string? Host { get; set; } // service base URL or identifier

    public string? UiManifestUrl { get; set; }

    // JSON blob describing capabilities (array or object)
    public string? CapabilitiesJson { get; set; }

    public int MaxConcurrentJobs { get; set; } = 1;

    public string? Status { get; set; } = "Unknown"; // Online, Offline, Disabled

    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string? ApiKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? Tags { get; set; }
}
