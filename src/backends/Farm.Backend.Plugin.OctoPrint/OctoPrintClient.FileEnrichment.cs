using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.OctoPrint;

public partial class OctoPrintClient : ISupportsPrinterFileEnrichment
{
    /// <inheritdoc />
    /// <remarks>
    /// Reads the native <c>/api/files/local/{path}</c> details once and keeps only what OctoPrint itself reports
    /// (analysis time, filament length/volume and highest printed Z). Grams, colours, costs and temperatures are
    /// never inferred. A
    /// <c>refs.download</c> value is honoured for bounded Range reads only when it equals the canonical
    /// <c>/downloads/files/local/{path}</c> URI, and no other reference is ever followed. Every wire attempt is
    /// charged to one per-file budget, redirects are refused, and the explicit download/copy capability is not used.
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
        if (FileEnrichmentSession.TryCreate(new BackendHttpTransport(_httpClient), baseUrl, observation, credential) is not { } session)
        {
            return new PrinterFileEnrichmentContent(
                Metadata: null,
                MetadataState: PrinterFileEnrichmentState.Unsupported,
                ThumbnailState: PrinterFileEnrichmentState.Unsupported,
                MetadataReason: "invalid_source_path",
                ThumbnailReason: "invalid_source_path");
        }

        PrinterFileEnrichmentSources sources = new(session.ReadNativeAsync, session.ReadRegionAsync, session.VerifyUnchangedAsync);
        return await PrinterFileEnrichmentAcquisition.AcquireAsync(observation, sources, captureThumbnail, ct)
            .ConfigureAwait(false);
    }

    private sealed class FileEnrichmentSession
    {
        private const int MaximumReferenceLength = 1024;
        private const int MaximumCacheBusterLength = 32;
        private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

        private readonly BackendHttpTransport _transport;
        private readonly string _baseUrl;
        private readonly PrinterFileInventoryEntry _observation;
        private readonly PrinterCredential? _credential;
        private readonly Uri _detailsUri;
        private readonly Uri _downloadUri;
        private readonly string[] _downloadSegments;
        private PrinterFileInventoryEntry? _pinned;
        private bool _downloadConfirmed;
        private string? _nativeFailure;

        private FileEnrichmentSession(
            BackendHttpTransport transport,
            string baseUrl,
            PrinterFileInventoryEntry observation,
            PrinterCredential? credential)
        {
            _transport = transport;
            _baseUrl = baseUrl;
            _observation = observation;
            _credential = credential;

            // BuildUri applies the same root, path and encoding validation as certified file copy. Only the
            // local store is ever inventoried, so any other root is rejected here without a request.
            _detailsUri = PrinterFileCopyTransport.BuildUri(
                baseUrl, observation.StorageRoot, observation.RelativePath, "local", "api/files/local/");
            _downloadUri = PrinterFileCopyTransport.BuildUri(
                baseUrl, observation.StorageRoot, observation.RelativePath, "local", "downloads/files/local/");
            _downloadSegments = _downloadUri.AbsolutePath[1..].Split('/').Select(Uri.UnescapeDataString).ToArray();
        }

        public static FileEnrichmentSession? TryCreate(
            BackendHttpTransport transport,
            string baseUrl,
            PrinterFileInventoryEntry observation,
            PrinterCredential? credential)
        {
            try
            {
                return new FileEnrichmentSession(transport, baseUrl, observation, credential);
            }
            catch (PrinterFileCopyException ex) when (ex.Reason == "invalid_source_path")
            {
                return null;
            }
        }

        public async Task<PrinterFileNativeEnrichment?> ReadNativeAsync(PrinterFileEnrichmentBudget budget)
        {
            try
            {
                NativeDetails details = await ReadDetailsAsync(budget).ConfigureAwait(false);
                _downloadConfirmed = details.DownloadConfirmed;
                Func<PrinterFileEnrichmentBudget, Task<byte[]?>>? readThumbnail = null;
                if (details.ThumbnailUri is { } thumbnailUri)
                {
                    readThumbnail = thumbnailBudget => ReadThumbnailAsync(thumbnailUri, thumbnailBudget);
                }

                return new PrinterFileNativeEnrichment(details.Metadata, details.Size, readThumbnail);
            }
            catch (PrinterFileCopyException ex)
            {
                // Lets the fallback report why it could not run instead of inventing a different reason.
                _nativeFailure = ex.Reason;
                throw;
            }
        }

        public Task<PrinterFileRegion> ReadRegionAsync(
            long offset, int count, long? expectedSize, PrinterFileEnrichmentBudget budget)
        {
            if (!_downloadConfirmed)
            {
                return Task.FromException<PrinterFileRegion>(
                    new PrinterFileCopyException(_nativeFailure ?? "unsupported_download_reference"));
            }

            return PrinterFileEnrichmentTransport.ReadRegionAsync(
                (range, token) => SendGetAsync(_downloadUri, range, budget, token),
                offset, count, expectedSize, budget);
        }

        public async Task VerifyUnchangedAsync(PrinterFileEnrichmentBudget budget)
        {
            if (_pinned is not null)
            {
                _ = await ReadDetailsAsync(budget).ConfigureAwait(false);
            }
        }

        // Digest/auth retries and redirect refusal are owned by the transport; the caller owns the response.
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

        // A missing thumbnail (404) is not an error; the caller keeps the metadata.
        private async Task<byte[]?> ReadThumbnailAsync(Uri uri, PrinterFileEnrichmentBudget budget)
        {
            try
            {
                return await PrinterFileEnrichmentTransport.ReadAsync(
                    token => SendGetAsync(uri, null, budget, token),
                    PrinterFileEnrichmentLimits.MaximumImageBytes,
                    budget).ConfigureAwait(false);
            }
            catch (PrinterFileCopyException ex) when (ex.Reason == "source_missing")
            {
                return null;
            }
        }

        private async Task<NativeDetails> ReadDetailsAsync(PrinterFileEnrichmentBudget budget)
        {
            byte[] bytes = await PrinterFileEnrichmentTransport.ReadAsync(
                token => SendGetAsync(_detailsUri, null, budget, token),
                PrinterFileEnrichmentLimits.MetadataBytes,
                budget).ConfigureAwait(false);
            using JsonDocument document = Parse(bytes);
            JsonElement details = document.RootElement;
            if (details.ValueKind != JsonValueKind.Object)
            {
                throw new PrinterFileCopyException("invalid_preview_metadata");
            }

            string? hash = ReadString(details, "hash") is { Length: > 0 and <= 256 } digest ? digest : null;
            PrinterFileInventoryEntry actual = new(
                _observation.StorageRoot,
                ReadString(details, "path") ?? string.Empty,
                PrinterFileInventorySession.Size(details),
                ProviderHash: hash,
                ModifiedValue: PrinterFileInventorySession.ModifiedValue(details, "date"));

            // Some servers omit listing sizes: the first details size is pinned for the final re-check.
            _pinned ??= _observation.SizeBytes is null ? _observation with { SizeBytes = actual.SizeBytes } : _observation;
            PrinterFilePreviewTransport.ValidateObservation(_pinned, actual);
            return new NativeDetails(
                ReadMetadata(details), actual.SizeBytes, IsExpectedDownloadReference(details), TryBuildThumbnailUri(details));
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

        // The printer's own reference is trusted only when it names exactly the file being enriched.
        private bool IsExpectedDownloadReference(JsonElement details)
        {
            if (!details.TryGetProperty("refs", out JsonElement refs) ||
                ReadString(refs, "download") is not { Length: > 0 and <= MaximumReferenceLength } reference)
            {
                return false;
            }

            string path = reference;
            if (reference[0] != '/')
            {
                if (!Uri.TryCreate(reference, UriKind.Absolute, out Uri? absolute) ||
                    !string.Equals(
                        absolute.GetLeftPart(UriPartial.Authority),
                        _downloadUri.GetLeftPart(UriPartial.Authority),
                        StringComparison.OrdinalIgnoreCase) ||
                    absolute.UserInfo.Length != 0 || absolute.Query.Length != 0 || absolute.Fragment.Length != 0)
                {
                    return false;
                }

                path = absolute.AbsolutePath;
            }

            string[] segments = path[1..].Split('/');
            if (segments.Length != _downloadSegments.Length)
            {
                return false;
            }

            for (int index = 0; index < segments.Length; index++)
            {
                if (!string.Equals(
                    Uri.UnescapeDataString(segments[index]), _downloadSegments[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        // Only a same-origin raster path under the printer's own host is ever requested. A trailing cache-buster
        // query is dropped, never forwarded.
        private Uri? TryBuildThumbnailUri(JsonElement details)
        {
            if (ReadString(details, "thumbnail") is not { Length: > 0 and <= MaximumReferenceLength } reference)
            {
                return null;
            }

            string path = reference;
            int query = reference.IndexOf('?');
            if (query >= 0)
            {
                string cacheBuster = reference[(query + 1)..];
                if (cacheBuster.Length is < 1 or > MaximumCacheBusterLength || !cacheBuster.All(char.IsAsciiLetterOrDigit))
                {
                    return null;
                }

                path = reference[..query];
            }

            if (path.StartsWith('/'))
            {
                path = path[1..];
            }

            if (!PrinterInventoryPath.TryCanonicalizeRelativePath(path, out _) || path.Contains('%') ||
                !ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                Uri uri = PrinterFileCopyTransport.BuildUri(_baseUrl, "local", path, "local", string.Empty);
                return uri == _downloadUri || uri == _detailsUri ? null : uri;
            }
            catch (PrinterFileCopyException)
            {
                return null;
            }
        }

        /// <summary>
        /// Maps only the analysis values OctoPrint reports: print time, filament length/volume and the highest
        /// printed Z. Weight, colour, cost, temperature and layer data are not part of its analysis and stay
        /// unknown; totals are summed only when every tool entry is valid.
        /// </summary>
        private static PrinterPrintMetadata? ReadMetadata(JsonElement details)
        {
            if (!details.TryGetProperty("gcodeAnalysis", out JsonElement analysis) ||
                analysis.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            PrinterPrintMetadata metadata = new()
            {
                EstimatedPrintTimeSeconds = PrinterFilePreviewTransport.PositiveNumber(analysis, "estimatedPrintTime"),
                MaxLayerZMm = analysis.TryGetProperty("printingArea", out JsonElement area)
                    ? PrinterFilePreviewTransport.PositiveNumber(area, "maxZ")
                    : null,
            };
            if (analysis.TryGetProperty("filament", out JsonElement filament) &&
                filament.ValueKind == JsonValueKind.Object)
            {
                metadata = metadata with
                {
                    FilamentUsedMm = SumTools(filament, "length"),
                    FilamentUsedCm3 = SumTools(filament, "volume"),
                };
            }

            return metadata.HasValues ? metadata : null;
        }

        private static double? SumTools(JsonElement filament, string property)
        {
            double total = 0;
            int tools = 0;
            foreach (JsonProperty tool in filament.EnumerateObject())
            {
                if (++tools > PrinterPrintMetadata.MaximumTools ||
                    tool.Value.ValueKind != JsonValueKind.Object ||
                    !tool.Value.TryGetProperty(property, out JsonElement value) ||
                    value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
                    !PrinterPrintMetadata.IsValidNumber(number))
                {
                    return null;
                }

                total += number;
            }

            return tools > 0 && total > 0 && PrinterPrintMetadata.IsValidNumber(total) ? total : null;
        }

        private static string? ReadString(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private sealed record NativeDetails(
            PrinterPrintMetadata? Metadata, long? Size, bool DownloadConfirmed, Uri? ThumbnailUri);
    }
}
