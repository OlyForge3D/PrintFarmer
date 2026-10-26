using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient : ISupportsSafePrinterFileDelete
{
    public PrinterFileDeleteGuarantee GetDeleteGuarantee(string storageRoot, string relativePath) =>
        PrinterFileDeleteTransport.IsExactFile(storageRoot, relativePath, "gcodes")
            ? PrinterFileDeleteGuarantee.CurrentExactPath : PrinterFileDeleteGuarantee.Unsupported;

    public Task<PrinterFileDeleteResult> DeleteExactFileAsync(
        string baseUrl, string storageRoot, string relativePath, string? providerVersion,
        PrinterCredential? credential, CancellationToken ct) =>
        PrinterFileDeleteTransport.DeleteAsync(_http, baseUrl, storageRoot, relativePath,
            "gcodes", "server/files/gcodes/", credential, ct);
}
