namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// A scored printer candidate returned by the dispatch scoring engine.
/// </summary>
public class DispatchCandidateDto
{
    /// <summary>Unique identifier of the candidate printer.</summary>
    public Guid PrinterId { get; set; }

    /// <summary>Display name of the candidate printer.</summary>
    public string PrinterName { get; set; } = string.Empty;

    /// <summary>Weighted average score (0–100). Zero if eliminated.</summary>
    public double Score { get; set; }

    /// <summary>Per-factor score breakdown for transparency.</summary>
    public Dictionary<string, FactorScoreDto> ScoreBreakdown { get; set; } = [];

    /// <summary>Whether this printer was eliminated by a hard requirement.</summary>
    public bool Eliminated { get; set; }

    /// <summary>Human-readable reasons for elimination.</summary>
    public List<string> EliminationReasons { get; set; } = [];
}

/// <summary>
/// Individual scoring factor detail for API responses.
/// </summary>
public class FactorScoreDto
{
    /// <summary>Human-readable factor name.</summary>
    public string FactorName { get; set; } = string.Empty;

    /// <summary>Raw score (0–100).</summary>
    public double Score { get; set; }

    /// <summary>Weight used in calculation.</summary>
    public double Weight { get; set; }

    /// <summary>Score × Weight.</summary>
    public double WeightedScore { get; set; }
}

/// <summary>
/// Request body for dispatching a job to a specific printer.
/// </summary>
public class DispatchJobDto
{
    /// <summary>The printer to dispatch the job to.</summary>
    public Guid PrinterId { get; set; }
}

/// <summary>
/// Auto-dispatch settings exposed via API.
/// </summary>
public class DispatchSettingsDto
{
    public bool AutoDispatchEnabled { get; set; }

    public AutoDispatchMode AutoDispatchMode { get; set; }

    public int IdleThresholdSeconds { get; set; }

    public double MinimumScoreThreshold { get; set; }

    public int MaxConcurrentDispatches { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Request body for updating auto-dispatch settings.
/// </summary>
public class UpdateDispatchSettingsDto
{
    public bool AutoDispatchEnabled { get; set; }

    public AutoDispatchMode AutoDispatchMode { get; set; }

    public int IdleThresholdSeconds { get; set; }

    public double MinimumScoreThreshold { get; set; }

    public int MaxConcurrentDispatches { get; set; }
}

/// <summary>
/// SignalR event payload when a job is auto-dispatched.
/// </summary>
public class JobAutoDispatchedEvent
{
    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public double Score { get; set; }

    public AutoDispatchMode Mode { get; set; }
}

/// <summary>
/// SignalR event payload when auto-dispatch suggests a job (Suggest mode).
/// </summary>
public class DispatchSuggestionEvent
{
    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public double Score { get; set; }
}

/// <summary>
/// SignalR event payload when an auto-dispatch attempt fails.
/// </summary>
public class DispatchFailedEvent
{
    public Guid? JobId { get; set; }

    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}
