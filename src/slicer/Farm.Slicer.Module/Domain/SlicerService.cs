using System.ComponentModel.DataAnnotations;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Represents a registered slicer worker service instance.
/// </summary>
public class SlicerService
{
    [Key]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SlicerType { get; set; } // maps to SlicerType enum

    public string? Version { get; set; }

    public string? Host { get; set; } // service base URL or identifier

    public string? UiManifestUrl { get; set; }

    // JSON blob describing capabilities (array or object)
    public string? CapabilitiesJson { get; set; }

    public int MaxConcurrentJobs { get; set; } = 1;

    public string? Status { get; set; } = "Unknown"; // Online, Offline, Disabled

    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string? ApiKey { get; set; }

    public DateTime? ApiKeyRotatedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? Tags { get; set; }

    /// <summary>
    /// Stable instance identifier assigned by the deployment environment.
    /// Survives container restarts to enable re-registration without duplicates.
    /// </summary>
    public string? InstanceId { get; set; }
}
