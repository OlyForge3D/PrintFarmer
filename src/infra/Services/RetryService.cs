using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services;

/// <summary>
/// Service for managing job retry logic with exponential backoff.
/// Handles automatic retry of failed print jobs based on error categories.
/// </summary>
public interface IRetryService
{
    /// <summary>
    /// Get the current retry policy configuration
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<RetryPolicy> GetRetryPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the global retry policy
    /// </summary>
    /// <param name="policy">The retry policy configuration to apply.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<RetryPolicy> UpdateRetryPolicyAsync(
        RetryPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get retry history for a specific job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<IEnumerable<JobRetry>> GetRetryHistoryAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific retry attempt
    /// </summary>
    /// <param name="retryId">The unique identifier of the retry attempt.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<JobRetry?> GetRetryAsync(
        Guid retryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determine if a job should be automatically retried based on error category
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to evaluate.</param>
    /// <param name="errorCategory">The category of the error that caused the failure.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<bool> ShouldRetryAsync(
        Guid jobId,
        ErrorCategory errorCategory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new retry attempt for a failed job
    /// </summary>
    /// <param name="originalJobId">The unique identifier of the original failed job.</param>
    /// <param name="errorCategory">The category of the error that caused the failure.</param>
    /// <param name="failureReason">A description of why the job failed.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<JobRetry> CreateRetryAsync(
        Guid originalJobId,
        ErrorCategory errorCategory,
        string failureReason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update retry attempt status
    /// </summary>
    /// <param name="retryId">The unique identifier of the retry attempt.</param>
    /// <param name="newStatus">The new status to set for the retry.</param>
    /// <param name="notes">Optional notes about the status change.</param>
    /// <param name="actualRetryTime">The actual time the retry was executed.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<JobRetry> UpdateRetryStatusAsync(
        Guid retryId,
        string newStatus,
        string? notes = null,
        DateTime? actualRetryTime = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get pending retries that are due to execute
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<IEnumerable<JobRetry>> GetDueRetriesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculate delay before next retry attempt
    /// </summary>
    /// <param name="attemptNumber">The current retry attempt number (1-based).</param>
    /// <param name="policy">The retry policy containing delay configuration.</param>
    TimeSpan CalculateRetryDelay(int attemptNumber, RetryPolicy policy);
}

public class RetryService(AppDbContext dbContext, ILogger<RetryService> logger) : IRetryService
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<RetryService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<RetryPolicy> GetRetryPolicyAsync(CancellationToken cancellationToken = default)
    {
        // Get existing policy or create default
        RetryPolicy? policy = await _dbContext.RetryPolicies.FirstOrDefaultAsync(cancellationToken);

        if (policy is null)
        {
            _logger.LogInformation("No retry policy found, creating default");
            policy = new RetryPolicy
            {
                IsEnabled = true,
                MaxRetries = 3,
                InitialDelaySeconds = 60,
                ExponentialBase = 2.0,
                MaxDelaySeconds = 3600,
                RetryOnErrorCategories = "Recoverable"
            };

            _dbContext.RetryPolicies.Add(policy);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return policy;
    }

    /// <inheritdoc />
    public async Task<RetryPolicy> UpdateRetryPolicyAsync(
        RetryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        policy.UpdatedAt = DateTime.UtcNow;
        _dbContext.RetryPolicies.Update(policy);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Retry policy updated: MaxRetries={MaxRetries}, InitialDelay={InitialDelay}s",
            policy.MaxRetries, policy.InitialDelaySeconds);

        return policy;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<JobRetry>> GetRetryHistoryAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.JobRetries
            .Where(jr => jr.OriginalJobId == jobId)
            .OrderByDescending(jr => jr.AttemptNumber)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JobRetry?> GetRetryAsync(
        Guid retryId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.JobRetries.FindAsync(new object?[] { retryId }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ShouldRetryAsync(
        Guid jobId,
        ErrorCategory errorCategory,
        CancellationToken cancellationToken = default)
    {
        // Check if retry is enabled
        RetryPolicy policy = await GetRetryPolicyAsync(cancellationToken);
        if (!policy.IsEnabled)
        {
            _logger.LogInformation("Retry disabled globally for job {JobId}", jobId);
            return false;
        }

        // Check if error category is in retry list
        List<string> retryCategories = policy.RetryOnErrorCategories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        bool shouldRetry = retryCategories.Contains(errorCategory.ToString());
        if (!shouldRetry)
        {
            _logger.LogInformation(
                "Error category {ErrorCategory} not in retry policy for job {JobId}",
                errorCategory, jobId);
            return false;
        }

        // Check if we haven't exceeded max retries
        int retryCount = await _dbContext.JobRetries
            .CountAsync(jr => jr.OriginalJobId == jobId, cancellationToken);

        if (retryCount >= policy.MaxRetries)
        {
            _logger.LogInformation(
                "Job {JobId} has reached max retries ({MaxRetries})",
                jobId, policy.MaxRetries);
            return false;
        }

        _logger.LogInformation(
            "Job {JobId} eligible for retry: Attempt {AttemptNumber}/{MaxRetries}, Category={Category}",
            jobId, retryCount + 1, policy.MaxRetries, errorCategory);
        return true;
    }

    /// <inheritdoc />
    public async Task<JobRetry> CreateRetryAsync(
        Guid originalJobId,
        ErrorCategory errorCategory,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        RetryPolicy policy = await GetRetryPolicyAsync(cancellationToken);

        // Get existing retries to determine attempt number
        int existingRetries = await _dbContext.JobRetries
            .Where(jr => jr.OriginalJobId == originalJobId)
            .CountAsync(cancellationToken);

        int attemptNumber = existingRetries + 1;
        TimeSpan delay = CalculateRetryDelay(attemptNumber, policy);

        var jobRetry = new JobRetry
        {
            Id = Guid.NewGuid(),
            OriginalJobId = originalJobId,
            RetryJobId = Guid.NewGuid(), // Will be set when actual retry job is created
            AttemptNumber = attemptNumber,
            ErrorCategory = errorCategory,
            FailureReason = failureReason,
            ScheduledRetryTime = DateTime.UtcNow.Add(delay),
            Status = "Pending"
        };

        _dbContext.JobRetries.Add(jobRetry);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created retry for job {OriginalJobId}: Attempt={Attempt}, DelaySeconds={DelaySeconds}, ScheduledTime={ScheduledTime}",
            originalJobId, attemptNumber, (int)delay.TotalSeconds, jobRetry.ScheduledRetryTime);

        return jobRetry;
    }

    /// <inheritdoc />
    public async Task<JobRetry> UpdateRetryStatusAsync(
        Guid retryId,
        string newStatus,
        string? notes = null,
        DateTime? actualRetryTime = null,
        CancellationToken cancellationToken = default)
    {
        JobRetry jobRetry = await GetRetryAsync(retryId, cancellationToken)
            ?? throw new InvalidOperationException($"Retry {retryId} not found");

        jobRetry.Status = newStatus;
        jobRetry.UpdatedAt = DateTime.UtcNow;

        if (notes is not null)
        {
            jobRetry.Notes = notes;
        }

        if (actualRetryTime.HasValue)
        {
            jobRetry.ActualRetryTime = actualRetryTime;
        }

        _dbContext.JobRetries.Update(jobRetry);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Updated retry {RetryId} to status {Status}",
            retryId, newStatus);

        return jobRetry;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<JobRetry>> GetDueRetriesAsync(CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        return await _dbContext.JobRetries
            .Where(jr => jr.Status == "Pending" && jr.ScheduledRetryTime <= now)
            .OrderBy(jr => jr.ScheduledRetryTime)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public TimeSpan CalculateRetryDelay(int attemptNumber, RetryPolicy policy)
    {
        if (attemptNumber < 1)
        {
            return TimeSpan.Zero;
        }

        int delaySeconds = (int)Math.Min(
            policy.InitialDelaySeconds * Math.Pow(policy.ExponentialBase, attemptNumber - 1),
            policy.MaxDelaySeconds);

        return TimeSpan.FromSeconds(Math.Max(delaySeconds, policy.InitialDelaySeconds));
    }
}
