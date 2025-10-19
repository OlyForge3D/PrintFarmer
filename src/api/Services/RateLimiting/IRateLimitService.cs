namespace Farm.Web.Api.Services.RateLimiting;

public interface IRateLimitService
{
    Task<RateLimitResult> CheckPasswordResetLimitAsync(string email, CancellationToken ct = default);
    Task RecordPasswordResetAttemptAsync(string email, CancellationToken ct = default);
    
    Task<RateLimitResult> CheckEmailConfirmationLimitAsync(string email, CancellationToken ct = default);
    Task RecordEmailConfirmationAttemptAsync(string email, CancellationToken ct = default);
}

public record RateLimitResult(
    bool IsAllowed,
    int RemainingAttempts,
    TimeSpan? RetryAfter = null,
    string? Message = null);
