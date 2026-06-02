using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Thin HTTP wrapper around the Printables.com public GraphQL API.
/// No authentication is required for public model queries.
/// </summary>
/// <remarks>
/// Uses raw <see cref="HttpClient"/> with a typed query string to avoid the StrawberryShake
/// code-generation overhead for a single two-field query.
/// Named client "Printables" — must be registered via AddHttpClient in DI.
/// </remarks>
public sealed class PrintablesGraphQLClient
{
    private const string GraphQlEndpoint = "https://api.printables.com/graphql/";
    private const string DownloadSource = "model_detail";
    private const string StlDownloadFileType = "stl";

    // JSON options reused across requests (camelCase, ignore null).
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>Initialises the client with an externally managed <see cref="HttpClient"/>.</summary>
    public PrintablesGraphQLClient(HttpClient http) => _http = http;

    /// <summary>
    /// Queries the Printables GraphQL API for model metadata by numeric model ID.
    /// </summary>
    /// <param name="modelId">Numeric Printables model ID.</param>
    /// <param name="sourceUrl">Original Printables URL (passed through to the DTO).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Raw GraphQL payload mapped to a preview DTO.</returns>
    /// <exception cref="PrintablesApiException">
    /// Thrown when the API returns GraphQL errors, the model node is null (private/deleted),
    /// or the HTTP call fails.
    /// </exception>
    public async Task<PrintablesPreviewDto> FetchPreviewAsync(string modelId, string sourceUrl, CancellationToken ct)
    {
        using JsonDocument doc = await SendGraphQlAsync(
            new
            {
                query = """
                    query PrintProfile($id: ID!) {
                      print(id: $id) {
                        id
                        name
                        user {
                          handle
                        }
                        license {
                          name
                        }
                        image {
                          filePath
                        }
                        stls {
                          id
                          name
                          fileSize
                        }
                      }
                    }
                    """,
                variables = new { id = modelId },
            },
            ct);

        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("data", out JsonElement dataEl) ||
            !dataEl.TryGetProperty("print", out JsonElement printEl) ||
            printEl.ValueKind == JsonValueKind.Null)
        {
            throw new PrintablesApiException("Model not found on Printables (it may be private or deleted).");
        }

        return MapToPreviewDto(printEl, sourceUrl);
    }

    /// <summary>
    /// Resolves a temporary direct download URL for a selected STL file.
    /// </summary>
    /// <param name="modelId">Numeric Printables model ID.</param>
    /// <param name="fileId">Selected Printables file ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The temporary CDN download URL.</returns>
    public async Task<string> GetStlDownloadUrlAsync(string modelId, string fileId, CancellationToken ct)
    {
        using JsonDocument doc = await SendGraphQlAsync(
            new
            {
                operationName = "GetDownloadLink",
                query = """
                    mutation GetDownloadLink($id: ID!, $modelId: ID!, $fileType: DownloadFileTypeEnum!, $source: DownloadSourceEnum!) {
                      getDownloadLink(id: $id, printId: $modelId, fileType: $fileType, source: $source) {
                        ok
                        errors {
                          field
                          messages
                        }
                        output {
                          link
                        }
                      }
                    }
                    """,
                variables = new
                {
                    id = fileId,
                    modelId,
                    fileType = StlDownloadFileType,
                    source = DownloadSource,
                },
            },
            ct);

        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("data", out JsonElement dataEl) ||
            !dataEl.TryGetProperty("getDownloadLink", out JsonElement downloadEl) ||
            downloadEl.ValueKind == JsonValueKind.Null)
        {
            throw new PrintablesApiException($"Printables did not return a download link for file '{fileId}'.");
        }

        bool isOk = downloadEl.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False && okEl.GetBoolean();
        if (!isOk)
        {
            string? message = TryGetDownloadErrorMessage(downloadEl);
            throw new PrintablesApiException(message ?? $"Printables rejected download-link resolution for file '{fileId}'.");
        }

        if (!downloadEl.TryGetProperty("output", out JsonElement outputEl) ||
            outputEl.ValueKind == JsonValueKind.Null ||
            !outputEl.TryGetProperty("link", out JsonElement linkEl) ||
            linkEl.ValueKind != JsonValueKind.String)
        {
            throw new PrintablesApiException($"Printables did not return a usable download link for file '{fileId}'.");
        }

        return linkEl.GetString()!;
    }

    /// <summary>
    /// Downloads the selected Printables file from the temporary CDN URL.
    /// </summary>
    /// <param name="downloadUrl">Resolved temporary CDN URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Raw file bytes.</returns>
    public async Task<byte[]> DownloadFileAsync(string downloadUrl, CancellationToken ct)
    {
        try
        {
            return await _http.GetByteArrayAsync(downloadUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PrintablesApiException($"Failed to download Printables file from '{downloadUrl}'.", ex);
        }
    }

    private async Task<JsonDocument> SendGraphQlAsync(object payload, CancellationToken ct)
    {
        HttpResponseMessage response;

        try
        {
            response = await _http.PostAsJsonAsync(GraphQlEndpoint, payload, _jsonOptions, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PrintablesApiException("Failed to reach the Printables API.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PrintablesApiException($"Printables API returned HTTP {(int)response.StatusCode}.");
        }

        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("errors", out JsonElement errorsEl) && errorsEl.ValueKind == JsonValueKind.Array)
        {
            string firstError = errorsEl[0].TryGetProperty("message", out JsonElement msg)
                ? msg.GetString() ?? "Unknown error"
                : "Unknown GraphQL error";
            doc.Dispose();
            throw new PrintablesApiException($"Printables GraphQL error: {firstError}");
        }

        return doc;
    }

    private static string? TryGetDownloadErrorMessage(JsonElement downloadEl)
    {
        if (!downloadEl.TryGetProperty("errors", out JsonElement errorsEl) || errorsEl.ValueKind != JsonValueKind.Array || errorsEl.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement firstError = errorsEl[0];
        if (firstError.TryGetProperty("messages", out JsonElement messagesEl) && messagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement messageEl in messagesEl.EnumerateArray())
            {
                if (messageEl.ValueKind == JsonValueKind.String)
                {
                    return messageEl.GetString();
                }
            }
        }

        return firstError.TryGetProperty("field", out JsonElement fieldEl) && fieldEl.ValueKind == JsonValueKind.String
            ? $"Failed to resolve Printables download link for field '{fieldEl.GetString()}'."
            : null;
    }

    /// <summary>Maps the raw <c>print</c> node from the GraphQL response to a <see cref="PrintablesPreviewDto"/>.</summary>
    private static PrintablesPreviewDto MapToPreviewDto(JsonElement print, string sourceUrl)
    {
        string id = GetString(print, "id") ?? string.Empty;
        string name = GetString(print, "name") ?? "(untitled)";

        string? creator = null;
        if (print.TryGetProperty("user", out JsonElement userEl) && userEl.ValueKind != JsonValueKind.Null)
        {
            creator = GetString(userEl, "handle");
        }

        string? license = null;
        if (print.TryGetProperty("license", out JsonElement licEl) && licEl.ValueKind != JsonValueKind.Null)
        {
            license = GetString(licEl, "name");
        }

        string? thumbnailUrl = null;
        if (print.TryGetProperty("image", out JsonElement imgEl) && imgEl.ValueKind != JsonValueKind.Null)
        {
            string? filePath = GetString(imgEl, "filePath");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                // filePath is already an absolute CDN URL on Printables.
                thumbnailUrl = filePath;
            }
        }

        List<PrintablesFileEntryDto> files = [];
        if (print.TryGetProperty("stls", out JsonElement stlsEl) && stlsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement stl in stlsEl.EnumerateArray())
            {
                string fileId = GetString(stl, "id") ?? string.Empty;
                string fileName = GetString(stl, "name") ?? string.Empty;
                long fileSize = stl.TryGetProperty("fileSize", out JsonElement sizeEl) && sizeEl.TryGetInt64(out long sz)
                    ? sz
                    : 0L;
                files.Add(new PrintablesFileEntryDto(fileId, fileName, fileSize));
            }
        }

        return new PrintablesPreviewDto(
            ModelId: id,
            Name: name,
            Creator: creator ?? string.Empty,
            License: license,
            ThumbnailUrl: thumbnailUrl,
            SourceUrl: sourceUrl,
            Files: files);
    }

    private static string? GetString(JsonElement el, string property) =>
        el.TryGetProperty(property, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
