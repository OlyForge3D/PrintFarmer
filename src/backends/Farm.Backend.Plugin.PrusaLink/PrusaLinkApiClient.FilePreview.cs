using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkApiClient
{
    private const int MaximumThumbnailReferenceLength = 1024;

    /// <inheritdoc />
    /// <remarks>
    /// Reads the native per-file details document and, when it names one, the printer's own thumbnail.
    /// It never requests the print file, the download reference, or any caller- or printer-supplied
    /// URL outside the two native thumbnail prefixes.
    /// </remarks>
    public async Task<PrinterFilePreviewContent> GetFilePreviewAsync(
        string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!PrinterInventoryPath.TryCanonicalizeRoot(observation.StorageRoot, out string root) ||
            root != observation.StorageRoot || root.Contains('%'))
        {
            throw new PrinterFileCopyException("invalid_source_path");
        }

        // BuildUri applies the same base, path and encoding validation as certified file copy.
        string rootPrefix = string.Join('/', root.Split('/').Select(Uri.EscapeDataString));
        Uri detailsUri = PrinterFileCopyTransport.BuildUri(
            baseUrl, root, observation.RelativePath, root, "api/v1/files/" + rootPrefix + "/");
        using JsonDocument details = await ReadFilePreviewDetailsAsync(
            detailsUri, observation, credential, ct, allowSizeEnrichment: true)
            .ConfigureAwait(false);
        long? size = PrinterFileInventorySession.Size(details.RootElement);
        PrinterPrintMetadata? metadata = ReadPrintFileMetadata(
            details.RootElement.TryGetProperty("meta", out JsonElement meta) ? meta : default);
        double? printTime = metadata?.EstimatedPrintTimeSeconds is > 0 ? metadata.EstimatedPrintTimeSeconds :
            ParseFilePreviewDuration(metadata?.EstimatedPrintTimeNormalMode) ??
            (metadata?.PrintTimeSeconds is > 0 ? metadata.PrintTimeSeconds : null);
        PrinterFilePreviewContent content = new(
            printTime, metadata?.LayerHeightMm is > 0 ? metadata.LayerHeightMm : null, SizeBytes: size, Metadata: metadata);
        if (TryBuildFilePreviewThumbnailUri(baseUrl, details.RootElement) is not { } thumbnailUri)
        {
            return content;
        }

        using HttpResponseMessage response = await SendFilePreviewRequestAsync(thumbnailUri, credential, ct)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return content;
        }

        using MemoryStream buffer = new();
        await PrinterFileCopyTransport.CopyResponseAsync(
            response, buffer, PrinterFilePreviewTransport.MaximumImageBytes, ct).ConfigureAwait(false);
        byte[] image = buffer.ToArray();
        if (PrinterFilePreviewTransport.DetectImageContentType(image) is not { } contentType)
        {
            // QOI is a documented PrusaLink thumbnail format that browsers cannot render.
            return image.AsSpan().StartsWith("qoif"u8)
                ? content
                : throw new PrinterFileCopyException("invalid_preview_image");
        }

        // Detect ordinary overwrites during capture without claiming a version-bound snapshot.
        using JsonDocument after = await ReadFilePreviewDetailsAsync(
            detailsUri, observation with { SizeBytes = size }, credential, ct)
            .ConfigureAwait(false);
        return content with { Image = image, ContentType = contentType };
    }

    [GeneratedRegex(
        @"^(?:([0-9]{1,7})d\s*)?(?:([0-9]{1,7})h\s*)?(?:([0-9]{1,7})m\s*)?(?:([0-9]{1,7})s)?$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex FilePreviewDurationPattern();

    private async Task<JsonDocument> ReadFilePreviewDetailsAsync(
        Uri uri, PrinterFileInventoryEntry observation, PrinterCredential? credential, CancellationToken ct,
        bool allowSizeEnrichment = false)
    {
        byte[] bytes = await PrinterFilePreviewTransport.ReadAsync(
            token => SendFilePreviewRequestAsync(uri, credential, token),
            PrinterFilePreviewTransport.MaximumMetadataBytes, ct).ConfigureAwait(false);
        JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
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

            string directory = observation.RelativePath[..(observation.RelativePath.LastIndexOf('/') + 1)];
            PrinterFileInventoryEntry actual = new(
                observation.StorageRoot, directory + name, PrinterFileInventorySession.Size(details),
                ModifiedValue: PrinterFileInventorySession.ModifiedValue(details, "m_timestamp"));

            // Some firmware omits listing sizes. Pin the first detail size for the post-image check.
            PrinterFilePreviewTransport.ValidateObservation(
                allowSizeEnrichment && observation.SizeBytes is null
                    ? observation with { SizeBytes = actual.SizeBytes } : observation, actual);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    // Digest authentication and redirect refusal are owned by the transport; the caller owns the response.
    private async Task<HttpResponseMessage> SendFilePreviewRequestAsync(
        Uri uri, PrinterCredential? credential, CancellationToken ct)
    {
        BackendHttpTransport transport = GetClientForCredentials(credential);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, uri.AbsoluteUri, credential);
        return await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    private static string? ReadFilePreviewString(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Parses the optional "1d 2h 3m 4s" duration; anything else stays unknown.</summary>
    private static double? ParseFilePreviewDuration(string? text)
    {
        if (text is not { Length: > 0 and <= 64 } || FilePreviewDurationPattern().Match(text.Trim()) is not
            { Success: true } match)
        {
            return null;
        }

        long[] units = [86400, 3600, 60, 1];
        long total = 0;
        for (int index = 0; index < units.Length; index++)
        {
            Group group = match.Groups[index + 1];
            if (group.Success)
            {
                total += long.Parse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture) * units[index];
            }
        }

        return total > 0 ? total : null;
    }

    /// <summary>
    /// Accepts only the native thumbnail locations. Download, icon and arbitrary file references are
    /// never followed, and encoded separators or dot segments are refused rather than normalized.
    /// </summary>
    private static Uri? TryBuildFilePreviewThumbnailUri(string baseUrl, JsonElement details)
    {
        if (!details.TryGetProperty("refs", out JsonElement refs) ||
            ReadFilePreviewString(refs, "thumbnail") is not { } reference ||
            reference.Length is < 2 or > MaximumThumbnailReferenceLength || reference[0] != '/')
        {
            return null;
        }

        List<string> segments = [];
        foreach (string encoded in reference[1..].Split('/'))
        {
            string segment = Uri.UnescapeDataString(encoded);
            if (segment.Length == 0 || segment is "." or ".." ||
                segment.Any(c => char.IsControl(c) || c is '/' or '\\' or '%' or '?' or '#' or ':'))
            {
                return null;
            }

            segments.Add(segment);
        }

        bool nativeLocation = segments is ["thumb", _, ..] or ["api", "thumbnails", _, ..];
        if (!nativeLocation)
        {
            return null;
        }

        Uri origin = new(baseUrl.TrimEnd('/') + "/");
        Uri thumbnail = new(origin, string.Join('/', segments.Select(Uri.EscapeDataString)));
        return IsSameOrigin(origin, thumbnail) && thumbnail.UserInfo.Length == 0 ? thumbnail : null;
    }
}
