namespace Farm.Infrastructure.Services.RateLimiting;

public interface IRateLimitService
{
    Task<RateLimitResult> CheckPasswordResetLimitAsync(string email, CancellationToken ct = default);

    Task RecordPasswordResetAttemptAsync(string email, CancellationToken ct = default);

    Task<RateLimitResult> CheckEmailConfirmationLimitAsync(string email, CancellationToken ct = default);

    Task RecordEmailConfirmationAttemptAsync(string email, CancellationToken ct = default);

    // Slice job submission limits (per user ID)
    Task<RateLimitResult> CheckSliceJobSubmitLimitAsync(Guid userId, CancellationToken ct = default);

    Task RecordSliceJobSubmitAttemptAsync(Guid userId, CancellationToken ct = default);

    // Authentication endpoint limits (per IP address)
    Task<RateLimitResult> CheckLoginLimitAsync(string ipAddress, CancellationToken ct = default);

    Task RecordLoginAttemptAsync(string ipAddress, CancellationToken ct = default);

    Task<RateLimitResult> CheckRegisterLimitAsync(string ipAddress, CancellationToken ct = default);

    Task RecordRegisterAttemptAsync(string ipAddress, CancellationToken ct = default);
}

public record RateLimitResult(
    bool IsAllowed,
    int RemainingAttempts,
    TimeSpan? RetryAfter = null,
    string? Message = null);
