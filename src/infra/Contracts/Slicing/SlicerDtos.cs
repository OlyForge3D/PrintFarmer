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
}

public class HeartbeatDto
{
    public string Status { get; set; } = string.Empty;

    public int? FreeSlots { get; set; }
}
