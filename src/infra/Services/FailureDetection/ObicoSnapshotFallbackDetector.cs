using System.Net;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Detects when an Obico-compatible GET prediction request failed because the ML server
/// could not reach the supplied snapshot URL, which allows a safe fallback to local fetch + upload.
/// </summary>
public static class ObicoSnapshotFallbackDetector
{
    private static readonly string[] SnapshotReachabilityIndicators =
    [
        "failed to fetch",
        "unable to fetch",
        "could not fetch",
        "cannot fetch",
        "failed to download",
        "unable to download",
        "could not download",
        "no route to host",
        "connection refused",
        "name or service not known",
        "temporary failure in name resolution",
        "timed out",
        "timeout",
    ];

    /// <summary>
    /// Detects status codes that mean the upstream GET contract itself is unavailable.
    /// </summary>
    public static bool ShouldFallbackToLegacyUpload(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotFound
            or HttpStatusCode.UnsupportedMediaType;
    }

    /// <summary>
    /// Detects Bad Request responses that indicate the ML server could not fetch the supplied snapshot URL.
    /// </summary>
    public static bool ShouldFallbackBecauseSnapshotWasUnreachable(HttpStatusCode statusCode, string? errorBody)
    {
        if (statusCode != HttpStatusCode.BadRequest || string.IsNullOrWhiteSpace(errorBody))
        {
            return false;
        }

        return SnapshotReachabilityIndicators.Any(indicator =>
            errorBody.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }
}
