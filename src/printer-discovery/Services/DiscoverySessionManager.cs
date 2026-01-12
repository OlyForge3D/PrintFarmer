namespace PrinterDiscovery.Services;

/// <summary>
/// Manages active discovery sessions and their cancellation tokens.
/// This is a singleton service to allow session cancellation across scoped service instances.
/// </summary>
public interface IDiscoverySessionManager
{
    /// <summary>
    /// Register a new session with its cancellation token source.
    /// </summary>
    void RegisterSession(string sessionId, CancellationTokenSource cts);

    /// <summary>
    /// Remove a session after it completes.
    /// </summary>
    void RemoveSession(string sessionId);

    /// <summary>
    /// Cancel an active session.
    /// </summary>
    /// <returns>True if the session was found and cancelled</returns>
    bool CancelSession(string sessionId);
}

public class DiscoverySessionManager : IDiscoverySessionManager
{
    private readonly Dictionary<string, CancellationTokenSource> _activeSessions = new();
    private readonly object _sessionsLock = new();
    private readonly ILogger<DiscoverySessionManager> _logger;

    public DiscoverySessionManager(ILogger<DiscoverySessionManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RegisterSession(string sessionId, CancellationTokenSource cts)
    {
        lock (_sessionsLock)
        {
            _activeSessions[sessionId] = cts;
            _logger.LogDebug("[SESSION-MANAGER] Registered session {SessionId}", sessionId);
        }
    }

    public void RemoveSession(string sessionId)
    {
        lock (_sessionsLock)
        {
            if (_activeSessions.Remove(sessionId, out CancellationTokenSource? cts))
            {
                cts.Dispose();
                _logger.LogDebug("[SESSION-MANAGER] Removed session {SessionId}", sessionId);
            }
        }
    }

    public bool CancelSession(string sessionId)
    {
        lock (_sessionsLock)
        {
            if (_activeSessions.TryGetValue(sessionId, out CancellationTokenSource? cts))
            {
                cts.Cancel();
                _logger.LogInformation("[SESSION-MANAGER] Cancelled session {SessionId}", sessionId);
                return true;
            }
        }

        _logger.LogWarning("[SESSION-MANAGER] Session {SessionId} not found for cancellation", sessionId);
        return false;
    }
}
