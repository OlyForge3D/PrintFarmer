namespace Farm.Web.Api.DTOs.Retries;

/// <summary>
/// Request to check if a job should be automatically retried.
/// </summary>
public class CheckRetryRequest
{
    /// <summary>
    /// The error category that occurred (e.g., "Recoverable", "Temporary", "Hardware", "Material").
    /// </summary>
    public string ErrorCategory { get; set; } = string.Empty;
}
