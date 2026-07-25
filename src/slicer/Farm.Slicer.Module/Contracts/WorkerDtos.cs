namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// Response for worker details.
/// </summary>
public class WorkerResponse
{
    /// <summary>Gets or sets the worker ID.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the parent service ID.</summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the worker name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the worker endpoint URL.</summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the worker capabilities.</summary>
    public string[] Capabilities { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the worker status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of free slots.</summary>
    public int FreeSlots { get; set; }

    /// <summary>Gets or sets the total number of slots.</summary>
    public int TotalSlots { get; set; }

    /// <summary>Gets or sets the number of active jobs.</summary>
    public int ActiveJobs { get; set; }

    /// <summary>Gets or sets the number of completed jobs.</summary>
    public int CompletedJobs { get; set; }

    /// <summary>Gets or sets the number of failed jobs.</summary>
    public int FailedJobs { get; set; }

    /// <summary>Gets or sets the average processing time in seconds.</summary>
    public double? AverageProcessingTimeSeconds { get; set; }

    /// <summary>Gets or sets the last heartbeat time.</summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>Gets or sets the registration time.</summary>
    public DateTime RegisteredAt { get; set; }

    /// <summary>Gets or sets the time the worker came online.</summary>
    public DateTime? OnlineAt { get; set; }

    /// <summary>Gets or sets the time the worker went offline.</summary>
    public DateTime? OfflineAt { get; set; }

    /// <summary>Gets or sets the worker version.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets whether the worker is disabled.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Gets or sets the reason for disabling the worker.</summary>
    public string? DisabledReason { get; set; }
}

/// <summary>
/// Request to disable a worker.
/// </summary>
public class DisableWorkerRequest
{
    /// <summary>Gets or sets the reason for disabling.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Request to update a worker's total slots.
/// </summary>
public class UpdateWorkerSlotsRequest
{
    /// <summary>Gets or sets the new total slots value.</summary>
    public int TotalSlots { get; set; }
}

/// <summary>
/// Response for an active job assigned to a worker.
/// </summary>
public class WorkerJobResponse
{
    /// <summary>Gets or sets the job ID.</summary>
    public Guid JobId { get; set; }

    /// <summary>Gets or sets the model file name.</summary>
    public string ModelFileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the job status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the progress percentage.</summary>
    public int ProgressPercent { get; set; }

    /// <summary>Gets or sets the progress message.</summary>
    public string? ProgressMessage { get; set; }

    /// <summary>Gets or sets the time the job started.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets the job priority.</summary>
    public int Priority { get; set; }
}
