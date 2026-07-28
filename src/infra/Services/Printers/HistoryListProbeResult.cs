namespace Farm.Infrastructure.Services.Printers;

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
    public static HistoryListProbeResult Unavailable() =>
        new(HistoryProbeStatus.Unavailable, null, "history_unavailable");

    /// <summary>Creates a result for an unexpected history-query failure.</summary>
    public static HistoryListProbeResult Error(string failureCode = "history_error") =>
        new(HistoryProbeStatus.Error, null, failureCode);
}
