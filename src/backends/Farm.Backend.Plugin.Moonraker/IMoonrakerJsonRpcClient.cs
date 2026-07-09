using Farm.Infrastructure.Domain;

namespace Farm.Backend.Plugin.Moonraker;

public interface IMoonrakerJsonRpcClient
{
    Task SendMethodAsync(Uri baseUrl, string method, PrinterCredential? credential, CancellationToken ct);
}
