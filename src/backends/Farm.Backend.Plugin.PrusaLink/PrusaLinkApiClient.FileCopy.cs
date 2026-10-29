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
        PrinterFileInventoryEntry observation = new(root, relativePath, SizeBytes: null);
        using JsonDocument details = await ReadFilePreviewDetailsAsync(
            detailsUri, observation, credential, ct).ConfigureAwait(false);
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
