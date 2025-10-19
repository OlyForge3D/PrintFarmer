namespace Farm.Web.Api.Services.JobDispatch;

/// <summary>
/// Configuration options for job dispatch retry behavior
/// </summary>
public class RetryOptions
{
    /// <summary>
    /// Maximum number of retry attempts for transient failures
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay in milliseconds between retry attempts
    /// </summary>
    public int BaseDelayMs { get; set; } = 250;

    /// <summary>
    /// Multiplier for exponential backoff (delay = BaseDelayMs * Multiplier^(attempt-1))
    /// </summary>
    public double Multiplier { get; set; } = 2.0;
}
