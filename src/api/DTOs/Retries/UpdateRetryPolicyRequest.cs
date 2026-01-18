namespace Farm.Web.Api.DTOs.Retries;

/// <summary>
/// Request to update the retry policy configuration.
/// </summary>
public class UpdateRetryPolicyRequest
{
    /// <summary>
    /// Whether automatic retry should be enabled.
    /// </summary>
    public required bool IsEnabled { get; set; }

    /// <summary>
    /// Maximum number of retry attempts (0-10).
    /// </summary>
    public required int MaxRetries { get; set; }

    /// <summary>
    /// Initial delay in seconds before first retry (1-3600).
    /// </summary>
    public required int InitialDelaySeconds { get; set; }

    /// <summary>
    /// Exponential backoff multiplier (1.0-5.0).
    /// </summary>
    public required double ExponentialBase { get; set; }

    /// <summary>
    /// Maximum delay in seconds between retries.
    /// Must be >= InitialDelaySeconds.
    /// </summary>
    public required int MaxDelaySeconds { get; set; }

    /// <summary>
    /// Comma-separated error categories to retry on.
    /// </summary>
    public string RetryOnErrorCategories { get; set; } = "Recoverable";
}
