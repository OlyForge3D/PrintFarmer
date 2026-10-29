using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkClient
{
    public Task<PrinterFileCopyBytes> CopyFileToAsync(
        string baseUrl, string storageRoot, string relativePath, PrinterCredential? credential,
        Stream destination, long maximumBytes, CancellationToken ct) =>
        _apiClient.CopyFileToAsync(
            baseUrl, storageRoot, relativePath, credential, destination, maximumBytes, ct);
}
