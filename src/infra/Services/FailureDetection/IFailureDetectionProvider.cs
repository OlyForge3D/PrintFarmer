namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Provider-neutral contract for scoring a printer-camera image or URL.
/// </summary>
public interface IFailureDetectionProvider
{
    /// <summary>
    /// Gets the stable name of this provider.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Analyzes an image using the configured provider endpoint.
    /// </summary>
    /// <param name="request">Image data and provider endpoint credentials.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The provider score and any provider-specific failure classification.</returns>
    Task<FailureDetectionProviderResult> AnalyzeAsync(
        FailureDetectionProviderRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Image and endpoint details supplied to a failure-detection provider.
/// </summary>
/// <param name="ImageData">Captured image bytes to score, when scoring a shared frame.</param>
/// <param name="Endpoint">Provider API endpoint.</param>
/// <param name="ApiToken">Optional bearer token.</param>
/// <param name="SnapshotUrl">Optional camera URL for providers that support URL-first analysis.</param>
public sealed record FailureDetectionProviderRequest(
    byte[]? ImageData,
    string Endpoint,
    string? ApiToken = null,
    string? SnapshotUrl = null);

internal static class FailureDetectionImageValidator
{
    public static bool IsJpeg(byte[]? imageData)
    {
        return imageData is { Length: >= 3 }
            && imageData[0] == 0xFF
            && imageData[1] == 0xD8
            && imageData[2] == 0xFF;
    }
}

/// <summary>
/// Provider-neutral score and optional classification result.
/// </summary>
/// <param name="Score">Normalized provider score from zero to one.</param>
/// <param name="IsFailureDetected">Provider classification, when available.</param>
/// <param name="AnalyzedAt">UTC time the analysis completed.</param>
/// <param name="ErrorMessage">Analysis error, if the request failed.</param>
/// <param name="UsedCapturedImage">Whether the provider analyzed the captured image instead of fetching the snapshot URL itself.</param>
public sealed record FailureDetectionProviderResult(
    decimal Score,
    bool? IsFailureDetected,
    DateTime AnalyzedAt,
    string? ErrorMessage = null,
    bool? UsedCapturedImage = null);

/// <summary>
/// PrintGuard-specific provider contract.
/// </summary>
public interface IPrintGuardFailureDetectionProvider : IFailureDetectionProvider;
