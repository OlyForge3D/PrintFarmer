namespace Farm.Web.Api.Services;

/// <summary>
/// Represents the coarse phase of application startup used by readiness and health probes.
/// </summary>
public enum StartupPhase
{
    /// <summary>Initialization has not yet completed (background tasks may be running).</summary>
    Starting = 0,
    /// <summary>Initialization finished successfully; the application is fully ready.</summary>
    Ready = 1,
    /// <summary>Initialization failed (timeout or unrecoverable error); manual intervention required.</summary>
    Failed = 2
}

/// <summary>
/// Tracks coarse startup readiness so early liveness endpoints can distinguish between
/// starting, ready, and failed states while heavier database initialization &amp; seeding runs in the background.
/// Thread-safe via volatile writes (single transitions) — no locking required.
/// </summary>
public class StartupStatus
{
    private volatile StartupPhase _phase = StartupPhase.Starting;
    private long _initStartTicks; // 0 until started
    private long _initEndTicks;   // set when ready/failed
    private Exception? _failure;

    /// <summary>
    /// UTC timestamp when initialization started (if recorded).
    /// </summary>
    public DateTime? InitializationStartedUtc => _initStartTicks == 0 ? null : new DateTime(Interlocked.Read(ref _initStartTicks), DateTimeKind.Utc);

    /// <summary>
    /// UTC timestamp when initialization completed (ready or failed).
    /// </summary>
    public DateTime? InitializationCompletedUtc => _initEndTicks == 0 ? null : new DateTime(Interlocked.Read(ref _initEndTicks), DateTimeKind.Utc);

    /// <summary>
    /// Total initialization duration if start and end have both been recorded.
    /// </summary>
    public TimeSpan? InitializationDuration => (InitializationStartedUtc, InitializationCompletedUtc) is (DateTime s, DateTime e) && e >= s ? e - s : null;

    /// <summary>
    /// Exception captured when marking failed (if any).
    /// </summary>
    public Exception? FailureException => _failure;

    /// <summary>
    /// Current startup phase. Transitions: Starting -> Ready OR Starting -> Failed (terminal).
    /// </summary>
    public StartupPhase Phase => _phase;

    /// <summary>
    /// Convenience boolean indicating readiness (true when <see cref="Phase"/> == <see cref="StartupPhase.Ready"/>).
    /// </summary>
    public bool IsReady => _phase == StartupPhase.Ready;

    /// <summary>
    /// Convenience boolean indicating a terminal failure (true when <see cref="Phase"/> == <see cref="StartupPhase.Failed"/>).
    /// </summary>
    public bool IsFailed => _phase == StartupPhase.Failed;

    /// <summary>
    /// Mark startup as successfully completed. Subsequent calls are ignored if already Ready or Failed.
    /// </summary>
    public void MarkInitializationStarted()
    {
        // Record only first time; ignore subsequent calls
        if (Interlocked.CompareExchange(ref _initStartTicks, DateTime.UtcNow.Ticks, 0) == 0)
        {
            // no-op beyond timestamp; phase remains Starting
        }
    }

    public void MarkReady()
    {
        if (_phase == StartupPhase.Starting)
        {
            _phase = StartupPhase.Ready;
            if (_initEndTicks == 0)
            {
                Interlocked.CompareExchange(ref _initEndTicks, DateTime.UtcNow.Ticks, 0);
            }
        }
    }

    /// <summary>
    /// Mark startup as failed. This transition is terminal and ignored if already Ready or Failed.
    /// </summary>
    public void MarkFailed(Exception? ex = null)
    {
        if (_phase == StartupPhase.Starting)
        {
            _phase = StartupPhase.Failed;
            _failure = ex;
            if (_initEndTicks == 0)
            {
                Interlocked.CompareExchange(ref _initEndTicks, DateTime.UtcNow.Ticks, 0);
            }
        }
    }
}
