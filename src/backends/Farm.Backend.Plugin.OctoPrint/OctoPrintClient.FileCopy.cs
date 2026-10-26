using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.OctoPrint;

public partial class OctoPrintClient : ISupportsPrinterFileCopy
{
    public Task<PrinterFileCopyBytes> CopyFileToAsync(
        string baseUrl, string storageRoot, string relativePath, PrinterCredential? credential,
        Stream destination, long maximumBytes, CancellationToken ct) =>
        PrinterFileCopyTransport.CopyAsync(
            _httpClient,
            PrinterFileCopyTransport.BuildUri(baseUrl, storageRoot, relativePath, "local", "downloads/files/local/"),
            credential, destination, maximumBytes, ct);
}
