namespace Farm.Infrastructure.Services.RateLimiting;

/// <summary>
/// Service for enforcing rate limits on various operations to prevent abuse.
/// Tracks attempt counts per identifier (email, user ID, IP address) with configurable windows.
/// </summary>
public interface IRateLimitService
{
    /// <summary>
    /// Checks if a password reset request is allowed for the specified email.
    /// </summary>
    /// <param name="email">The email address to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating whether the request is allowed and remaining attempts.</returns>
    Task<RateLimitResult> CheckPasswordResetLimitAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Records a password reset attempt for rate limiting purposes.
    /// </summary>
    /// <param name="email">The email address that attempted a password reset.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordPasswordResetAttemptAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Checks if an email confirmation request is allowed for the specified email.
    /// </summary>
    /// <param name="email">The email address to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating whether the request is allowed and remaining attempts.</returns>
    Task<RateLimitResult> CheckEmailConfirmationLimitAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Records an email confirmation attempt for rate limiting purposes.
    /// </summary>
    /// <param name="email">The email address that requested confirmation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordEmailConfirmationAttemptAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Checks if a slice job submission is allowed for the specified user.
    /// </summary>
    /// <param name="userId">The user ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating whether the submission is allowed and remaining attempts.</returns>
    Task<RateLimitResult> CheckSliceJobSubmitLimitAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Records a slice job submission attempt for rate limiting purposes.
    /// </summary>
    /// <param name="userId">The user ID that submitted a slice job.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordSliceJobSubmitAttemptAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a login attempt is allowed from the specified IP address.
    /// </summary>
    /// <param name="ipAddress">The IP address to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating whether the login is allowed and remaining attempts.</returns>
    Task<RateLimitResult> CheckLoginLimitAsync(string ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Records a login attempt for rate limiting purposes.
    /// </summary>
    /// <param name="ipAddress">The IP address that attempted to log in.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordLoginAttemptAsync(string ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Checks if a registration attempt is allowed from the specified IP address.
    /// </summary>
    /// <param name="ipAddress">The IP address to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating whether registration is allowed and remaining attempts.</returns>
    Task<RateLimitResult> CheckRegisterLimitAsync(string ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Records a registration attempt for rate limiting purposes.
    /// </summary>
    /// <param name="ipAddress">The IP address that attempted to register.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordRegisterAttemptAsync(string ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Checks if an OctoPrint-compatible upload is allowed for the specified key.
    /// </summary>
    /// <param name="key">The rate limit key (API key or IP address).</param>
    /// <param name="maxPerMinute">Maximum uploads allowed per minute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating whether the upload is allowed and remaining attempts.</returns>
    Task<RateLimitResult> CheckOctoPrintUploadLimitAsync(string key, int maxPerMinute, CancellationToken ct = default);

    /// <summary>
    /// Records an OctoPrint-compatible upload attempt for rate limiting purposes.
    /// </summary>
    /// <param name="key">The rate limit key (API key or IP address).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordOctoPrintUploadAttemptAsync(string key, CancellationToken ct = default);
}

public record RateLimitResult(
    bool IsAllowed,
    int RemainingAttempts,
    TimeSpan? RetryAfter = null,
    string? Message = null);
