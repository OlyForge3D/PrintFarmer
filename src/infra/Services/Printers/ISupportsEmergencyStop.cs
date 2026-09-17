using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Emergency shutdown using the backend's own mechanism; queue bypass is backend-specific.</summary>
public interface ISupportsEmergencyStop
{
    /// <summary>Requests emergency shutdown. Implementations must document whether their transport bypasses queued commands.</summary>
    Task<bool> EmergencyStopAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default);
}
