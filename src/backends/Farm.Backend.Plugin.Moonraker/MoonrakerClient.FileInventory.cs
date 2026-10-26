using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient : ISupportsFileInventory
{
    /// <summary>Reads the native recursive gcodes listing without legacy thumbnail/metadata fanout.</summary>
    public async Task<PrinterFileInventoryResult> GetFileInventoryAsync(
        string baseUrl, PrinterCredential? credential = null,
        PrinterFileInventoryOptions? options = null, CancellationToken ct = default)
    {
        using PrinterFileInventorySession scan = new(options, ct);
        try
        {
            using JsonDocument document = await scan.ReadAsync(async token =>
            {
                using HttpRequestMessage request = new(
                    HttpMethod.Get,
                    new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "server/files/list?root=gcodes"));
                if (credential?.HasApiKey == true)
                {
                    request.Headers.Add("X-Api-Key", credential.ApiKey);
                }

                return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            PrinterFileInventorySession.RequireUntruncated(root);
            if (!root.TryGetProperty("result", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
            {
                throw new PrinterFileInventoryException(PrinterFileInventoryReason.MalformedResponse);
            }

            foreach (JsonElement file in files.EnumerateArray())
            {
                string? path = file.GetProperty("path").GetString();
                if (!PrinterInventoryPath.TryCanonicalizeRelativePath(path, out string canonical))
                {
                    throw new PrinterFileInventoryException(PrinterFileInventoryReason.InvalidPath);
                }

                scan.VisitEntry(canonical.Count(c => c == '/'));
                scan.Add(new("gcodes", canonical, PrinterFileInventorySession.Size(file),
                    PrinterFileInventorySession.Modified(file, "modified"),
                    ModifiedValue: PrinterFileInventorySession.ModifiedValue(file, "modified")));
            }

            scan.Cover("gcodes", true, PrinterFileInventoryEvidence.NativeRecursiveListing);
            return scan.Complete();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
            OperationCanceledException or JsonException or InvalidOperationException or
            KeyNotFoundException or PrinterFileInventoryException)
        {
            return scan.Failed(exception);
        }
    }
}
