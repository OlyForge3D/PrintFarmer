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
    /// Stable identifier for this worker process across restarts and redeploys. When it
    /// matches an existing registration, that service/worker row is updated in place instead
    /// of creating a duplicate (issue #1528). It is never authentication material: every
    /// registration — matched or not — always issues a fresh API key, so a known InstanceId
    /// can never recover or reuse a prior credential.
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
