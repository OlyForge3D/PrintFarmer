namespace Farm.Web.Api.DTOs.Retries;

/// <summary>
/// Response indicating whether a job should be automatically retried.
/// </summary>
public class CheckRetryResponse
{
    /// <summary>
    /// True if the job should be automatically retried based on policy and error category.
    /// </summary>
    public bool ShouldRetry { get; set; }
}
