namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Result of a printer control command (set temps, move, etc.). Replaces the
/// boolean return value for the gated endpoints so the API layer can map
/// outcomes to distinct HTTP status codes (404 vs 409 vs 502).
/// </summary>
public enum PrinterControlOutcome
{
    Ok,
    NotFound,
    BackendUnsupported,
    BackendBusy,
    BackendUnreachable,
}
