using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Health level reported for internal services on the system status page.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemServiceHealth
{
    Healthy,
    Degraded,
    Critical,
}

/// <summary>
/// Aggregated system information returned by <c>GET /api/system/info</c>.
/// </summary>
public record SystemInfoDto
{
    /// <summary>Application metadata for the running API process.</summary>
    public required SystemAppInfoDto App { get; init; }

    /// <summary>CPU metrics for the host running PrintFarmer.</summary>
    public required SystemCpuInfoDto Cpu { get; init; }

    /// <summary>Memory metrics for the host running PrintFarmer.</summary>
    public required SystemMemoryInfoDto Memory { get; init; }

    /// <summary>Disk metrics for the drive containing PrintFarmer storage.</summary>
    public required SystemDiskInfoDto Disk { get; init; }

    /// <summary>Internal service health for the API and registered background services.</summary>
    public required IReadOnlyList<SystemServiceInfoDto> Services { get; init; }

    /// <summary>Database metadata and lightweight entity counts.</summary>
    public required SystemDatabaseInfoDto Database { get; init; }
}

/// <summary>
/// Application metadata for the current API instance.
/// </summary>
public record SystemAppInfoDto
{
    /// <summary>The informational build version of the API.</summary>
    public required string Version { get; init; }

    /// <summary>The process uptime formatted for operator display.</summary>
    public required string Uptime { get; init; }

    /// <summary>The current machine hostname.</summary>
    public required string Hostname { get; init; }
}

/// <summary>
/// CPU metrics for the current host.
/// </summary>
public record SystemCpuInfoDto
{
    /// <summary>Number of logical processor cores available to the process.</summary>
    public required int Cores { get; init; }

    /// <summary>Current host CPU utilization percentage.</summary>
    public required double UsagePercent { get; init; }
}

/// <summary>
/// Memory metrics for the current host.
/// </summary>
public record SystemMemoryInfoDto
{
    /// <summary>Bytes currently in use on the host, or process working set as a fallback.</summary>
    public required long UsedBytes { get; init; }

    /// <summary>Total bytes of addressable host memory when available.</summary>
    public required long TotalBytes { get; init; }
}

/// <summary>
/// Disk metrics for the storage drive used by PrintFarmer.
/// </summary>
public record SystemDiskInfoDto
{
    /// <summary>Bytes used on the storage drive.</summary>
    public required long UsedBytes { get; init; }

    /// <summary>Total bytes on the storage drive.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Bytes consumed by archived G-code files in PrintFarmer storage.</summary>
    public required long ArchiveBytes { get; init; }

    /// <summary>Bytes consumed by the PrintFarmer database.</summary>
    public required long DatabaseBytes { get; init; }
}

/// <summary>
/// Health entry for an internal API-managed service.
/// </summary>
public record SystemServiceInfoDto
{
    /// <summary>Operator-facing service name.</summary>
    public required string Name { get; init; }

    /// <summary>Reported service version.</summary>
    public required string Version { get; init; }

    /// <summary>Current service health.</summary>
    public required SystemServiceHealth Health { get; init; }
}

/// <summary>
/// Database metadata surfaced to the system status page.
/// </summary>
public record SystemDatabaseInfoDto
{
    /// <summary>Normalized database engine name.</summary>
    public required string Engine { get; init; }

    /// <summary>Database server or library version.</summary>
    public required string Version { get; init; }

    /// <summary>Current printer count in the application database.</summary>
    public required int PrinterCount { get; init; }

    /// <summary>Current archived G-code count in the application database.</summary>
    public required int ArchiveCount { get; init; }
}
