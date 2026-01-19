using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Retry policy configuration for failed print jobs
/// Controls automatic retry behavior with exponential backoff
/// </summary>
public class RetryPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Enable automatic retry on job failure
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum number of retry attempts (not counting original attempt)
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Initial delay in seconds before first retry (e.g., 60 = 1 minute)
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Exponential backoff multiplier (e.g., 2.0 = delay doubles each retry)
    /// Attempt 1: 60s, Attempt 2: 120s, Attempt 3: 240s, Attempt 4: 480s
    /// </summary>
    public double ExponentialBase { get; set; } = 2.0;

    /// <summary>
    /// Maximum delay cap in seconds (prevents infinite backoff growth)
    /// </summary>
    public int MaxDelaySeconds { get; set; } = 3600; // 1 hour

    /// <summary>
    /// Categories of errors that should trigger automatic retry
    /// </summary>
    public string RetryOnErrorCategories { get; set; } = "Recoverable"; // Comma-separated: "Recoverable,Unknown"

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Calculate delay in seconds for a given retry attempt number (1-based)
    /// </summary>
    public int GetDelaySeconds(int attemptNumber)
    {
        if (attemptNumber < 1)
        {
            return 0;
        }

        int delaySeconds = (int)Math.Min(
            InitialDelaySeconds * Math.Pow(ExponentialBase, attemptNumber - 1),
            MaxDelaySeconds);

        return Math.Max(delaySeconds, InitialDelaySeconds); // Never return less than initial delay
    }
}
