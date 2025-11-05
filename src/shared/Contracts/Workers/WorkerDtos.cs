using System;

namespace Farm.Web.Shared.Contracts.Workers;

/// <summary>
/// Response for worker details
/// </summary>
public class WorkerResponse
{
    public Guid Id { get; set; }
    public string ServiceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EndpointUrl { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = Array.Empty<string>();
    public string Status { get; set; } = string.Empty;
    public int FreeSlots { get; set; }
    public int TotalSlots { get; set; }
    public int ActiveJobs { get; set; }
    public int CompletedJobs { get; set; }
    public int FailedJobs { get; set; }
    public double? AverageProcessingTimeSeconds { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime? OnlineAt { get; set; }
    public DateTime? OfflineAt { get; set; }
    public string? Version { get; set; }
    public bool IsDisabled { get; set; }
    public string? DisabledReason { get; set; }
}

/// <summary>
/// Request to disable a worker
/// </summary>
public class DisableWorkerRequest
{
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Response for active job assigned to a worker
/// </summary>
public class WorkerJobResponse
{
    public Guid JobId { get; set; }
    public string ModelFileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ProgressPercent { get; set; }
    public string? ProgressMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public int Priority { get; set; }
}
