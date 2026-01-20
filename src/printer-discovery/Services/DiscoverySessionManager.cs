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
    /// <param name="sessionId">The unique identifier for the discovery session.</param>
    /// <param name="cts">The cancellation token source for the session.</param>
    void RegisterSession(string sessionId, CancellationTokenSource cts);

    /// <summary>
    /// Remove a session after it completes.
    /// </summary>
    /// <param name="sessionId">The unique identifier for the session to remove.</param>
    void RemoveSession(string sessionId);

    /// <summary>
    /// Cancel an active session.
    /// </summary>
    /// <param name="sessionId">The unique identifier for the session to cancel.</param>
    /// <returns>True if the session was found and cancelled</returns>
    bool CancelSession(string sessionId);
}

public class DiscoverySessionManager(ILogger<DiscoverySessionManager> logger) : IDiscoverySessionManager
{
    private readonly Dictionary<string, CancellationTokenSource> _activeSessions = new();
    private readonly Lock _sessionsLock = new();
    private readonly ILogger<DiscoverySessionManager> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
