using System.Net;

namespace Farm.Infrastructure.Exceptions;

/// <summary>
/// Thrown when the upstream Spoolman REST API rejects a request. Carries the upstream
/// status code plus the human-readable detail parsed out of Spoolman's (FastAPI) error
/// body so the API layer can surface something actionable instead of the opaque
/// "Response status code does not indicate success" text produced by
/// <see cref="System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode"/>.
/// </summary>
public sealed class SpoolmanApiException : Exception
{
    /// <summary>Status code returned by Spoolman, when known.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Detail parsed from the Spoolman error body, when available.</summary>
    public string? Detail { get; }

    public SpoolmanApiException()
    {
    }

    public SpoolmanApiException(string message) : base(message)
    {
    }

    public SpoolmanApiException(string message, Exception inner) : base(message, inner)
    {
    }

    public SpoolmanApiException(HttpStatusCode statusCode, string operation, string? detail)
        : base(BuildMessage(statusCode, operation, detail))
    {
        StatusCode = statusCode;
        Detail = detail;
    }

    private static string BuildMessage(HttpStatusCode statusCode, string operation, string? detail)
    {
        string suffix = string.IsNullOrWhiteSpace(detail)
            ? statusCode.ToString()
            : detail;
        return $"Spoolman rejected the request to {operation} ({(int)statusCode}): {suffix}";
    }
}
