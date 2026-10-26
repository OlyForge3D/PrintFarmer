using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient : ISupportsPrinterFileCopy
{
    public Task<PrinterFileCopyBytes> CopyFileToAsync(
        string baseUrl, string storageRoot, string relativePath, PrinterCredential? credential,
        Stream destination, long maximumBytes, CancellationToken ct) =>
        PrinterFileCopyTransport.CopyAsync(
            _http,
            PrinterFileCopyTransport.BuildUri(baseUrl, storageRoot, relativePath, "gcodes", "server/files/gcodes/"),
            credential, destination, maximumBytes, ct);
}
