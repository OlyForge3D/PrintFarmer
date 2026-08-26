namespace Farm.Web.Api.DTOs.Retries;

/// <summary>
/// Represents the current job retry policy configuration.
/// </summary>
public class RetryPolicyDto
{
    /// <summary>
    /// Unique identifier for the retry policy.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Whether automatic retry is enabled globally.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Maximum number of retry attempts allowed per job.
    /// </summary>
    public int MaxRetries { get; set; }

    /// <summary>
    /// Initial delay in seconds before first retry attempt.
    /// </summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>
    /// Exponential backoff multiplier (e.g., 2.0 means delay doubles each attempt).
    /// </summary>
    public double ExponentialBase { get; set; }

    /// <summary>
    /// Maximum delay in seconds between retry attempts.
    /// </summary>
    public int MaxDelaySeconds { get; set; }

    /// <summary>
    /// Comma-separated list of error categories that trigger automatic retry.
    /// </summary>
    public string RetryOnErrorCategories { get; set; } = string.Empty;
}
