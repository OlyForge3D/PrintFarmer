using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkClient : ISupportsPrinterFilePreview
{
    /// <summary>Uses native file details and its thumbnail reference, never the print file itself.</summary>
    public Task<PrinterFilePreviewContent> GetFilePreviewAsync(
        string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential,
        CancellationToken ct) =>
        _apiClient.GetFilePreviewAsync(baseUrl, observation, credential, ct);
}
