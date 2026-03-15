using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.RateLimiting;

public class InMemoryRateLimitService(RateLimitOptions options, ILogger<InMemoryRateLimitService> logger) : IRateLimitService
{
    private readonly RateLimitOptions _options = options;
    private readonly ILogger<InMemoryRateLimitService> _logger = logger;
    private readonly ConcurrentDictionary<string, List<DateTime>> _passwordResetAttempts = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _emailConfirmationAttempts = new();
    private readonly ConcurrentDictionary<Guid, List<DateTime>> _sliceJobSubmitAttempts = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _loginAttempts = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _registerAttempts = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _octoPrintUploadAttempts = new();

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

    public Task<RateLimitResult> CheckSliceJobSubmitLimitAsync(Guid userId, CancellationToken ct = default)
    {
        int maxPerHour = _options.SliceJobs?.MaxAttemptsPerHour ?? 20;
        int maxPerDay = _options.SliceJobs?.MaxAttemptsPerDay ?? 200;
        return CheckGuidLimitAsync(userId, _sliceJobSubmitAttempts, maxPerHour, maxPerDay, "SliceJobSubmit");
    }

    public Task RecordSliceJobSubmitAttemptAsync(Guid userId, CancellationToken ct = default)
    {
        RecordGuidAttempt(userId, _sliceJobSubmitAttempts);
        return Task.CompletedTask;
    }

    public Task<RateLimitResult> CheckLoginLimitAsync(string ipAddress, CancellationToken ct = default)
    {
        int maxPerMinute = _options.Authentication?.MaxLoginAttemptsPerMinute ?? 10;
        return CheckShortTermLimitAsync(
            ipAddress,
            _loginAttempts,
            maxPerMinute,
            TimeSpan.FromMinutes(1),
            "Login");
    }

    public Task RecordLoginAttemptAsync(string ipAddress, CancellationToken ct = default)
    {
        RecordAttempt(ipAddress, _loginAttempts);
        return Task.CompletedTask;
    }

    public Task<RateLimitResult> CheckRegisterLimitAsync(string ipAddress, CancellationToken ct = default)
    {
        int maxPerMinute = _options.Authentication?.MaxRegisterAttemptsPerMinute ?? 10;
        return CheckShortTermLimitAsync(
            ipAddress,
            _registerAttempts,
            maxPerMinute,
            TimeSpan.FromMinutes(1),
            "Register");
    }

    public Task RecordRegisterAttemptAsync(string ipAddress, CancellationToken ct = default)
    {
        RecordAttempt(ipAddress, _registerAttempts);
        return Task.CompletedTask;
    }

    private Task<RateLimitResult> CheckLimitAsync(
        string key,
        ConcurrentDictionary<string, List<DateTime>> attempts,
        int maxPerHour,
        int maxPerDay,
        string operation)
    {
        DateTime now = DateTime.UtcNow;
        string normalizedKey = key.ToLowerInvariant();

        if (!attempts.TryGetValue(normalizedKey, out List<DateTime>? attemptList))
        {
            return Task.FromResult(new RateLimitResult(true, maxPerHour));
        }

        lock (attemptList)
        {
            // Remove old attempts (older than 24 hours)
            _ = attemptList.RemoveAll(a => (now - a).TotalHours > 24);

            int attemptsInLastHour = attemptList.Count(a => (now - a).TotalHours < 1);
            int attemptsInLastDay = attemptList.Count;

            if (attemptsInLastHour >= maxPerHour)
            {
                DateTime oldestInHour = attemptList.Where(a => (now - a).TotalHours < 1).Min();
                TimeSpan retryAfter = TimeSpan.FromHours(1) - (now - oldestInHour);

                _logger.LogWarning("{Operation} rate limit exceeded for {NormalizedKey} (hourly)", operation, normalizedKey);

                return Task.FromResult(new RateLimitResult(
                    false,
                    0,
                    retryAfter,
                    $"Too many attempts. Please try again in {Math.Ceiling(retryAfter.TotalMinutes)} minutes."));
            }

            if (attemptsInLastDay >= maxPerDay)
            {
                DateTime oldestInDay = attemptList.Min();
                TimeSpan retryAfter = TimeSpan.FromHours(24) - (now - oldestInDay);

                _logger.LogWarning("{Operation} rate limit exceeded for {NormalizedKey} (daily)", operation, normalizedKey);

                return Task.FromResult(new RateLimitResult(
                    false,
                    0,
                    retryAfter,
                    $"Daily limit exceeded. Please try again in {Math.Ceiling(retryAfter.TotalHours)} hours."));
            }

            int remaining = Math.Min(maxPerHour - attemptsInLastHour, maxPerDay - attemptsInLastDay);
            return Task.FromResult(new RateLimitResult(true, remaining));
        }
    }

    private Task<RateLimitResult> CheckGuidLimitAsync(
        Guid key,
        ConcurrentDictionary<Guid, List<DateTime>> attempts,
        int maxPerHour,
        int maxPerDay,
        string operation)
    {
        DateTime now = DateTime.UtcNow;

        if (!attempts.TryGetValue(key, out List<DateTime>? attemptList))
        {
            return Task.FromResult(new RateLimitResult(true, maxPerHour));
        }

        lock (attemptList)
        {
            _ = attemptList.RemoveAll(a => (now - a).TotalHours > 24);
            int attemptsInLastHour = attemptList.Count(a => (now - a).TotalHours < 1);
            int attemptsInLastDay = attemptList.Count;

            if (attemptsInLastHour >= maxPerHour)
            {
                DateTime oldestInHour = attemptList.Where(a => (now - a).TotalHours < 1).Min();
                TimeSpan retryAfter = TimeSpan.FromHours(1) - (now - oldestInHour);
                _logger.LogWarning("{Operation} rate limit exceeded for user {Key} (hourly)", operation, key);
                return Task.FromResult(new RateLimitResult(false, 0, retryAfter, $"Too many slice jobs this hour. Retry in {Math.Ceiling(retryAfter.TotalMinutes)} minutes."));
            }

            if (attemptsInLastDay >= maxPerDay)
            {
                DateTime oldestInDay = attemptList.Min();
                TimeSpan retryAfter = TimeSpan.FromHours(24) - (now - oldestInDay);
                _logger.LogWarning("{Operation} rate limit exceeded for user {Key} (daily)", operation, key);
                return Task.FromResult(new RateLimitResult(false, 0, retryAfter, $"Daily slice job limit reached. Retry in {Math.Ceiling(retryAfter.TotalHours)} hours."));
            }

            int remaining = Math.Min(maxPerHour - attemptsInLastHour, maxPerDay - attemptsInLastDay);
            return Task.FromResult(new RateLimitResult(true, remaining));
        }
    }

    private void RecordGuidAttempt(Guid key, ConcurrentDictionary<Guid, List<DateTime>> attempts)
    {
        DateTime now = DateTime.UtcNow;
        _ = attempts.AddOrUpdate(
            key,
            _ => [now],
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Add(now);
                    existing.RemoveAll(a => (now - a).TotalHours > 24);
                }

                return existing;
            });
    }

    private void RecordAttempt(string key, ConcurrentDictionary<string, List<DateTime>> attempts)
    {
        string normalizedKey = key.ToLowerInvariant();
        DateTime now = DateTime.UtcNow;

        _ = attempts.AddOrUpdate(
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

    public Task<RateLimitResult> CheckOctoPrintUploadLimitAsync(string key, int maxPerMinute, CancellationToken ct = default)
    {
        return CheckShortTermLimitAsync(
            key,
            _octoPrintUploadAttempts,
            maxPerMinute,
            TimeSpan.FromMinutes(1),
            "OctoPrintUpload");
    }

    public Task RecordOctoPrintUploadAttemptAsync(string key, CancellationToken ct = default)
    {
        RecordAttempt(key, _octoPrintUploadAttempts);
        return Task.CompletedTask;
    }

    private Task<RateLimitResult> CheckShortTermLimitAsync(
        string key,
        ConcurrentDictionary<string, List<DateTime>> attempts,
        int maxAttempts,
        TimeSpan window,
        string operation)
    {
        DateTime now = DateTime.UtcNow;
        string normalizedKey = key.ToLowerInvariant();

        if (!attempts.TryGetValue(normalizedKey, out List<DateTime>? attemptList))
        {
            return Task.FromResult(new RateLimitResult(true, maxAttempts));
        }

        lock (attemptList)
        {
            // Remove old attempts (outside the window)
            _ = attemptList.RemoveAll(a => (now - a) > window);

            int attemptsInWindow = attemptList.Count;

            if (attemptsInWindow >= maxAttempts)
            {
                DateTime oldestInWindow = attemptList.Min();
                TimeSpan retryAfter = window - (now - oldestInWindow);

                _logger.LogWarning("{Operation} rate limit exceeded for {NormalizedKey}", operation, normalizedKey);

                return Task.FromResult(new RateLimitResult(
                    IsAllowed: false,
                    RemainingAttempts: 0,
                    RetryAfter: retryAfter,
                    Message: $"Too many {operation.ToLower()} attempts. Please try again in {(int)retryAfter.TotalSeconds} seconds."));
            }

            return Task.FromResult(new RateLimitResult(true, maxAttempts - attemptsInWindow));
        }
    }
}
