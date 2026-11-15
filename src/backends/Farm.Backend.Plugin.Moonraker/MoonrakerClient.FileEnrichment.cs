using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient : ISupportsPrinterFileEnrichment
{
    /// <inheritdoc />
    /// <remarks>
    /// Reads Moonraker's native <c>/server/files/metadata</c> once, then fills only still-missing fields from bounded
    /// <c>Range</c> reads of the canonical <c>/server/files/gcodes/{path}</c> download. Every wire attempt is counted
    /// against the per-file budget, redirects are refused, and the explicit download/copy capability is never used.
    /// </remarks>
    public async Task<PrinterFileEnrichmentContent> EnrichFileAsync(
        string baseUrl,
        PrinterFileInventoryEntry observation,
        PrinterCredential? credential,
        bool captureThumbnail,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(observation);
        ct.ThrowIfCancellationRequested();
        if (FileEnrichmentSession.TryCreate(new BackendHttpTransport(_http), baseUrl, observation, credential) is not { } session)
        {
            return new PrinterFileEnrichmentContent(
                Metadata: null,
                MetadataState: PrinterFileEnrichmentState.Unsupported,
                ThumbnailState: PrinterFileEnrichmentState.Unsupported,
                MetadataReason: "invalid_source_path",
                ThumbnailReason: "invalid_source_path");
        }

        PrinterFileEnrichmentSources sources = new(session.ReadNativeAsync, session.ReadRegionAsync, session.VerifyUnchangedAsync);
        return await PrinterFileEnrichmentAcquisition.AcquireAsync(
            observation, sources, captureThumbnail, ct, backend: PrinterBackend.Moonraker)
            .ConfigureAwait(false);
    }

    private sealed class FileEnrichmentSession
    {
        private const int MaximumThumbnailEntries = 32;
        private const int MaximumThumbnailRequests = 4;
        private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

        private readonly BackendHttpTransport _transport;
        private readonly string _baseUrl;
        private readonly PrinterFileInventoryEntry _observation;
        private readonly PrinterCredential? _credential;
        private readonly Uri _metadataUri;
        private readonly Uri _fileUri;
        private PrinterFileInventoryEntry? _pinned;

        private FileEnrichmentSession(
            BackendHttpTransport transport,
            string baseUrl,
            PrinterFileInventoryEntry observation,
            PrinterCredential? credential,
            Uri metadataUri,
            Uri fileUri)
        {
            _transport = transport;
            _baseUrl = baseUrl;
            _observation = observation;
            _credential = credential;
            _metadataUri = metadataUri;
            _fileUri = fileUri;
        }

        public static FileEnrichmentSession? TryCreate(
            BackendHttpTransport transport,
            string baseUrl,
            PrinterFileInventoryEntry observation,
            PrinterCredential? credential)
        {
            try
            {
                // BuildUri performs the same root/path/endpoint validation as certified file copy.
                Uri fileUri = PrinterFileCopyTransport.BuildUri(
                    baseUrl, observation.StorageRoot, observation.RelativePath, "gcodes", "server/files/gcodes/");
                Uri metadataUri = new(
                    new Uri(baseUrl.TrimEnd('/') + "/"),
                    "server/files/metadata?filename=" + Uri.EscapeDataString(observation.RelativePath));
                return new FileEnrichmentSession(transport, baseUrl, observation, credential, metadataUri, fileUri);
            }
            catch (PrinterFileCopyException ex) when (ex.Reason == "invalid_source_path")
            {
                return null;
            }
        }

        public async Task<PrinterFileNativeEnrichment?> ReadNativeAsync(PrinterFileEnrichmentBudget budget)
        {
            NativeDetails details;
            try
            {
                details = await ReadDetailsAsync(budget).ConfigureAwait(false);
            }
            catch (PrinterFileCopyException ex) when (ex.Reason == "source_missing")
            {
                // Moonraker answers 404 for a file it has not scanned yet. The bounded file read decides
                // whether the file is genuinely gone, so this is not conclusive on its own.
                return null;
            }

            Func<PrinterFileEnrichmentBudget, Task<byte[]?>>? readThumbnail = null;
            if (details.Thumbnails.Length > 0)
            {
                readThumbnail = thumbnailBudget => ReadThumbnailAsync(details.Thumbnails, thumbnailBudget);
            }

            return new PrinterFileNativeEnrichment(details.Metadata, details.Size, readThumbnail);
        }

        public Task<PrinterFileRegion> ReadRegionAsync(
            long offset, int count, long? expectedSize, PrinterFileEnrichmentBudget budget) =>
            PrinterFileEnrichmentTransport.ReadRegionAsync(
                (range, token) => SendGetAsync(_fileUri, range, budget, token), offset, count, expectedSize, budget);

        public async Task VerifyUnchangedAsync(PrinterFileEnrichmentBudget budget)
        {
            // Without a native observation there is nothing pinned to compare against.
            if (_pinned is not null)
            {
                _ = await ReadDetailsAsync(budget).ConfigureAwait(false);
            }
        }

        private async Task<HttpResponseMessage> SendGetAsync(
            Uri uri, RangeHeaderValue? range, PrinterFileEnrichmentBudget budget, CancellationToken ct)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.Range = range;
            if (_credential?.HasApiKey == true)
            {
                request.Headers.Add("X-Api-Key", _credential.ApiKey);
            }

            return await _transport
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.BeginRequest, ct)
                .ConfigureAwait(false);
        }

        private async Task<NativeDetails> ReadDetailsAsync(PrinterFileEnrichmentBudget budget)
        {
            byte[] bytes = await PrinterFileEnrichmentTransport.ReadAsync(
                token => SendGetAsync(_metadataUri, null, budget, token),
                PrinterFileEnrichmentLimits.MetadataBytes,
                budget).ConfigureAwait(false);
            using JsonDocument document = Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Object)
            {
                throw new PrinterFileCopyException("invalid_preview_metadata");
            }

            string filename = result.TryGetProperty("filename", out JsonElement name) &&
                name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty;
            PrinterFileInventoryEntry actual = new(
                _observation.StorageRoot,
                filename,
                PrinterFileInventorySession.Size(result),
                ModifiedValue: PrinterFileInventorySession.ModifiedValue(result, "modified"));
            _pinned ??= _observation.SizeBytes is null ? _observation with { SizeBytes = actual.SizeBytes } : _observation;
            PrinterFilePreviewTransport.ValidateObservation(_pinned, actual);
            return new NativeDetails(ReadMetadata(result), actual.SizeBytes, ReadThumbnailCandidates(result));
        }

        private async Task<byte[]?> ReadThumbnailAsync(Uri[] candidates, PrinterFileEnrichmentBudget budget)
        {
            byte[]? rejected = null;
            PrinterFileCopyException? failure = null;
            foreach (Uri uri in candidates)
            {
                byte[] image;
                try
                {
                    image = await PrinterFileEnrichmentTransport.ReadAsync(
                        token => SendGetAsync(uri, null, budget, token),
                        PrinterFileEnrichmentLimits.MaximumImageBytes,
                        budget).ConfigureAwait(false);
                }
                catch (PrinterFileCopyException ex) when (ex.Reason is "source_missing" or "size_limit" or "incomplete_response")
                {
                    // A broken candidate must not hide a smaller one that is still usable.
                    if (ex.Reason != "source_missing")
                    {
                        failure ??= ex;
                    }

                    continue;
                }

                if (PrinterFileImageValidator.TryValidate(image, out _, out _, out _))
                {
                    return image;
                }

                rejected ??= image;
            }

            // Hand an invalid image to the acquisition so it is reported as a failed thumbnail, not a missing one.
            return rejected ?? (failure is null ? null : throw new PrinterFileCopyException(failure.Reason));
        }

        private Uri[] ReadThumbnailCandidates(JsonElement result)
        {
            if (!result.TryGetProperty("thumbnails", out JsonElement thumbnails) ||
                thumbnails.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            // Native paths are relative to the G-code directory and are never treated as arbitrary URLs.
            string sourcePath = _observation.RelativePath;
            string directory = sourcePath.Contains('/') ? sourcePath[..(sourcePath.LastIndexOf('/') + 1)] : string.Empty;
            List<(Uri Location, long Pixels, long Size)> candidates = [];
            foreach (JsonElement item in thumbnails.EnumerateArray().Take(MaximumThumbnailEntries))
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    PrinterFileInventorySession.Size(item) > PrinterFileEnrichmentLimits.MaximumImageBytes ||
                    !item.TryGetProperty("relative_path", out JsonElement pathValue) ||
                    pathValue.ValueKind != JsonValueKind.String ||
                    pathValue.GetString() is not { } relativePath ||
                    !PrinterInventoryPath.TryCanonicalizeRelativePath(relativePath, out _) ||
                    relativePath.Contains('%') ||
                    !ImageExtensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                Uri imageUri;
                try
                {
                    imageUri = PrinterFileCopyTransport.BuildUri(
                        _baseUrl, _observation.StorageRoot, directory + relativePath, "gcodes", "server/files/gcodes/");
                }
                catch (PrinterFileCopyException)
                {
                    continue;
                }

                if (imageUri == _fileUri || candidates.Any(candidate => candidate.Location == imageUri))
                {
                    continue;
                }

                candidates.Add((
                    imageUri,
                    Dimension(item, "width") * Dimension(item, "height"),
                    PrinterFileInventorySession.Size(item) ?? 0));
            }

            return candidates
                .OrderByDescending(candidate => candidate.Pixels)
                .ThenByDescending(candidate => candidate.Size)
                .Take(MaximumThumbnailRequests)
                .Select(candidate => candidate.Location)
                .ToArray();
        }

        private static JsonDocument Parse(byte[] bytes)
        {
            try
            {
                return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException)
            {
                throw new PrinterFileCopyException("invalid_preview_metadata");
            }
        }

        private static long Dimension(JsonElement item, string property) =>
            item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number) && number > 0 ? number : 0;

        /// <summary>
        /// Maps only what Moonraker reports. Zero, negative, non-finite or malformed values stay unknown, and
        /// nothing is inferred beyond arithmetic on complete per-tool weights.
        /// </summary>
        private static PrinterPrintMetadata? ReadMetadata(JsonElement result)
        {
            JsonElement Value(string property) => result.TryGetProperty(property, out JsonElement value) ? value : default;
            double? Positive(string property) => ReadNumber(Value(property)) is { } number and > 0 ? number : null;
            int? PositiveInteger(string property) => Value(property) is { ValueKind: JsonValueKind.Number } value &&
                value.TryGetInt32(out int number) && number > 0 ? number : null;
            string? Text(string property) => ReadText(Value(property));

            // Moonraker reports the literal "Unknown" when it could not identify the slicer; that is no identity.
            string? slicer = Text("slicer") is { } name && !name.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? name : null;
            string? filamentType = Text("filament_type");
            PrinterFilamentTool[]? tools = ReadTools(Value("filament_weights"), Value("filament_colors"), ReadTypeSegments(filamentType));
            PrinterPrintMetadata metadata = new()
            {
                SlicerName = slicer,
                SlicerVersion = slicer is null ? null : Text("slicer_version"),
                LayerHeightMm = Positive("layer_height"),
                NozzleDiameterMm = Positive("nozzle_diameter"),
                EstimatedPrintTimeSeconds = Positive("estimated_time"),
                LayerCount = PositiveInteger("layer_count"),
                MaxLayerZMm = Positive("object_height"),
                NozzleTemperatureC = Positive("first_layer_extr_temp"),
                BedTemperatureC = Positive("first_layer_bed_temp"),
                FilamentUsedMm = Positive("filament_total"),
                FilamentUsedG = Positive("filament_weight_total"),
                FilamentType = filamentType,
                MaterialName = Text("filament_name"),
                Tools = tools,
                FilamentTypePerTool = tools is not null && tools.All(tool => tool.Material is not null)
                    ? tools.Select(tool => tool.Material!).ToArray()
                    : null,
                FilamentUsedPerToolG = tools is not null && tools.All(tool => tool.FilamentUsedG.HasValue)
                    ? tools.Select(tool => tool.FilamentUsedG!.Value).ToArray()
                    : null,
            };
            if (metadata.FilamentUsedG.HasValue)
            {
                metadata = metadata with { FilamentUsedGIsDerived = false };
            }
            else if (metadata.FilamentUsedPerToolG is { Length: > 0 } grams)
            {
                double total = grams.Sum();
                if (total > 0 && PrinterPrintMetadata.IsValidNumber(total))
                {
                    metadata = metadata with { FilamentUsedG = total, FilamentUsedGIsDerived = true };
                }
            }

            return metadata.HasValues ? metadata : null;
        }

        private static PrinterFilamentTool[]? ReadTools(JsonElement weights, JsonElement colors, string[]? types)
        {
            int weightCount = ToolCount(weights);
            int colorCount = ToolCount(colors);
            int count = Math.Max(weightCount, colorCount);
            if (count == 0 && types is { Length: > 1 })
            {
                count = types.Length;
            }

            // Arrays that disagree on the tool count cannot be aligned by index without guessing.
            if (count == 0 || (weightCount > 0 && colorCount > 0 && weightCount != colorCount))
            {
                return null;
            }

            string[]? materials = types is not null && types.Length == count ? types : null;
            PrinterFilamentTool[] tools = Enumerable.Range(0, count).Select(index => new PrinterFilamentTool
            {
                Index = index,
                Material = materials?[index],
                Color = colorCount > 0 && ReadText(colors[index]) is { } color && PrinterFilamentTool.IsValidColor(color)
                    ? color
                    : null,
                FilamentUsedG = weightCount > 0 ? ReadNumber(weights[index]) : null,
            }).ToArray();

            // Entries without any value would only claim tools the printer never described.
            return tools.Any(tool => tool.Material is not null || tool.Color is not null || tool.FilamentUsedG.HasValue)
                ? tools
                : null;
        }

        // Moonraker joins one filament type per extruder with ';'.
        private static string[]? ReadTypeSegments(string? type)
        {
            if (type is null)
            {
                return null;
            }

            string[] segments = type.Split(';');
            return segments.Length <= PrinterPrintMetadata.MaximumTools && segments.All(PrinterPrintMetadata.IsValidText)
                ? segments
                : null;
        }

        private static int ToolCount(JsonElement value) =>
            value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 and <= PrinterPrintMetadata.MaximumTools
                ? value.GetArrayLength()
                : 0;

        private static double? ReadNumber(JsonElement value) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) &&
            PrinterPrintMetadata.IsValidNumber(number) ? number : null;

        private static string? ReadText(JsonElement value) =>
            value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
            PrinterPrintMetadata.IsValidText(text) ? text : null;

        private sealed record NativeDetails(PrinterPrintMetadata? Metadata, long? Size, Uri[] Thumbnails);
    }
}
