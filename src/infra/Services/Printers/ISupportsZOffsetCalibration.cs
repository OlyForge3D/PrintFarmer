using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Plugin-owned calibration command transport, not evidence that firmware persists an offset.</summary>
public interface ISupportsZOffsetCalibration
{
    /// <summary>Sends the backend's legacy offset-save sequence for a decimal offset between -5 and 5 mm.</summary>
    Task<bool> SaveZOffsetAsync(string baseUrl, decimal offsetMm, PrinterCredential? credential, CancellationToken ct = default);
}
