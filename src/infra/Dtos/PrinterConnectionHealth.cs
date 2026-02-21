using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// A single state transition entry in the connection health log.
/// </summary>
public sealed record ConnectionStateTransition(
    DateTime TimestampUtc,
    PrinterConnectionState FromState,
    PrinterConnectionState ToState,
    string? Reason);

/// <summary>
/// Per-printer connection health snapshot exposed via the diagnostics API.
/// Populated by backend services and aggregated by the diagnostics controller.
/// </summary>
public sealed class PrinterConnectionHealth
{
    public required Guid PrinterId { get; init; }

    public required string PrinterName { get; init; }

    public required PrinterBackend Backend { get; init; }

    public PrinterConnectionState ConnectionState { get; set; } = PrinterConnectionState.Offline;

    public DateTime? LastConnectedUtc { get; set; }

    public DateTime? LastDisconnectedUtc { get; set; }

    public int ReconnectAttempts { get; set; }

    public int TotalReconnects { get; set; }

    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Rolling uptime percentage over the last hour (0.0–100.0).
    /// </summary>
    public double UptimePercent { get; set; }

    /// <summary>
    /// Current polling/connection mode (e.g., "WebSocket", "HTTP Fallback", "Polling").
    /// </summary>
    public string? ConnectionMode { get; set; }

    private readonly object _transitionLock = new();
    private readonly List<ConnectionStateTransition> _transitions = new(24);

    /// <summary>
    /// Last N state transitions for diagnostics. Ring buffer capped at <see cref="MaxTransitions"/>.
    /// </summary>
    [JsonInclude]
    public IReadOnlyList<ConnectionStateTransition> RecentTransitions
    {
        get
        {
            lock (_transitionLock)
            {
                return _transitions.ToList();
            }
        }
    }

    public const int MaxTransitions = 20;

    /// <summary>
    /// Records a state transition and updates connection timestamps.
    /// </summary>
    public void RecordTransition(PrinterConnectionState newState, string? reason = null)
    {
        lock (_transitionLock)
        {
            var previous = ConnectionState;
            if (previous == newState)
            {
                return;
            }

            var now = DateTime.UtcNow;
            ConnectionState = newState;

            _transitions.Add(new ConnectionStateTransition(now, previous, newState, reason));
            if (_transitions.Count > MaxTransitions)
            {
                _transitions.RemoveAt(0);
            }

            switch (newState)
            {
                case PrinterConnectionState.Connected:
                    LastConnectedUtc = now;
                    ConsecutiveFailures = 0;
                    if (previous is PrinterConnectionState.Offline or PrinterConnectionState.Reconnecting)
                    {
                        TotalReconnects++;
                    }

                    break;
                case PrinterConnectionState.Offline:
                    LastDisconnectedUtc = now;
                    break;
            }
        }
    }

    /// <summary>
    /// Computes rolling uptime % based on transition history over the given window.
    /// </summary>
    public void UpdateUptimePercent(TimeSpan window)
    {
        lock (_transitionLock)
        {
            var now = DateTime.UtcNow;
            var cutoff = now - window;

            if (_transitions.Count == 0)
            {
                UptimePercent = ConnectionState == PrinterConnectionState.Connected ? 100.0 : 0.0;
                return;
            }

            double onlineSeconds = 0;

            // Walk transitions in order, accumulate time spent in Connected state
            var prevState = _transitions.FirstOrDefault(t => t.TimestampUtc >= cutoff)?.FromState
                            ?? _transitions[^1].ToState;
            var prevTime = cutoff;

            foreach (var t in _transitions.Where(t => t.TimestampUtc >= cutoff))
            {
                if (prevState == PrinterConnectionState.Connected)
                {
                    onlineSeconds += (t.TimestampUtc - prevTime).TotalSeconds;
                }

                prevState = t.ToState;
                prevTime = t.TimestampUtc;
            }

            // Account for time from last transition to now
            if (prevState == PrinterConnectionState.Connected)
            {
                onlineSeconds += (now - prevTime).TotalSeconds;
            }

            var totalSeconds = window.TotalSeconds;
            UptimePercent = totalSeconds > 0 ? Math.Round(onlineSeconds / totalSeconds * 100, 1) : 0;
        }
    }
}
