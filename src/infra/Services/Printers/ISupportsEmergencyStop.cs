using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Emergency shutdown through a transport that bypasses the controller G-code queue.</summary>
public interface ISupportsEmergencyStop
{
    /// <summary>Requests emergency shutdown without waiting for queued G-code to complete.</summary>
    Task<bool> EmergencyStopAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default);
}
