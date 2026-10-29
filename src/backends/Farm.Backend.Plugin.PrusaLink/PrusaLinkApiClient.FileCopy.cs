using System.Text.Json;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkApiClient
{
    private const int MaximumDownloadReferenceLength = 1024;

    /// <inheritdoc />
    public async Task<PrinterFileCopyBytes> CopyFileToAsync(
        string baseUrl, string storageRoot, string relativePath, PrinterCredential? credential,
        Stream destination, long maximumBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        if (!PrinterInventoryPath.TryCanonicalizeRoot(storageRoot, out string root) ||
            root != storageRoot || root.Contains('%'))
        {
            throw new PrinterFileCopyException("invalid_source_path");
        }

        string rootPrefix = string.Join('/', root.Split('/').Select(Uri.EscapeDataString));
        Uri detailsUri = PrinterFileCopyTransport.BuildUri(
            baseUrl, root, relativePath, root, "api/v1/files/" + rootPrefix + "/");
        Uri downloadUri = PrinterFileCopyTransport.BuildUri(
            baseUrl, root, relativePath, root, rootPrefix + "/");
        string[] downloadSegments = [.. root.Split('/'), .. relativePath.Split('/')];
        using JsonDocument details = await ReadFileCopyDetailsAsync(
            detailsUri, relativePath, credential, ct).ConfigureAwait(false);
        JsonElement references = details.RootElement.TryGetProperty("refs", out JsonElement refs)
            ? refs
            : default;
        if (!IsExpectedDownloadReference(
            ReadFilePreviewString(references, "download"), downloadUri, downloadSegments))
        {
            throw new PrinterFileCopyException("unsupported_download_reference");
        }

        BackendHttpTransport transport = GetClientForCredentials(credential);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, downloadUri.AbsoluteUri, credential);
        using HttpResponseMessage response = await transport.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await PrinterFileCopyTransport.CopyResponseAsync(
            response, destination, maximumBytes, ct).ConfigureAwait(false);
    }

    // Revision checks belong to the copy service's before/after inventory reads; here only identity is pinned.
    private async Task<JsonDocument> ReadFileCopyDetailsAsync(
        Uri uri, string relativePath, PrinterCredential? credential, CancellationToken ct)
    {
        byte[] bytes = await PrinterFilePreviewTransport.ReadAsync(
            token => SendFilePreviewRequestAsync(uri, credential, token),
            PrinterFilePreviewTransport.MaximumMetadataBytes, ct).ConfigureAwait(false);
        JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PrinterFileCopyException("invalid_preview_metadata");
            }

            string leaf = relativePath[(relativePath.LastIndexOf('/') + 1)..];
            if (ReadFilePreviewString(document.RootElement, "type") != "PRINT_FILE" ||
                !string.Equals(ReadFilePreviewString(document.RootElement, "name"), leaf, StringComparison.Ordinal))
            {
                throw new PrinterFileCopyException("source_changed");
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static bool IsExpectedDownloadReference(
        string? reference, Uri downloadUri, string[] downloadSegments)
    {
        if (reference is not { Length: > 0 and <= MaximumDownloadReferenceLength })
        {
            return false;
        }

        if (reference[0] != '/')
        {
            return Uri.TryCreate(reference, UriKind.Absolute, out Uri? absolute) &&
                string.Equals(absolute.AbsoluteUri, downloadUri.AbsoluteUri, StringComparison.Ordinal);
        }

        string[] segments = reference[1..].Split('/');
        if (segments.Length != downloadSegments.Length)
        {
            return false;
        }

        for (int index = 0; index < segments.Length; index++)
        {
            if (!string.Equals(
                Uri.UnescapeDataString(segments[index]), downloadSegments[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
