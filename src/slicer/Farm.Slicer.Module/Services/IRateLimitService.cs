namespace Farm.Slicer.Module.Services;

/// <summary>
/// Adapter interface for rate limiting in slice job operations.
/// The host application provides the implementation bridging to its rate limit infrastructure.
/// </summary>
public interface IRateLimitService
{
    /// <summary>
    /// Check whether the specified key has exceeded its rate limit.
    /// </summary>
    /// <param name="key">Rate limit key (e.g., user ID, IP address).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating whether the request is allowed.</returns>
    Task<SlicerRateLimitResult> CheckAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Result of a rate limit check.
/// </summary>
/// <param name="IsAllowed">Whether the request is allowed.</param>
/// <param name="RetryAfterSeconds">Seconds until the next allowed request, if rate limited.</param>
public record SlicerRateLimitResult(bool IsAllowed, int? RetryAfterSeconds = null);
