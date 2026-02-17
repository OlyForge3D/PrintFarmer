namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Provides per-printer connection health snapshots from a backend service.
/// Implemented by Moonraker, SDCP, PrusaLink, and OctoPrint subscription/polling services.
/// </summary>
public interface IPrinterConnectionHealthProvider
{
    /// <summary>
    /// Returns connection health data for all printers managed by this backend.
    /// </summary>
    IReadOnlyDictionary<Guid, PrinterConnectionHealth> GetConnectionHealth();
}
