namespace Farm.Infrastructure.Services.Printers;

/// <summary>Stable failure reasons shared by list and detail history probes.</summary>
public static class HistoryProbeFailureCodes
{
    /// <summary>The provider returned no usable response without a classified transport failure.</summary>
    public const string Unavailable = "history_unavailable";

    /// <summary>The provider could not be reached over its transport.</summary>
    public const string TransportUnavailable = "history_transport_unavailable";

    /// <summary>The provider operation exceeded its configured timeout.</summary>
    public const string Timeout = "history_timeout";
}

/// <summary>
/// Describes whether a backend history-list response is authoritative enough for
/// physical dispatch reconciliation.
/// </summary>
public enum HistoryProbeStatus
{
    /// <summary>The backend successfully returned an authoritative history list.</summary>
    Authoritative,

    /// <summary>The backend does not implement history queries.</summary>
    Unsupported,

    /// <summary>The backend supports history but did not return a usable response.</summary>
    Unavailable,

    /// <summary>The backend history query failed unexpectedly.</summary>
    Error,
}

/// <summary>
/// Typed result of probing a printer backend's history list.
/// </summary>
/// <param name="Status">Authority classification for the probe.</param>
/// <param name="History">History returned by a successful authoritative probe.</param>
/// <param name="FailureCode">Stable failure code for non-authoritative results.</param>
public sealed record HistoryListProbeResult(
    HistoryProbeStatus Status,
    HistoryListResponse? History,
    string? FailureCode)
{
    /// <summary>Creates an authoritative result, including a valid empty history.</summary>
    public static HistoryListProbeResult Authoritative(HistoryListResponse history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return new(HistoryProbeStatus.Authoritative, history, null);
    }

    /// <summary>Creates a result for a backend without history capability.</summary>
    public static HistoryListProbeResult Unsupported() =>
        new(HistoryProbeStatus.Unsupported, null, "history_unsupported");

    /// <summary>Creates a result for a supported backend that returned no usable response.</summary>
    public static HistoryListProbeResult Unavailable(
        string failureCode = HistoryProbeFailureCodes.Unavailable) =>
        new(HistoryProbeStatus.Unavailable, null, failureCode);

    /// <summary>Creates a result for an unexpected history-query failure.</summary>
    public static HistoryListProbeResult Error(string failureCode = "history_error") =>
        new(HistoryProbeStatus.Error, null, failureCode);
}

/// <summary>Authority classification for an exact backend-history detail probe.</summary>
public enum HistoryDetailProbeStatus
{
    /// <summary>The exact historical job was found.</summary>
    Found,

    /// <summary>The backend explicitly reported that the exact ID does not exist.</summary>
    NotFound,

    /// <summary>The backend does not implement history queries.</summary>
    Unsupported,

    /// <summary>The backend supports history but could not return a usable response.</summary>
    Unavailable,

    /// <summary>The backend returned malformed or otherwise invalid detail data.</summary>
    Error,
}

/// <summary>
/// Signals that a provider explicitly reported an exact history identifier as absent.
/// </summary>
public sealed class HistoryJobNotFoundException : KeyNotFoundException
{
    /// <summary>Initializes an explicit provider not-found outcome.</summary>
    public HistoryJobNotFoundException()
        : base("The requested history job was not found.")
    {
    }

    /// <summary>Initializes an explicit provider not-found outcome.</summary>
    /// <param name="message">The exception message.</param>
    public HistoryJobNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an explicit provider not-found outcome.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying provider exception.</param>
    public HistoryJobNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Typed result of probing one exact provider history identifier.
/// </summary>
/// <param name="Status">Authority classification for the probe.</param>
/// <param name="Job">Exact historical job when found.</param>
/// <param name="FailureCode">Stable code for non-found outcomes.</param>
public sealed record HistoryJobProbeResult(
    HistoryDetailProbeStatus Status,
    HistoryJob? Job,
    string? FailureCode)
{
    /// <summary>Creates a successful exact-job result.</summary>
    public static HistoryJobProbeResult Found(HistoryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(HistoryDetailProbeStatus.Found, job, null);
    }

    /// <summary>Creates an explicitly authoritative not-found result.</summary>
    public static HistoryJobProbeResult NotFound() =>
        new(HistoryDetailProbeStatus.NotFound, null, "history_job_not_found");

    /// <summary>Creates a result for a backend without history capability.</summary>
    public static HistoryJobProbeResult Unsupported() =>
        new(HistoryDetailProbeStatus.Unsupported, null, "history_unsupported");

    /// <summary>Creates a result for transport, timeout, or blank-response failure.</summary>
    public static HistoryJobProbeResult Unavailable(
        string failureCode = HistoryProbeFailureCodes.Unavailable) =>
        new(HistoryDetailProbeStatus.Unavailable, null, failureCode);

    /// <summary>Creates a result for malformed or invalid detail data.</summary>
    public static HistoryJobProbeResult Error(string failureCode = "history_error") =>
        new(HistoryDetailProbeStatus.Error, null, failureCode);
}
