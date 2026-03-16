namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Service interface for AI-powered print failure detection using the Obico ML API.
/// </summary>
public interface IObicoFailureDetectionService
{
    /// <summary>
    /// Analyzes a JPEG image for print failures.
    /// </summary>
    /// <param name="imageData">JPEG image bytes to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Failure detection result with confidence score.</returns>
    Task<FailureDetectionResult> AnalyzeImageAsync(byte[] imageData, CancellationToken ct = default);

    /// <summary>
    /// Fetches an image from a URL and analyzes it for print failures.
    /// </summary>
    /// <param name="snapshotUrl">URL of the camera snapshot to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Failure detection result with confidence score.</returns>
    Task<FailureDetectionResult> AnalyzeImageFromUrlAsync(string snapshotUrl, CancellationToken ct = default);
}
