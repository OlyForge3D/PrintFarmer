namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Individual scoring factor result with weight and optional elimination reason.
/// </summary>
/// <param name="FactorName">Human-readable name of the scoring factor.</param>
/// <param name="Score">Raw score from 0 (worst) to 100 (best).</param>
/// <param name="Weight">Relative importance weight for weighted average calculation.</param>
/// <param name="WeightedScore">Score × Weight, used in final aggregation.</param>
/// <param name="IsHardRequirement">If true, a zero score eliminates the printer.</param>
/// <param name="EliminationReason">Explanation when a hard requirement causes elimination.</param>
public record FactorScore(
    string FactorName,
    double Score,
    double Weight,
    double WeightedScore,
    bool IsHardRequirement,
    string? EliminationReason = null);

/// <summary>
/// Complete dispatch score for a single printer candidate.
/// Contains the weighted total, per-factor breakdown, and elimination status.
/// </summary>
/// <param name="PrinterId">Unique identifier of the candidate printer.</param>
/// <param name="PrinterName">Display name of the candidate printer.</param>
/// <param name="TotalScore">Weighted average score (0–100). Zero if eliminated.</param>
/// <param name="ScoreBreakdown">Per-factor scores keyed by factor name.</param>
/// <param name="Eliminated">True if one or more hard requirements failed.</param>
/// <param name="EliminationReasons">List of reasons for elimination (empty if not eliminated).</param>
public record DispatchScore(
    Guid PrinterId,
    string PrinterName,
    double TotalScore,
    Dictionary<string, FactorScore> ScoreBreakdown,
    bool Eliminated,
    List<string> EliminationReasons);

/// <summary>
/// Dispatch action recorded in the audit trail.
/// </summary>
public enum DispatchAction
{
    /// <summary>Printer was suggested as a candidate.</summary>
    Suggested = 0,

    /// <summary>Job was dispatched to this printer.</summary>
    Dispatched = 1,

    /// <summary>Printer was rejected (eliminated or user chose another).</summary>
    Rejected = 2,

    /// <summary>Dispatch attempt failed.</summary>
    Failed = 3
}

/// <summary>
/// How a job was dispatched to a printer.
/// </summary>
public enum DispatchMode
{
    /// <summary>User manually assigned the printer.</summary>
    Manual = 0,

    /// <summary>User selected from scored suggestions.</summary>
    Suggested = 1,

    /// <summary>System automatically dispatched (future Phase 2).</summary>
    Auto = 2
}

/// <summary>
/// System-wide auto-dispatch behavior mode.
/// </summary>
public enum AutoDispatchMode
{
    /// <summary>No automatic action when printers go idle.</summary>
    Manual = 0,

    /// <summary>Score jobs and notify via SignalR, but let the operator dispatch.</summary>
    Suggest = 1,

    /// <summary>Score jobs and dispatch the best match automatically.</summary>
    Auto = 2
}

/// <summary>
/// Strategy for distributing jobs across eligible printers during batch dispatch.
/// </summary>
public enum LoadBalancingStrategy
{
    /// <summary>Use the existing scoring algorithm — assign each job to its highest-scoring printer.</summary>
    BestFit = 0,

    /// <summary>Distribute jobs evenly across eligible printers in a round-robin cycle.</summary>
    RoundRobin = 1,

    /// <summary>Prefer printers with the shortest queue (fewest active/queued jobs).</summary>
    LeastBusy = 2
}
