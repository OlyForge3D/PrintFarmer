using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient : ISupportsPrinterFilePreview
{
    /// <inheritdoc />
    public async Task<PrinterFilePreviewContent> GetFilePreviewAsync(
        string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential, CancellationToken ct)
    {
        // BuildUri performs the same root/path/endpoint validation as certified file copy.
        Uri fileUri = PrinterFileCopyTransport.BuildUri(
            baseUrl, observation.StorageRoot, observation.RelativePath, "gcodes", "server/files/gcodes/");
        Uri metadataUri = new(
            new Uri(baseUrl.TrimEnd('/') + "/"),
            "server/files/metadata?filename=" + Uri.EscapeDataString(observation.RelativePath));
        using JsonDocument document = await ReadPreviewMetadataAsync(metadataUri, observation, credential, ct)
            .ConfigureAwait(false);
        JsonElement metadata = document.RootElement.GetProperty("result");
        double? printTime = PrinterFilePreviewTransport.PositiveNumber(metadata, "estimated_time");
        double? layerHeight = PrinterFilePreviewTransport.PositiveNumber(metadata, "layer_height");
        if (!metadata.TryGetProperty("thumbnails", out JsonElement thumbnails) ||
            thumbnails.ValueKind != JsonValueKind.Array)
        {
            return new(printTime, layerHeight);
        }

        // Native metadata paths are relative to the G-code directory, not an arbitrary URL.
        string directory = observation.RelativePath.Contains('/')
            ? observation.RelativePath[..(observation.RelativePath.LastIndexOf('/') + 1)] : string.Empty;
        foreach (JsonElement thumbnail in thumbnails.EnumerateArray().OrderByDescending(
            item => PrinterFileInventorySession.Size(item) ?? 0))
        {
            if (PrinterFileInventorySession.Size(thumbnail) > PrinterFilePreviewTransport.MaximumImageBytes ||
                !thumbnail.TryGetProperty("relative_path", out JsonElement pathValue) ||
                pathValue.ValueKind != JsonValueKind.String ||
                pathValue.GetString() is not { } path ||
                !PrinterInventoryPath.TryCanonicalizeRelativePath(path, out _) || path.Contains('%'))
            {
                continue;
            }

            string? contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => null,
            };
            if (contentType is null)
            {
                continue;
            }

            Uri imageUri = PrinterFileCopyTransport.BuildUri(
                baseUrl, observation.StorageRoot, directory + path, "gcodes", "server/files/gcodes/");
            if (imageUri == fileUri)
            {
                continue;
            }

            byte[] image = await PrinterFilePreviewTransport.ReadAsync(
                _http, imageUri, credential, PrinterFilePreviewTransport.MaximumImageBytes, ct).ConfigureAwait(false);
            if (!PrinterFilePreviewTransport.IsValidImage(image, contentType))
            {
                throw new PrinterFileCopyException("invalid_preview_image");
            }

            // Detect ordinary overwrites during capture without claiming a version-bound snapshot.
            using JsonDocument after = await ReadPreviewMetadataAsync(metadataUri, observation, credential, ct)
                .ConfigureAwait(false);
            return new(printTime, layerHeight, image, contentType);
        }

        return new(printTime, layerHeight);
    }

    private async Task<JsonDocument> ReadPreviewMetadataAsync(
        Uri uri, PrinterFileInventoryEntry observation, PrinterCredential? credential, CancellationToken ct)
    {
        byte[] bytes = await PrinterFilePreviewTransport.ReadAsync(
            _http, uri, credential, PrinterFilePreviewTransport.MaximumMetadataBytes, ct).ConfigureAwait(false);
        JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
            JsonElement metadata = document.RootElement.GetProperty("result");
            PrinterFilePreviewTransport.ValidateObservation(observation, new(
                observation.StorageRoot, metadata.GetProperty("filename").GetString() ?? string.Empty,
                PrinterFileInventorySession.Size(metadata),
                ModifiedValue: PrinterFileInventorySession.ModifiedValue(metadata, "modified")));
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }
}
