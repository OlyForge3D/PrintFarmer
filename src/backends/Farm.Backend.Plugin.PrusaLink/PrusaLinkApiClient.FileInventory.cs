using System.Text.Json;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkApiClient
{
    /// <summary>Traverses only declared available storage using bounded, authenticated directory listings.</summary>
    public async Task<PrinterFileInventoryResult> GetFileInventoryAsync(
        string baseUrl, PrinterCredential? credential = null,
        PrinterFileInventoryOptions? options = null, CancellationToken ct = default)
    {
        using PrinterFileInventorySession scan = new(options, ct);
        try
        {
            using JsonDocument storageDocument = await ReadInventoryDocumentAsync(
                baseUrl, "api/v1/storage", credential, scan).ConfigureAwait(false);
            JsonElement storageResponse = storageDocument.RootElement;
            PrinterFileInventorySession.RequireUntruncated(storageResponse);
            HashSet<string> roots = new(StringComparer.Ordinal);
            bool unavailableStorage = false;
            foreach (JsonElement storage in storageResponse.GetProperty("storage_list").EnumerateArray())
            {
                scan.VisitEntry(0);
                if (!PrinterInventoryPath.TryCanonicalizeRoot(
                    storage.GetProperty("path").GetString(), out string root) || !roots.Add(root))
                {
                    throw new PrinterFileInventoryException(PrinterFileInventoryReason.InvalidPath);
                }

                if (!storage.GetProperty("available").GetBoolean())
                {
                    scan.Cover(root, false, PrinterFileInventoryEvidence.None);
                    unavailableStorage = true;
                    continue;
                }

                await ReadInventoryFolderAsync(baseUrl, root, string.Empty, 0, credential, scan)
                    .ConfigureAwait(false);
                scan.Cover(root, true, PrinterFileInventoryEvidence.TraversedDirectories);
            }

            if (scan.Options.IncludeFileDetails)
            {
                await scan.EnrichAsync(entry => entry.DetailsFetchedAtUtc.HasValue
                    ? Task.FromResult(entry)
                    : ReadInventoryDetailsAsync(baseUrl, entry, credential, scan)).ConfigureAwait(false);
            }

            return unavailableStorage
                ? scan.Failed(new PrinterFileInventoryException(PrinterFileInventoryReason.StorageUnavailable))
                : scan.Complete();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
            OperationCanceledException or JsonException or InvalidOperationException or
            KeyNotFoundException or PrinterFileInventoryException)
        {
            return scan.Failed(exception);
        }
    }

    private async Task ReadInventoryFolderAsync(
        string baseUrl, string root, string parent, int depth,
        PrinterCredential? credential, PrinterFileInventorySession scan)
    {
        if (depth > scan.Options.MaxDepth)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.DepthLimit);
        }

        string providerPath = root + (parent.Length == 0 ? string.Empty : "/" + parent);
        string escapedPath = string.Join('/', providerPath.Split('/').Select(Uri.EscapeDataString));
        using JsonDocument document = await ReadInventoryDocumentAsync(
            baseUrl, "api/v1/files/" + escapedPath, credential, scan).ConfigureAwait(false);
        JsonElement folder = document.RootElement;
        PrinterFileInventorySession.RequireUntruncated(folder);
        if (folder.GetProperty("type").GetString() != "FOLDER")
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.MalformedResponse);
        }

        foreach (JsonElement child in folder.GetProperty("children").EnumerateArray())
        {
            scan.VisitEntry(depth);
            PrinterFileInventorySession.RequireUntruncated(child);
            string name = child.GetProperty("name").GetString() ?? string.Empty;
            string path = parent.Length == 0 ? name : parent + "/" + name;
            if (name.Contains('/') || !PrinterInventoryPath.TryCanonicalizeRelativePath(path, out _))
            {
                throw new PrinterFileInventoryException(PrinterFileInventoryReason.InvalidPath);
            }

            switch (child.GetProperty("type").GetString())
            {
                case "FOLDER":
                    await ReadInventoryFolderAsync(baseUrl, root, path, depth + 1, credential, scan)
                        .ConfigureAwait(false);
                    break;
                case "PRINT_FILE":
                    PrinterFileInventoryEntry entry = new(root, path, PrinterFileInventorySession.Size(child),
                        PrinterFileInventorySession.Modified(child, "m_timestamp"),
                        ModifiedValue: PrinterFileInventorySession.ModifiedValue(child, "m_timestamp"),
                        DisplayName: ReadFilePreviewString(child, "display_name"));
                    scan.Add(ReuseInventoryDetails(entry, scan.Options));
                    break;
                case "FILE":
                case "FIRMWARE":
                    break;
                default:
                    throw new PrinterFileInventoryException(PrinterFileInventoryReason.IncompleteResponse);
            }
        }
    }

    private static PrinterFileInventoryEntry ReuseInventoryDetails(
        PrinterFileInventoryEntry entry, PrinterFileInventoryOptions options)
    {
        if (options.IncludeFileDetails && entry.ModifiedValue is not null &&
            options.CachedEntries.TryGetValue(
                PrinterInventoryPath.ComputeIdentity(entry.StorageRoot, entry.RelativePath), out var cached) &&
            cached.StorageRoot == entry.StorageRoot && cached.RelativePath == entry.RelativePath &&
            cached.DetailsFetchedAtUtc.HasValue && cached.ModifiedValue == entry.ModifiedValue &&
            (entry.SizeBytes is null || entry.SizeBytes == cached.SizeBytes))
        {
            return entry with
            {
                SizeBytes = entry.SizeBytes ?? cached.SizeBytes,
                DisplayName = entry.DisplayName ?? cached.DisplayName,
                Metadata = cached.Metadata,
                DetailsFetchedAtUtc = cached.DetailsFetchedAtUtc,
            };
        }

        return entry;
    }

    private async Task<PrinterFileInventoryEntry> ReadInventoryDetailsAsync(
        string baseUrl, PrinterFileInventoryEntry entry, PrinterCredential? credential,
        PrinterFileInventorySession scan)
    {
        string path = string.Join('/', (entry.StorageRoot + "/" + entry.RelativePath)
            .Split('/').Select(Uri.EscapeDataString));
        using JsonDocument document = await ReadInventoryDocumentAsync(
            baseUrl, "api/v1/files/" + path, credential, scan).ConfigureAwait(false);
        JsonElement details = document.RootElement;
        PrinterFileInventorySession.RequireUntruncated(details);
        long? size = PrinterFileInventorySession.Size(details);
        string? modified = PrinterFileInventorySession.ModifiedValue(details, "m_timestamp");
        if (ReadFilePreviewString(details, "type") != "PRINT_FILE" ||
            ReadFilePreviewString(details, "name") != entry.RelativePath[(entry.RelativePath.LastIndexOf('/') + 1)..] ||
            (entry.SizeBytes.HasValue && entry.SizeBytes != size) ||
            (entry.ModifiedValue is not null && entry.ModifiedValue != modified))
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.IncompleteResponse);
        }

        return entry with
        {
            SizeBytes = size,
            ModifiedAt = PrinterFileInventorySession.Modified(details, "m_timestamp"),
            ModifiedValue = modified,
            DisplayName = ReadFilePreviewString(details, "display_name") ?? entry.DisplayName,
            Metadata = ReadPrintFileMetadata(details.TryGetProperty("meta", out JsonElement meta) ? meta : default),
            DetailsFetchedAtUtc = DateTime.UtcNow,
        };
    }

    private Task<JsonDocument> ReadInventoryDocumentAsync(
        string baseUrl, string path, PrinterCredential? credential, PrinterFileInventorySession scan) =>
        scan.ReadAsync(
            async token =>
            {
                BackendHttpTransport transport = GetClientForCredentials(credential);
                using HttpRequestMessage request = CreateRequest(
                    HttpMethod.Get,
                    new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path).ToString(), credential);
                return await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
            });
}
