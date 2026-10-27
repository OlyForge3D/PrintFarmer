using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkClient : ISupportsPrinterFileEnrichment
{
    /// <summary>Uses native file details, its thumbnail reference and bounded reads of the exact file only.</summary>
    public Task<PrinterFileEnrichmentContent> EnrichFileAsync(
        string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential,
        bool captureThumbnail, CancellationToken ct) =>
        _apiClient.EnrichFileAsync(baseUrl, observation, credential, captureThumbnail, ct);
}
