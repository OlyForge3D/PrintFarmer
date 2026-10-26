using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkClient : ISupportsFileInventory
{
    /// <summary>Uses native storage discovery, never the guessed USB or legacy listing fallback.</summary>
    public Task<PrinterFileInventoryResult> GetFileInventoryAsync(
        string baseUrl, PrinterCredential? credential = null,
        PrinterFileInventoryOptions? options = null, CancellationToken ct = default) =>
        _apiClient.GetFileInventoryAsync(baseUrl, credential, options, ct);
}
