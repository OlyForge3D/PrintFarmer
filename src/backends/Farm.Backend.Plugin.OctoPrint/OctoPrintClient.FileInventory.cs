using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.OctoPrint;

public partial class OctoPrintClient : ISupportsFileInventory
{
    private readonly TimeProvider _inventoryTimeProvider = TimeProvider.System;

    internal OctoPrintClient(HttpClient httpClient, TimeProvider inventoryTimeProvider)
        : this(httpClient)
    {
        _inventoryTimeProvider = inventoryTimeProvider;
    }

    /// <summary>Reads authenticated native local storage recursively, retaining provider paths.</summary>
    public async Task<PrinterFileInventoryResult> GetFileInventoryAsync(
        string baseUrl, PrinterCredential? credential = null,
        PrinterFileInventoryOptions? options = null, CancellationToken ct = default)
    {
        using PrinterFileInventorySession scan = new(options, _inventoryTimeProvider, ct);
        try
        {
            using JsonDocument document = await scan.ReadAsync(async token =>
            {
                using HttpRequestMessage request = new(
                    HttpMethod.Get,
                    new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "api/files/local?recursive=true"));
                if (credential?.HasApiKey == true)
                {
                    request.Headers.Add("X-Api-Key", credential.ApiKey);
                }

                return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            PrinterFileInventorySession.RequireUntruncated(root);
            ReadInventoryChildren(root.GetProperty("files"), string.Empty, 0, scan);
            scan.Cover("local", true, PrinterFileInventoryEvidence.NativeRecursiveListing);
            return scan.Complete();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
            OperationCanceledException or JsonException or InvalidOperationException or
            KeyNotFoundException or PrinterFileInventoryException)
        {
            return scan.Failed(exception);
        }
    }

    private static void ReadInventoryChildren(
        JsonElement children, string parent, int depth, PrinterFileInventorySession scan)
    {
        if (depth > scan.Options.MaxDepth)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.DepthLimit);
        }

        foreach (JsonElement child in children.EnumerateArray())
        {
            scan.VisitEntry(depth);
            PrinterFileInventorySession.RequireUntruncated(child);
            string name = child.GetProperty("name").GetString() ?? string.Empty;
            string path = parent.Length == 0 ? name : parent + "/" + name;
            if (name.Contains('/') || !PrinterInventoryPath.TryCanonicalizeRelativePath(path, out _) ||
                (child.TryGetProperty("path", out JsonElement providerPath) && providerPath.GetString() != path))
            {
                throw new PrinterFileInventoryException(PrinterFileInventoryReason.InvalidPath);
            }

            string? type = child.GetProperty("type").GetString();
            if (type == "folder")
            {
                ReadInventoryChildren(child.GetProperty("children"), path, depth + 1, scan);
            }
            else if (type == "machinecode")
            {
                string? providerHash = child.TryGetProperty("hash", out JsonElement hash) &&
                    hash.ValueKind == JsonValueKind.String && hash.GetString() is { Length: > 0 and <= 256 } digest
                        ? digest : null;
                scan.Add(new("local", path, PrinterFileInventorySession.Size(child),
                    PrinterFileInventorySession.Modified(child, "date"),
                    ProviderHash: providerHash,
                    ModifiedValue: PrinterFileInventorySession.ModifiedValue(child, "date")));
            }
            else if (type != "model")
            {
                throw new PrinterFileInventoryException(PrinterFileInventoryReason.IncompleteResponse);
            }
        }
    }
}
