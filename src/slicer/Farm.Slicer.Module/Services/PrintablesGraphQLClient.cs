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
        var payload = new
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
        };

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

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;

        // Surface GraphQL-level errors first.
        if (root.TryGetProperty("errors", out JsonElement errorsEl) && errorsEl.ValueKind == JsonValueKind.Array)
        {
            string firstError = errorsEl[0].TryGetProperty("message", out JsonElement msg)
                ? msg.GetString() ?? "Unknown error"
                : "Unknown GraphQL error";
            throw new PrintablesApiException($"Printables GraphQL error: {firstError}");
        }

        if (!root.TryGetProperty("data", out JsonElement dataEl) ||
            !dataEl.TryGetProperty("print", out JsonElement printEl) ||
            printEl.ValueKind == JsonValueKind.Null)
        {
            throw new PrintablesApiException("Model not found on Printables (it may be private or deleted).");
        }

        return MapToPreviewDto(printEl, sourceUrl);
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
