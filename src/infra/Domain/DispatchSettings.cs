using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Services.Queue.Dispatch;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Singleton entity storing system-wide auto-dispatch configuration.
/// One row in the database — use Id = 1 by convention.
/// </summary>
public class DispatchSettings
{
    /// <summary>
    /// Primary key. Always 1 (singleton pattern).
    /// </summary>
    [Key]
    public int Id { get; set; } = 1;

    /// <summary>
    /// Master switch: enables or disables the entire auto-dispatch system.
    /// When false, printers that go idle take no automatic action.
    /// </summary>
    public bool AutoDispatchEnabled { get; set; }

    /// <summary>
    /// Determines behavior when a printer goes idle:
    /// Manual — no automatic action (operator handles everything).
    /// Suggest — score and notify via SignalR, but don't dispatch.
    /// Auto — score, pick best job, and dispatch automatically.
    /// </summary>
    public AutoDispatchMode AutoDispatchMode { get; set; } = AutoDispatchMode.Manual;

    /// <summary>
    /// Seconds to wait after a printer goes idle before acting.
    /// Prevents dispatching during brief pauses (e.g., bed clearing).
    /// </summary>
    public int IdleThresholdSeconds { get; set; } = 30;

    /// <summary>
    /// Minimum weighted score (0–100) a printer must achieve for a job
    /// before auto-dispatch will assign it. Prevents bad matches.
    /// </summary>
    public double MinimumScoreThreshold { get; set; } = 0.5;

    /// <summary>
    /// Maximum number of auto-dispatches that can be in-flight simultaneously.
    /// Prevents overwhelming the operator with too many concurrent starts.
    /// </summary>
    public int MaxConcurrentDispatches { get; set; } = 3;

    /// <summary>
    /// Strategy for distributing jobs across printers during batch dispatch.
    /// BestFit (default) uses scoring, RoundRobin distributes evenly, LeastBusy prefers shortest queues.
    /// </summary>
    public LoadBalancingStrategy LoadBalancingStrategy { get; set; } = LoadBalancingStrategy.BestFit;

    /// <summary>
    /// UTC timestamp of the last settings update.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
