using System.Text.Json.Serialization;

namespace Farm.Infrastructure;
#pragma warning disable SA1649 // File name should match first type name
#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Lifecycle status of a print job.
/// </summary>
// Custom permissive converter so tests / workers can deserialize numeric or string forms ("Queued", 0, "0").
[JsonConverter(typeof(Json.PrintJobStatusJsonConverter))]
public enum PrintJobStatus
{
    Queued = 0,
    Assigned = 1,
    Starting = 2,
    Printing = 3,
    Paused = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7
}

/// <summary>
/// Priority levels influencing scheduling order.
/// </summary>
public enum PrintJobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3
}

/// <summary>
/// Represents a print job (active or historical) with scheduling and tracking data.
/// </summary>
public record PrintJobDto(
    Guid Id,
    string Name,
    int Priority,
    PrintJobStatus Status,
    DateTime QueuedAt,
    DateTime? StartedAt = null,
    DateTime? CompletedAt = null,
    string? ErrorMessage = null,
    Guid GcodeFileId = default,
    string GcodeFileName = "",
    Guid? AssignedPrinterId = null,
    string? AssignedPrinterName = null,
    double? HotendTemperature = null,
    double? BedTemperature = null,
    int? SpoolId = null,
    double? ProgressPercentage = null,
    string? CurrentState = null,
    string[]? RequiredCapabilities = null,
    bool AutoAssign = true,
    Guid[]? PreferredPrinterIds = null,
    Guid[]? ExcludedPrinterIds = null,
    int? PlateIndex = null,
    string? PlateName = null);

/// <summary>
/// Request payload for creating and queueing a new print job.
/// </summary>
public class CreatePrintJobDto
{
    public string Name { get; set; } = string.Empty;

    public int Priority { get; set; }

    public Guid GcodeFileId { get; set; }

    public double? HotendTemperature { get; set; }

    public double? BedTemperature { get; set; }

    public int? SpoolId { get; set; }

    public string[]? RequiredCapabilities { get; set; }

    public bool AutoAssign { get; set; } = true;

    public Guid[]? PreferredPrinterIds { get; set; }

    public Guid[]? ExcludedPrinterIds { get; set; }
}

/// <summary>
/// Update payload for adjusting job metadata or scheduling parameters.
/// </summary>
public record UpdatePrintJobDto(
    string Name,
    int Priority,
    double? HotendTemperature = null,
    double? BedTemperature = null,
    int? SpoolId = null,
    string[]? RequiredCapabilities = null,
    bool AutoAssign = true,
    Guid[]? PreferredPrinterIds = null,
    Guid[]? ExcludedPrinterIds = null);

/// <summary>
/// DTO for reporting print job status and all available properties.
/// Used by both API controllers and infrastructure services.
/// </summary>
public class PrintJobStatusDto
{
    public string? State { get; set; }

    public double? Progress { get; set; }

    public string? JobName { get; set; }

    public string? ThumbnailUrl { get; set; }

    public string? Error { get; set; }
}

#pragma warning restore SA1649 // File name should match first type name
#pragma warning restore SA1402 // File may only contain a single type
