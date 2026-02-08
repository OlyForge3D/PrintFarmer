using System;

namespace Farm.Infrastructure.Contracts.Slicing;

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
    /// If true, seed all system profiles from the worker on registration (push-based).
    /// Default is false - profiles are imported on-demand when printers are added (pull-based).
    /// </summary>
    public bool SeedProfilesOnRegistration { get; set; } = false;
}

public class HeartbeatDto
{
    public string Status { get; set; } = string.Empty;

    public int? FreeSlots { get; set; }
}
