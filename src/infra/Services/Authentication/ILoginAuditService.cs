namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Records every login attempt (success and failure) to the dedicated audit log.
/// Captures the submitted username, real client IP, user agent, and normalized failure reason.
/// Never logs passwords in any form.
/// </summary>
public interface ILoginAuditService
{
    /// <summary>
    /// Record a single login attempt.
    /// </summary>
    /// <param name="username">Username or email as submitted. Nullable — attacker junk is allowed; will be truncated at 256 chars.</param>
    /// <param name="success">Whether authentication succeeded.</param>
    /// <param name="ipAddress">Real client IP. Respects X-Forwarded-For when behind nginx.</param>
    /// <param name="userAgent">User-Agent header, if present.</param>
    /// <param name="failureReason">Normalized failure code, e.g. "invalid_credentials", "account_locked", "account_disabled". Null on success.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAsync(
        string? username,
        bool success,
        string ipAddress,
        string? userAgent,
        string? failureReason,
        CancellationToken cancellationToken = default);
}
