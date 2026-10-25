using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.OctoPrint;

public partial class OctoPrintClient : ISupportsSafePrinterFileDelete
{
    public PrinterFileDeleteGuarantee GetDeleteGuarantee(string storageRoot, string relativePath) =>
        PrinterFileDeleteTransport.IsExactFile(storageRoot, relativePath, "local")
            ? PrinterFileDeleteGuarantee.CurrentExactPath : PrinterFileDeleteGuarantee.Unsupported;

    // Deliberately bypass SendWithRetryAsync. Service requires a fresh machinecode file
    // inventory entry, not merely a syntactically valid path (OctoPrint also deletes folders).
    public Task<PrinterFileDeleteResult> DeleteExactFileAsync(
        string baseUrl, string storageRoot, string relativePath, string? providerVersion,
        PrinterCredential? credential, CancellationToken ct) =>
        PrinterFileDeleteTransport.DeleteAsync(_httpClient, baseUrl, storageRoot, relativePath,
            "local", "api/files/local/", credential, ct);
}
