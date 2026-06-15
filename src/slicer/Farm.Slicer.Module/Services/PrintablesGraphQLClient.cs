using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// HTTP wrapper around Printables.com public GraphQL operations used by PrintFarmer.
/// </summary>
public sealed class PrintablesGraphQLClient : IPrintablesGraphQLClient
{
    private const string DownloadSource = "model_detail";
    private const string StlDownloadFileType = "stl";
    private const string DefaultUserAgent = "PrintFarmer/1.0";
    private const string DefaultOrigin = "https://www.printables.com";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PrintablesGraphQLClient> _logger;
    private readonly PrintablesGraphQlOptions _options;

    public PrintablesGraphQLClient(
        HttpClient http,
        IMemoryCache cache,
        IOptions<PrintablesGraphQlOptions> options,
        ILogger<PrintablesGraphQLClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (_http.Timeout == Timeout.InfiniteTimeSpan && _options.TimeoutSeconds > 0)
        {
            _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        }

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        }

        if (!_http.DefaultRequestHeaders.Contains("Origin"))
        {
            _http.DefaultRequestHeaders.Add("Origin", DefaultOrigin);
        }
    }

    public Task<IReadOnlyList<PrintablesCollectionDto>> GetUserCollectionsAsync(string userId, CancellationToken ct)
    {
        string normalizedUser = NormalizeRequired(userId, nameof(userId));
        string cacheKey = $"printables:collections:{normalizedUser}";

        return GetOrCreateCachedAsync<IReadOnlyList<PrintablesCollectionDto>>(
            cacheKey,
            async token =>
            {
                using JsonDocument doc = await SendGraphQlWithRetryAsync(
                    operationName: "UserCollections",
                    payload: new
                    {
                        operationName = "UserCollections",
                        query = """
                            query UserCollections($userId: String!) {
                              user(username: $userId) {
                                collections {
                                  edges {
                                    node {
                                      id
                                      name
                                      slug
                                      description
                                      printsCount
                                      image {
                                        filePath
                                      }
                                    }
                                  }
                                }
                              }
                            }
                            """,
                        variables = new { userId = normalizedUser },
                    },
                    token);

                JsonElement userElement = GetRequiredByPaths(
                    doc.RootElement,
                    "UserCollections",
                    ["data", "user"],
                    ["data", "profile"]);

                JsonElement collectionsNode = GetRequiredByPaths(
                    userElement,
                    "UserCollections",
                    ["collections"],
                    ["publicCollections"]);

                return [.. EnumerateConnectionNodes(collectionsNode).Select(MapCollection)];
            },
            ct);
    }

    public Task<PrintablesPagedResultDto<PrintablesModelCardDto>> GetUserModelsAsync(
        string userId,
        int limit,
        string? cursor,
        string? ordering,
        CancellationToken ct)
    {
        string normalizedUser = NormalizeRequired(userId, nameof(userId));
        int normalizedLimit = NormalizeLimit(limit);
        string normalizedOrdering = string.IsNullOrWhiteSpace(ordering) ? "LATEST" : ordering.Trim();
        string cacheKey = $"printables:user-models:{normalizedUser}:{normalizedLimit}:{cursor}:{normalizedOrdering}";

        return GetOrCreateCachedAsync(
            cacheKey,
            async token =>
            {
                using JsonDocument doc = await SendGraphQlWithRetryAsync(
                    operationName: "UserModels",
                    payload: new
                    {
                        operationName = "UserModels",
                        query = """
                            query UserModels($userId: String!, $first: Int!, $after: String, $ordering: String) {
                              user(username: $userId) {
                                prints(first: $first, after: $after, ordering: $ordering) {
                                  edges {
                                    cursor
                                    node {
                                      id
                                      name
                                      slug
                                      summary
                                      likesCount
                                      downloadsCount
                                      image {
                                        filePath
                                      }
                                      user {
                                        handle
                                      }
                                    }
                                  }
                                  pageInfo {
                                    hasNextPage
                                    endCursor
                                  }
                                }
                              }
                            }
                            """,
                        variables = new
                        {
                            userId = normalizedUser,
                            first = normalizedLimit,
                            after = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim(),
                            ordering = normalizedOrdering,
                        },
                    },
                    token);

                JsonElement userElement = GetRequiredByPaths(
                    doc.RootElement,
                    "UserModels",
                    ["data", "user"],
                    ["data", "profile"]);

                JsonElement modelsConnection = GetRequiredByPaths(
                    userElement,
                    "UserModels",
                    ["prints"],
                    ["models"]);

                return MapPagedModelCards(modelsConnection, "UserModels");
            },
            ct);
    }

    public Task<PrintablesPagedResultDto<PrintablesModelCardDto>> SearchModelsAsync(
        string query,
        int limit,
        string? cursor,
        CancellationToken ct)
    {
        string normalizedQuery = NormalizeRequired(query, nameof(query));
        int normalizedLimit = NormalizeLimit(limit);
        string cacheKey = $"printables:search:{normalizedQuery}:{normalizedLimit}:{cursor}";

        return GetOrCreateCachedAsync(
            cacheKey,
            async token =>
            {
                using JsonDocument doc = await SendGraphQlWithRetryAsync(
                    operationName: "SearchModels",
                    payload: new
                    {
                        operationName = "SearchModels",
                        query = """
                            query SearchModels($query: String!, $first: Int!, $after: String) {
                              search(query: $query, first: $first, after: $after) {
                                prints {
                                  edges {
                                    cursor
                                    node {
                                      id
                                      name
                                      slug
                                      summary
                                      likesCount
                                      downloadsCount
                                      image {
                                        filePath
                                      }
                                      user {
                                        handle
                                      }
                                    }
                                  }
                                  pageInfo {
                                    hasNextPage
                                    endCursor
                                  }
                                }
                              }
                            }
                            """,
                        variables = new
                        {
                            query = normalizedQuery,
                            first = normalizedLimit,
                            after = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim(),
                        },
                    },
                    token);

                JsonElement searchElement = GetRequiredByPaths(
                    doc.RootElement,
                    "SearchModels",
                    ["data", "search"]);

                JsonElement modelsConnection = GetRequiredByPaths(
                    searchElement,
                    "SearchModels",
                    ["prints"],
                    ["models"]);

                return MapPagedModelCards(modelsConnection, "SearchModels");
            },
            ct);
    }

    public Task<PrintablesPrintProfileDto> GetPrintProfileAsync(string printId, CancellationToken ct)
    {
        string normalizedPrintId = NormalizeRequired(printId, nameof(printId));
        string cacheKey = $"printables:print-profile:{normalizedPrintId}";

        return GetOrCreateCachedAsync(
            cacheKey,
            async token =>
            {
                using JsonDocument doc = await SendGraphQlWithRetryAsync(
                    operationName: "PrintProfile",
                    payload: new
                    {
                        operationName = "PrintProfile",
                        query = """
                            query PrintProfile($id: ID!) {
                              print(id: $id) {
                                id
                                name
                                slug
                                summary
                                description
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
                        variables = new { id = normalizedPrintId },
                    },
                    token);

                JsonElement printElement = GetRequiredByPaths(
                    doc.RootElement,
                    "PrintProfile",
                    ["data", "print"]);

                return MapPrintProfile(printElement);
            },
            ct);
    }

    public async Task<PrintablesPreviewDto> FetchPreviewAsync(string modelId, string sourceUrl, CancellationToken ct)
    {
        PrintablesPrintProfileDto profile = await GetPrintProfileAsync(modelId, ct);
        return new PrintablesPreviewDto(
            ModelId: profile.Id,
            Name: profile.Name,
            Creator: profile.Creator,
            License: profile.License,
            ThumbnailUrl: profile.ThumbnailUrl,
            SourceUrl: sourceUrl,
            Files: profile.Files);
    }

    public async Task<string> GetStlDownloadUrlAsync(string modelId, string fileId, CancellationToken ct)
    {
        string normalizedModelId = NormalizeRequired(modelId, nameof(modelId));
        string normalizedFileId = NormalizeRequired(fileId, nameof(fileId));

        using JsonDocument doc = await SendGraphQlWithRetryAsync(
            operationName: "GetDownloadLink",
            payload: new
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
                    id = normalizedFileId,
                    modelId = normalizedModelId,
                    fileType = StlDownloadFileType,
                    source = DownloadSource,
                },
            },
            ct);

        JsonElement downloadEl = GetRequiredByPaths(
            doc.RootElement,
            "GetDownloadLink",
            ["data", "getDownloadLink"]);

        bool isOk = downloadEl.TryGetProperty("ok", out JsonElement okEl) &&
                    okEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    okEl.GetBoolean();

        if (!isOk)
        {
            string? message = TryGetDownloadErrorMessage(downloadEl);
            throw new PrintablesApiException(message ?? $"Printables rejected download-link resolution for file '{normalizedFileId}'.");
        }

        if (!TryGetString(downloadEl, out string? link, "output", "link"))
        {
            throw new PrintablesApiException($"Printables did not return a usable download link for file '{normalizedFileId}'.");
        }

        return link!;
    }

    public async Task<byte[]> DownloadFileAsync(string downloadUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new ArgumentException("Download URL must not be empty.", nameof(downloadUrl));
        }

        try
        {
            return await _http.GetByteArrayAsync(downloadUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PrintablesApiException($"Failed to download Printables file from '{downloadUrl}'.", ex);
        }
    }

    private async Task<JsonDocument> SendGraphQlWithRetryAsync(string operationName, object payload, CancellationToken ct)
    {
        int maxAttempts = Math.Max(1, _options.MaxAttempts);
        int baseDelayMs = Math.Max(50, _options.RetryBaseDelayMs);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await SendGraphQlOnceAsync(operationName, payload, ct);
            }
            catch (PrintablesApiException ex) when (attempt < maxAttempts && ex.IsTransient)
            {
                int delayMs = baseDelayMs * (1 << (attempt - 1));
                _logger.LogWarning(
                    ex,
                    "Transient Printables error during {Operation} (attempt {Attempt}/{MaxAttempts}); retrying in {DelayMs}ms",
                    operationName,
                    attempt,
                    maxAttempts,
                    delayMs);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
            }
        }

        throw new PrintablesApiException($"Printables operation '{operationName}' failed after {maxAttempts} attempt(s).");
    }

    private async Task<JsonDocument> SendGraphQlOnceAsync(string operationName, object payload, CancellationToken ct)
    {
        HttpResponseMessage response;
        string endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? "https://api.printables.com/graphql/" : _options.Endpoint.Trim();

        try
        {
            response = await _http.PostAsJsonAsync(endpoint, payload, _jsonOptions, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PrintablesApiException($"Printables '{operationName}' timed out.", ex, isTransient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new PrintablesApiException($"Failed to reach Printables for '{operationName}'.", ex, isTransient: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            bool transient = IsTransientStatusCode(response.StatusCode);
            throw new PrintablesApiException(
                $"Printables API returned HTTP {(int)response.StatusCode} for '{operationName}'.",
                isTransient: transient);
        }

        string raw;
        try
        {
            raw = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            throw new PrintablesApiException($"Failed to read Printables response for '{operationName}'.", ex, isTransient: true);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new PrintablesApiException($"Invalid JSON payload from Printables for '{operationName}'.", ex);
        }

        if (doc.RootElement.TryGetProperty("errors", out JsonElement errorsEl) &&
            errorsEl.ValueKind == JsonValueKind.Array &&
            errorsEl.GetArrayLength() > 0)
        {
            string? message = errorsEl[0].TryGetProperty("message", out JsonElement msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : null;

            doc.Dispose();
            throw new PrintablesApiException(
                $"Printables GraphQL error during '{operationName}': {message ?? "Unknown error"}");
        }

        return doc;
    }

    private Task<T> GetOrCreateCachedAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct)
    {
        if (_options.CacheTtlSeconds <= 0)
        {
            return factory(ct);
        }

        return _cache.GetOrCreateAsync(
            key,
            async cacheEntry =>
            {
                cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_options.CacheTtlSeconds);
                return await factory(ct);
            })!;
    }

    private static PrintablesPagedResultDto<PrintablesModelCardDto> MapPagedModelCards(JsonElement connectionElement, string operationName)
    {
        List<PrintablesModelCardDto> items = [.. EnumerateConnectionNodes(connectionElement).Select(MapModelCard)];

        bool hasNextPage = false;
        string? endCursor = null;

        if (TryGetElement(connectionElement, out JsonElement pageInfo, "pageInfo"))
        {
            hasNextPage = pageInfo.TryGetProperty("hasNextPage", out JsonElement hasNextEl) &&
                          hasNextEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                          hasNextEl.GetBoolean();

            if (pageInfo.TryGetProperty("endCursor", out JsonElement cursorEl) && cursorEl.ValueKind == JsonValueKind.String)
            {
                endCursor = cursorEl.GetString();
            }
        }

        if (hasNextPage && string.IsNullOrWhiteSpace(endCursor))
        {
            throw new PrintablesApiException(
                $"Printables '{operationName}' returned hasNextPage=true without endCursor. Schema may have changed.");
        }

        return new PrintablesPagedResultDto<PrintablesModelCardDto>(items, endCursor, hasNextPage);
    }

    private static IEnumerable<JsonElement> EnumerateConnectionNodes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (TryGetElement(element, out JsonElement edges, "edges") && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement edge in edges.EnumerateArray())
            {
                if (TryGetElement(edge, out JsonElement node, "node"))
                {
                    yield return node;
                }
                else
                {
                    yield return edge;
                }
            }

            yield break;
        }

        if (TryGetElement(element, out JsonElement nodes, "nodes") && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                yield return node;
            }

            yield break;
        }

        if (TryGetElement(element, out JsonElement items, "items") && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        throw new PrintablesApiException("Printables GraphQL response had no collection edges/nodes/items. Schema may have changed.");
    }

    private static PrintablesCollectionDto MapCollection(JsonElement collectionElement)
    {
        return new PrintablesCollectionDto(
            Id: ReadRequiredString(collectionElement, "collection id", "id"),
            Name: ReadRequiredString(collectionElement, "collection name", "name"),
            Slug: ReadOptionalString(collectionElement, "slug"),
            Description: ReadOptionalString(collectionElement, "description", "summary"),
            ModelCount: ReadOptionalInt(collectionElement, "printsCount", "modelCount"),
            ThumbnailUrl: ReadOptionalString(collectionElement, "image", "filePath"));
    }

    private static PrintablesModelCardDto MapModelCard(JsonElement modelElement)
    {
        return new PrintablesModelCardDto(
            Id: ReadRequiredString(modelElement, "model id", "id"),
            Name: ReadRequiredString(modelElement, "model name", "name"),
            Slug: ReadOptionalString(modelElement, "slug"),
            Description: ReadOptionalString(modelElement, "summary", "description"),
            Creator: ReadOptionalString(modelElement, "user", "handle"),
            ThumbnailUrl: ReadOptionalString(modelElement, "image", "filePath"),
            LikeCount: ReadOptionalInt(modelElement, "likesCount", "likeCount"),
            DownloadCount: ReadOptionalInt(modelElement, "downloadsCount", "downloadCount"));
    }

    private static PrintablesPrintProfileDto MapPrintProfile(JsonElement printElement)
    {
        List<PrintablesFileEntryDto> files = [];

        if (TryGetElement(printElement, out JsonElement stlsElement, "stls") && stlsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement fileElement in stlsElement.EnumerateArray())
            {
                files.Add(new PrintablesFileEntryDto(
                    Id: ReadRequiredString(fileElement, "file id", "id"),
                    Name: ReadRequiredString(fileElement, "file name", "name"),
                    FileSize: ReadOptionalLong(fileElement, "fileSize")));
            }
        }

        return new PrintablesPrintProfileDto(
            Id: ReadRequiredString(printElement, "print id", "id"),
            Name: ReadRequiredString(printElement, "print name", "name"),
            Slug: ReadOptionalString(printElement, "slug"),
            Description: ReadOptionalString(printElement, "description", "summary"),
            Creator: ReadOptionalString(printElement, "user", "handle") ?? string.Empty,
            License: ReadOptionalString(printElement, "license", "name"),
            ThumbnailUrl: ReadOptionalString(printElement, "image", "filePath"),
            Files: files);
    }

    private static JsonElement GetRequiredByPaths(JsonElement root, string operationName, params string[][] paths)
    {
        foreach (string[] path in paths)
        {
            if (TryGetElement(root, out JsonElement value, path) && value.ValueKind != JsonValueKind.Null)
            {
                return value;
            }
        }

        throw new PrintablesApiException(
            $"Printables response for '{operationName}' was missing required path(s): {string.Join(" | ", paths.Select(p => string.Join(".", p)))}. Schema may have changed.");
    }

    private static bool TryGetElement(JsonElement source, out JsonElement value, params string[] path)
    {
        JsonElement current = source;
        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                value = default;
                return false;
            }
        }

        value = current;
        return true;
    }

    private static bool TryGetString(JsonElement source, out string? value, params string[] path)
    {
        value = null;
        if (!TryGetElement(source, out JsonElement element, path) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string ReadRequiredString(JsonElement element, string fieldDescription, params string[] path)
    {
        if (!TryGetString(element, out string? value, path))
        {
            throw new PrintablesApiException($"Printables response is missing required {fieldDescription}.");
        }

        return value!;
    }

    private static string? ReadOptionalString(JsonElement element, params string[] path)
    {
        return TryGetString(element, out string? value, path) ? value : null;
    }

    private static int ReadOptionalInt(JsonElement element, params string[] path)
    {
        if (!TryGetElement(element, out JsonElement target, path))
        {
            return 0;
        }

        return target.ValueKind switch
        {
            JsonValueKind.Number when target.TryGetInt32(out int value) => value,
            JsonValueKind.Number when target.TryGetInt64(out long value) && value <= int.MaxValue => (int)value,
            _ => 0,
        };
    }

    private static long ReadOptionalLong(JsonElement element, params string[] path)
    {
        if (!TryGetElement(element, out JsonElement target, path))
        {
            return 0;
        }

        return target.ValueKind == JsonValueKind.Number && target.TryGetInt64(out long value) ? value : 0L;
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code == 408 || code == 429 || code >= 500;
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", paramName);
        }

        return value.Trim();
    }

    private static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        return Math.Min(limit, 100);
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
}
