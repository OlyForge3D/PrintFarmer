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
    /// Stable instance identifier for this worker, persisted across container restarts.
    /// When provided, the API upserts instead of creating duplicate registrations.
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
/// Redacted slicer worker registration response for read endpoints.
/// </summary>
public class SlicerServiceResponseDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SlicerType { get; set; }

    public string? Version { get; set; }

    public string? Host { get; set; }

    public string? UiManifestUrl { get; set; }

    public string? CapabilitiesJson { get; set; }

    public int MaxConcurrentJobs { get; set; }

    public string? Status { get; set; }

    public DateTime LastSeen { get; set; }

    public DateTime? ApiKeyRotatedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? Tags { get; set; }

    public string? InstanceId { get; set; }
}
