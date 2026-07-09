using Farm.Infrastructure.Domain;

namespace Farm.Backend.Plugin.Moonraker;

public interface ISnapmakerU1CameraMonitorManager
{
    Task<bool> EnsureMonitorStartedAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct);
}
