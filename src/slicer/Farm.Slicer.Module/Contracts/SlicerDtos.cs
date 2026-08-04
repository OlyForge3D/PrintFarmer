namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// DTO for registering a new slicer worker with the orchestrator.
/// </summary>
public class RegisterSlicerDto
{
    public string Name { get; set; } = string.Empty;

    public int SlicerType { get; set; }

    public string Version { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string? UiManifestUrl { get; set; }

    public string? CapabilitiesJson { get; set; }

    public int MaxConcurrentJobs { get; set; }

    public string? Tags { get; set; }

    /// <summary>
    /// Diagnostic identifier for this worker process. It is not authentication material,
    /// and registration always creates a fresh service identity and key.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// If true, seed all system profiles from the worker on registration (push-based).
    /// </summary>
    public bool SeedProfilesOnRegistration { get; set; }
}

/// <summary>
/// DTO for slicer worker heartbeat updates.
/// </summary>
public class HeartbeatDto
{
    public string Status { get; set; } = string.Empty;

    public int? FreeSlots { get; set; }
}

/// <summary>
/// Redacted status for a registered slicer service.
/// </summary>
public sealed class SlicerServiceResponse
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SlicerType { get; set; }

    public string? Version { get; set; }

    public int MaxConcurrentJobs { get; set; }

    public string? Status { get; set; }

    public DateTime LastSeen { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? Tags { get; set; }
}
