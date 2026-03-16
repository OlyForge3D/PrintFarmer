namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Result of an AI-powered failure detection analysis.
/// </summary>
public sealed class FailureDetectionResult
{
    /// <summary>
    /// Indicates whether a failure was detected based on the confidence threshold.
    /// </summary>
    public bool IsFailureDetected { get; init; }

    /// <summary>
    /// Confidence score from the ML model (0.0 to 1.0).
    /// Higher values indicate higher likelihood of failure.
    /// </summary>
    public decimal Confidence { get; init; }

    /// <summary>
    /// Timestamp when the analysis was performed.
    /// </summary>
    public DateTime AnalyzedAt { get; init; }

    /// <summary>
    /// Error message if the analysis failed.
    /// Null if analysis succeeded.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful failure detection result.
    /// </summary>
    public static FailureDetectionResult Success(decimal confidence, bool isFailureDetected)
    {
        return new FailureDetectionResult
        {
            IsFailureDetected = isFailureDetected,
            Confidence = confidence,
            AnalyzedAt = DateTime.UtcNow,
            ErrorMessage = null
        };
    }

    /// <summary>
    /// Creates a failed failure detection result with an error message.
    /// </summary>
    public static FailureDetectionResult Error(string errorMessage)
    {
        return new FailureDetectionResult
        {
            IsFailureDetected = false,
            Confidence = 0m,
            AnalyzedAt = DateTime.UtcNow,
            ErrorMessage = errorMessage
        };
    }
}
