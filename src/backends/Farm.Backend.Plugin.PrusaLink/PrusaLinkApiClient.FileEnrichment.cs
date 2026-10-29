using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkApiClient
{
    /// <inheritdoc />
    /// <remarks>
    /// Reads the native details document once and keeps its already-normalized <c>meta</c>. Only the printer's own
    /// thumbnail and, for fields the printer did not report, bounded Range reads of the exact file URI derived from
    /// the observation are requested. A <c>refs.download</c> value is honoured only when it equals that URI, and no
    /// other reference is ever followed. Every wire attempt, including a Digest retry, is charged to one budget.
    /// </remarks>
    public async Task<PrinterFileEnrichmentContent> EnrichFileAsync(
        string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential,
        bool captureThumbnail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(observation);
        ct.ThrowIfCancellationRequested();
        if (FileEnrichmentSession.TryCreate(this, baseUrl, observation, credential) is not { } session)
        {
            return new PrinterFileEnrichmentContent(
                Metadata: null,
                MetadataState: PrinterFileEnrichmentState.Unsupported,
                ThumbnailState: PrinterFileEnrichmentState.Unsupported,
                MetadataReason: "invalid_source_path",
                ThumbnailReason: "invalid_source_path");
        }

        PrinterFileEnrichmentSources sources = new(
            session.ReadNativeAsync, session.ReadRegionAsync, session.VerifyUnchangedAsync);
        return await PrinterFileEnrichmentAcquisition.AcquireAsync(
            observation, sources, captureThumbnail, ct, backend: PrinterBackend.PrusaLink)
            .ConfigureAwait(false);
    }

    private sealed class FileEnrichmentSession
    {
        private readonly PrusaLinkApiClient _owner;
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
            PrusaLinkApiClient owner, string baseUrl, PrinterFileInventoryEntry observation,
            PrinterCredential? credential, string root)
        {
            _owner = owner;
            _baseUrl = baseUrl;
            _observation = observation;
            _credential = credential;

            // BuildUri applies the same base, path and encoding validation as certified file copy.
            string rootPrefix = string.Join('/', root.Split('/').Select(Uri.EscapeDataString));
            _detailsUri = PrinterFileCopyTransport.BuildUri(
                baseUrl, root, observation.RelativePath, root, "api/v1/files/" + rootPrefix + "/");
            _downloadUri = PrinterFileCopyTransport.BuildUri(
                baseUrl, root, observation.RelativePath, root, rootPrefix + "/");
            _downloadSegments = [.. root.Split('/'), .. observation.RelativePath.Split('/')];
        }

        public static FileEnrichmentSession? TryCreate(
            PrusaLinkApiClient owner, string baseUrl, PrinterFileInventoryEntry observation,
            PrinterCredential? credential)
        {
            if (!PrinterInventoryPath.TryCanonicalizeRoot(observation.StorageRoot, out string root) ||
                root != observation.StorageRoot || root.Contains('%'))
            {
                return null;
            }

            try
            {
                return new FileEnrichmentSession(owner, baseUrl, observation, credential, root);
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
                await ReadDetailsAsync(budget).ConfigureAwait(false);
            }
        }

        // Digest authentication and redirect refusal are owned by the transport; the caller owns the response.
        private async Task<HttpResponseMessage> SendGetAsync(
            Uri uri, RangeHeaderValue? range, PrinterFileEnrichmentBudget budget, CancellationToken token)
        {
            BackendHttpTransport transport = _owner.GetClientForCredentials(_credential);
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, uri.AbsoluteUri, _credential);
            request.Headers.Range = range;
            return await transport.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, budget.BeginRequest, token)
                .ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendThumbnailAsync(
            Uri uri, PrinterFileEnrichmentBudget budget, CancellationToken token)
        {
            HttpResponseMessage response = await SendGetAsync(uri, null, budget, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                return response;
            }

#pragma warning disable IDISP017 // Prefer using - every other path hands the response to the caller
            response.Dispose();
#pragma warning restore IDISP017
            throw new PrinterFileCopyException("source_missing");
        }

        // A missing thumbnail (404 or 204) is not an error; the caller keeps the metadata.
        private async Task<byte[]?> ReadThumbnailAsync(Uri uri, PrinterFileEnrichmentBudget budget)
        {
            try
            {
                return await PrinterFileEnrichmentTransport.ReadAsync(
                    token => SendThumbnailAsync(uri, budget, token),
                    PrinterFileEnrichmentLimits.MaximumImageBytes, budget).ConfigureAwait(false);
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
                PrinterFileEnrichmentLimits.MetadataBytes, budget).ConfigureAwait(false);
            using JsonDocument document = Parse(bytes);
            JsonElement details = document.RootElement;
            if (details.ValueKind != JsonValueKind.Object)
            {
                throw new PrinterFileCopyException("invalid_preview_metadata");
            }

            // The details document names the leaf only; its folder is the observed folder.
            if (ReadFilePreviewString(details, "type") != "PRINT_FILE" ||
                ReadFilePreviewString(details, "name") is not { Length: > 0 } name || name.Contains('/'))
            {
                throw new PrinterFileCopyException("source_changed");
            }

            string directory = _observation.RelativePath[..(_observation.RelativePath.LastIndexOf('/') + 1)];
            PrinterFileInventoryEntry actual = new(
                _observation.StorageRoot, directory + name, PrinterFileInventorySession.Size(details),
                ModifiedValue: PrinterFileInventorySession.ModifiedValue(details, "m_timestamp"));

            // Some firmware omits listing sizes: the first details size is pinned for the final re-check.
            _pinned ??= _observation.SizeBytes is null
                ? _observation with { SizeBytes = actual.SizeBytes }
                : _observation;
            PrinterFilePreviewTransport.ValidateObservation(_pinned, actual);
            return new NativeDetails(
                ReadPrintFileMetadata(details.TryGetProperty("meta", out JsonElement meta) ? meta : default),
                actual.SizeBytes,
                HasExpectedDownloadReference(details),
                TryBuildFilePreviewThumbnailUri(_baseUrl, details));
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
        private bool HasExpectedDownloadReference(JsonElement details)
        {
            JsonElement refs = details.TryGetProperty("refs", out JsonElement value) ? value : default;
            return PrusaLinkApiClient.IsExpectedDownloadReference(
                ReadFilePreviewString(refs, "download"), _downloadUri, _downloadSegments);
        }

        private sealed record NativeDetails(
            PrinterPrintMetadata? Metadata, long? Size, bool DownloadConfirmed, Uri? ThumbnailUri);
    }
}
