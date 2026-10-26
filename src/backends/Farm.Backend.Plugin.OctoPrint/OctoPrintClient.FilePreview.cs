using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.OctoPrint;

public partial class OctoPrintClient : ISupportsPrinterFilePreview
{
    /// <inheritdoc />
    public async Task<PrinterFilePreviewContent> GetFilePreviewAsync(
        string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential, CancellationToken ct)
    {
        Uri uri = PrinterFileCopyTransport.BuildUri(
            baseUrl, observation.StorageRoot, observation.RelativePath, "local", "api/files/local/");
        byte[] bytes = await PrinterFilePreviewTransport.ReadAsync(
            _httpClient, uri, credential, PrinterFilePreviewTransport.MaximumMetadataBytes, ct).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement metadata = document.RootElement;
        string? hash = metadata.TryGetProperty("hash", out JsonElement digest) &&
            digest.ValueKind == JsonValueKind.String && digest.GetString() is { Length: > 0 and <= 256 } value
                ? value : null;
        PrinterFilePreviewTransport.ValidateObservation(observation, new(
            observation.StorageRoot, metadata.GetProperty("path").GetString() ?? string.Empty,
            PrinterFileInventorySession.Size(metadata), ProviderHash: hash,
            ModifiedValue: PrinterFileInventorySession.ModifiedValue(metadata, "date")));
        double? printTime = metadata.TryGetProperty("gcodeAnalysis", out JsonElement analysis)
            ? PrinterFilePreviewTransport.PositiveNumber(analysis, "estimatedPrintTime") : null;

        // Core OctoPrint does not declare a standard thumbnail or layer-height API.
        return new(printTime, null);
    }
}
