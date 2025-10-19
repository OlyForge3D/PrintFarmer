using System.Collections.Concurrent;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.RateLimiting;

public class InMemoryRateLimitService : IRateLimitService
{
    private readonly RateLimitOptions _options;
    private readonly IUnifiedLoggingService _logger;
    private readonly ConcurrentDictionary<string, List<DateTime>> _passwordResetAttempts = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _emailConfirmationAttempts = new();

    public InMemoryRateLimitService(RateLimitOptions options, IUnifiedLoggingService logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<RateLimitResult> CheckPasswordResetLimitAsync(string email, CancellationToken ct = default)
    {
        return CheckLimitAsync(
            email,
            _passwordResetAttempts,
            _options.PasswordReset.MaxAttemptsPerHour,
            _options.PasswordReset.MaxAttemptsPerDay,
            "PasswordReset");
    }

    public Task RecordPasswordResetAttemptAsync(string email, CancellationToken ct = default)
    {
        RecordAttempt(email, _passwordResetAttempts);
        return Task.CompletedTask;
    }

    public Task<RateLimitResult> CheckEmailConfirmationLimitAsync(string email, CancellationToken ct = default)
    {
        return CheckLimitAsync(
            email,
            _emailConfirmationAttempts,
            _options.EmailConfirmation.MaxAttemptsPerHour,
            _options.EmailConfirmation.MaxAttemptsPerDay,
            "EmailConfirmation");
    }

    public Task RecordEmailConfirmationAttemptAsync(string email, CancellationToken ct = default)
    {
        RecordAttempt(email, _emailConfirmationAttempts);
        return Task.CompletedTask;
    }

    private Task<RateLimitResult> CheckLimitAsync(
        string key,
        ConcurrentDictionary<string, List<DateTime>> attempts,
        int maxPerHour,
        int maxPerDay,
        string operation)
    {
        var now = DateTime.UtcNow;
        var normalizedKey = key.ToLowerInvariant();

        if (!attempts.TryGetValue(normalizedKey, out var attemptList))
        {
            return Task.FromResult(new RateLimitResult(true, maxPerHour));
        }

        lock (attemptList)
        {
            // Remove old attempts (older than 24 hours)
            attemptList.RemoveAll(a => (now - a).TotalHours > 24);

            var attemptsInLastHour = attemptList.Count(a => (now - a).TotalHours < 1);
            var attemptsInLastDay = attemptList.Count;

            if (attemptsInLastHour >= maxPerHour)
            {
                var oldestInHour = attemptList.Where(a => (now - a).TotalHours < 1).Min();
                var retryAfter = TimeSpan.FromHours(1) - (now - oldestInHour);
                
                _logger.LogWarning($"{operation} rate limit exceeded for {normalizedKey} (hourly)", null, new
                {
                    Key = normalizedKey,
                    Operation = operation,
                    AttemptsInLastHour = attemptsInLastHour,
                    MaxPerHour = maxPerHour
                });

                return Task.FromResult(new RateLimitResult(
                    false,
                    0,
                    retryAfter,
                    $"Too many attempts. Please try again in {Math.Ceiling(retryAfter.TotalMinutes)} minutes."));
            }

            if (attemptsInLastDay >= maxPerDay)
            {
                var oldestInDay = attemptList.Min();
                var retryAfter = TimeSpan.FromHours(24) - (now - oldestInDay);
                
                _logger.LogWarning($"{operation} rate limit exceeded for {normalizedKey} (daily)", null, new
                {
                    Key = normalizedKey,
                    Operation = operation,
                    AttemptsInLastDay = attemptsInLastDay,
                    MaxPerDay = maxPerDay
                });

                return Task.FromResult(new RateLimitResult(
                    false,
                    0,
                    retryAfter,
                    $"Daily limit exceeded. Please try again in {Math.Ceiling(retryAfter.TotalHours)} hours."));
            }

            var remaining = Math.Min(maxPerHour - attemptsInLastHour, maxPerDay - attemptsInLastDay);
            return Task.FromResult(new RateLimitResult(true, remaining));
        }
    }

    private void RecordAttempt(string key, ConcurrentDictionary<string, List<DateTime>> attempts)
    {
        var normalizedKey = key.ToLowerInvariant();
        var now = DateTime.UtcNow;

        attempts.AddOrUpdate(
            normalizedKey,
            _ => [now],
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Add(now);
                    // Clean up old attempts while recording
                    existing.RemoveAll(a => (now - a).TotalHours > 24);
                }
                return existing;
            });
    }
}
