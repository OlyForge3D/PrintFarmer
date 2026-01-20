namespace Farm.Infrastructure.Normalization;

/// <summary>
/// Centralized utility for URL normalization to ensure consistent handling across the application.
/// Consolidates similar functionality previously scattered throughout multiple services.
/// </summary>
public static class UrlNormalizer
{
    /// <summary>
    /// Normalizes a base URL by ensuring it doesn't have a trailing slash.
    /// Used consistently across all HTTP clients and API integrations.
    /// </summary>
    /// <param name="baseUrl">The base URL to normalize</param>
    /// <returns>Normalized URL without trailing slash</returns>
    /// <exception cref="ArgumentException">Thrown when baseUrl is null, empty, or whitespace</exception>
    public static string NormalizeBaseUrl(string baseUrl)
    {
        return string.IsNullOrWhiteSpace(baseUrl)
            ? throw new ArgumentException("Base URL cannot be null or empty", nameof(baseUrl))
            : baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Normalizes a base URL by ensuring it doesn't have a trailing slash.
    /// Returns null if the input is null or whitespace, otherwise normalizes.
    /// </summary>
    /// <param name="baseUrl">The base URL to normalize, can be null or whitespace</param>
    /// <returns>Normalized URL without trailing slash, or null if input was null/whitespace</returns>
    public static string? NormalizeBaseUrlNullable(string? baseUrl)
    {
        return string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Ensures a URL has a proper URI format with scheme.
    /// Adds http:// scheme if missing, validates format, and returns a proper Uri object.
    /// </summary>
    /// <param name="baseUrl">The base URL to validate and ensure has a scheme</param>
    /// <returns>A Uri with proper scheme</returns>
    /// <exception cref="ArgumentException">Thrown when URL is invalid</exception>
    public static Uri EnsureBaseUri(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL is required", nameof(baseUrl));
        }

        // Ensure scheme but do not force a port; preserve caller-provided formatting
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? abs))
        {
            return abs;
        }

        // Prepend http:// if missing a scheme
        if (Uri.TryCreate("http://" + baseUrl.Trim(), UriKind.Absolute, out abs))
        {
            return abs;
        }

        // Fallback: treat as http
        return new UriBuilder("http", baseUrl.Trim()).Uri;
    }

    /// <summary>
    /// Combines a base URL (without trailing slash) with a relative path (with leading slash).
    /// Used for constructing full URLs for file endpoints and API paths.
    /// </summary>
    /// <param name="baseUrl">The base URL (will be trimmed of trailing slash)</param>
    /// <param name="relativePath">The relative path (should start with / or be combined properly)</param>
    /// <returns>Combined URL with proper path separation</returns>
    public static string CombineUrl(string baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL cannot be null or empty", nameof(baseUrl));
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return NormalizeBaseUrl(baseUrl);
        }

        string normalizedBase = NormalizeBaseUrl(baseUrl);
        string normalizedPath = relativePath.TrimStart('/');

        return $"{normalizedBase}/{normalizedPath}";
    }

    /// <summary>
    /// Combines a base URL with a relative path, handling absolute URLs gracefully.
    /// If relativePath is an absolute URL, returns it as-is.
    /// Otherwise combines base URL with relative path.
    /// </summary>
    /// <param name="baseUrl">The base URL (will be trimmed of trailing slash)</param>
    /// <param name="relativePath">The relative path (or absolute URL)</param>
    /// <returns>Combined URL if relative, or the absolute URL if absolute</returns>
    public static string CombineUrlSmart(string baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return NormalizeBaseUrl(baseUrl);
        }

        // If relative path is absolute URL, return as-is
        return relativePath.StartsWith("http://") || relativePath.StartsWith("https://") ? relativePath : CombineUrl(baseUrl, relativePath);
    }
}
