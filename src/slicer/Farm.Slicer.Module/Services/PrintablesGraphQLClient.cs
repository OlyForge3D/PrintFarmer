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
    private const string MediaBaseUrl = "https://media.printables.com/";

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

    public Task<PrintablesUserProfileDto> ResolveUserProfileAsync(string username, CancellationToken ct)
    {
        string normalizedUsername = NormalizeUsername(username, nameof(username));
        string cacheKey = $"printables:user-profile:{normalizedUsername}";

        return GetOrCreateCachedAsync(
            cacheKey,
            async token => await ResolveUserInternalAsync(normalizedUsername, token),
            ct);
    }

    public Task<IReadOnlyList<PrintablesCollectionDto>> GetUserCollectionsAsync(string username, CancellationToken ct)
    {
        string normalizedUsername = NormalizeUsername(username, nameof(username));
        string cacheKey = $"printables:collections:{normalizedUsername}";

        return GetOrCreateCachedAsync<IReadOnlyList<PrintablesCollectionDto>>(
            cacheKey,
            async token =>
            {
                PrintablesUserProfileDto user = await ResolveUserInternalAsync(normalizedUsername, token);
                using JsonDocument doc = await SendGraphQlWithRetryAsync(
                    operationName: "UserCollections",
                    payload: new
                    {
                        operationName = "UserCollections",
                        query = """
                            query UserCollections($userId: ID!) {
                              userCollections(userId: $userId) {
                                id
                                name
                                printsCount
                                likesCount
                                thumbnails {
                                  image {
                                    filePath
                                  }
                                }
                              }
                            }
                            """,
                        variables = new { userId = user.Id },
                    },
                    token);

                JsonElement collectionsElement = GetRequiredByPaths(
                    doc.RootElement,
                    "UserCollections",
                    ["data", "userCollections"]);

                return [.. EnumerateConnectionNodes(collectionsElement).Select(MapCollection)];
            },
            ct);
    }

    public Task<PrintablesPagedResultDto<PrintablesModelCardDto>> GetUserModelsAsync(
        string username,
        int limit,
        string? cursor,
        string? ordering,
        CancellationToken ct)
    {
        string normalizedUsername = NormalizeUsername(username, nameof(username));
        int normalizedLimit = NormalizeLimit(limit);
        string? normalizedCursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
        string? normalizedOrdering = NormalizeOptionalOrdering(ordering);
        string cacheKey = $"printables:user-models:{normalizedUsername}:{normalizedLimit}:{normalizedCursor}:{normalizedOrdering}";

        return GetOrCreateCachedAsync(
            cacheKey,
            async token =>
            {
                PrintablesUserProfileDto user = await ResolveUserInternalAsync(normalizedUsername, token);
                using JsonDocument doc = await SendGraphQlWithRetryAsync(
                    operationName: "UserModels",
                    payload: new
                    {
                        operationName = "UserModels",
                        query = """
                            query UserModels($userId: ID!, $limit: Int, $cursor: String, $ordering: UserModelsOrderingObject) {
                              userModels(userId: $userId, limit: $limit, cursor: $cursor, ordering2: $ordering) {
                                cursor
                                items {
                                  id
                                  name
                                  slug
                                  summary
                                  likesCount
                                  downloadCount
                                  image {
                                    filePath
                                  }
                                  user {
                                    handle
                                  }
                                }
                              }
                            }
                            """,
                        variables = new
                        {
                            userId = user.Id,
                            limit = normalizedLimit,
                            cursor = normalizedCursor,
                            ordering = normalizedOrdering is null
                                ? null
                                : new { orderBy = normalizedOrdering, sortOrder = "DESC" },
                        },
                    },
                    token);

                JsonElement modelsConnection = GetRequiredByPaths(
                    doc.RootElement,
                    "UserModels",
                    ["data", "userModels"]);

                return MapCursorPagedModelCards(modelsConnection);
            },
            ct);
    }

    public Task<PrintablesPagedResultDto<PrintablesModelCardDto>> GetCollectionModelsAsync(
        string collectionId,
        int limit,
        string? cursor,
        string? query,
        string? ordering,
        CancellationToken ct)
    {
        string normalizedCollectionId = NormalizeRequired(collectionId, nameof(collectionId));
        int normalizedLimit = NormalizeLimit(limit);
        string? normalizedCursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
        string? normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        string? normalizedOrdering = NormalizeOptionalOrdering(ordering);
        string cacheKey = $"printables:collection-models:{normalizedCollectionId}:{normalizedLimit}:{normalizedCursor}:{normalizedQuery}:{normalizedOrdering}";

        return GetOrCreateCachedAsync(
            cacheKey,
            async token =>
            {
                using JsonDocument doc = await SendGraphQlWithRetryAsync(
                    operationName: "CollectionModels",
                    payload: new
                    {
                        operationName = "CollectionModels",
                        query = """
                            query CollectionModels(
                              $collectionId: ID!,
                              $limit: Int,
                              $cursor: String,
                              $query: String,
                              $ordering: CollectionPrintsOrderingEnum
                            ) {
                              moreCollectionModels(
                                collectionId: $collectionId,
                                limit: $limit,
                                cursor: $cursor,
                                query: $query,
                                ordering: $ordering
                              ) {
                                cursor
                                items {
                                  print {
                                    id
                                    name
                                    slug
                                    summary
                                    likesCount
                                    downloadCount
                                    image {
                                      filePath
                                    }
                                    user {
                                      handle
                                    }
                                  }
                                }
                              }
                            }
                            """,
                        variables = new
                        {
                            collectionId = normalizedCollectionId,
                            limit = normalizedLimit,
                            cursor = normalizedCursor,
                            query = normalizedQuery,
                            ordering = normalizedOrdering,
                        },
                    },
                    token);

                JsonElement modelsConnection = GetRequiredByPaths(
                    doc.RootElement,
                    "CollectionModels",
                    ["data", "moreCollectionModels"]);

                return MapCursorPagedModelCards(
                    modelsConnection,
                    itemTransform: static item =>
                    {
                        if (!TryGetElement(item, out JsonElement printEl, "print") || printEl.ValueKind == JsonValueKind.Null)
                        {
                            return null;
                        }

                        return MapModelCard(printEl);
                    });
            },
            ct);
    }

    public Task<PrintablesSearchResultsDto> SearchModelsAsync(
        string query,
        int offset,
        int limit,
        string? ordering,
        CancellationToken ct)
    {
        string normalizedQuery = NormalizeRequired(query, nameof(query));
        int normalizedOffset = Math.Max(0, offset);
        int normalizedLimit = NormalizeLimit(limit);
        string? normalizedOrdering = NormalizeOptionalSearchOrdering(ordering);
        string cacheKey = $"printables:search:{normalizedQuery}:{normalizedOffset}:{normalizedLimit}:{normalizedOrdering}";

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
                            query SearchModels($query: String!, $offset: Int, $limit: Int, $ordering: SearchChoicesEnum) {
                              searchPrints2(query: $query, offset: $offset, limit: $limit, ordering: $ordering) {
                                totalCount
                                items {
                                  id
                                  name
                                  slug
                                  summary
                                  likesCount
                                  downloadCount
                                  image {
                                    filePath
                                  }
                                  user {
                                    handle
                                  }
                                }
                              }
                            }
                            """,
                        variables = new
                        {
                            query = normalizedQuery,
                            offset = normalizedOffset,
                            limit = normalizedLimit,
                            ordering = normalizedOrdering,
                        },
                    },
                    token);

                JsonElement searchElement = GetRequiredByPaths(
                    doc.RootElement,
                    "SearchModels",
                    ["data", "searchPrints2"]);

                return MapSearchResults(searchElement, normalizedOffset, normalizedLimit);
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

    private async Task<PrintablesUserProfileDto> ResolveUserInternalAsync(string normalizedUsername, CancellationToken ct)
    {
        using JsonDocument doc = await SendGraphQlWithRetryAsync(
            operationName: "ResolveUser",
            payload: new
            {
                operationName = "ResolveUser",
                query = """
                    query ResolveUser($query: String!) {
                      searchUsers2(query: $query, limit: 10) {
                        items {
                          id
                          handle
                          publicUsername
                          avatarFilePath
                          verified
                        }
                      }
                    }
                    """,
                variables = new { query = $"@{normalizedUsername}" },
            },
            ct);

        JsonElement usersElement = GetRequiredByPaths(
            doc.RootElement,
            "ResolveUser",
            ["data", "searchUsers2", "items"]);

        PrintablesUserProfileDto? resolved = SelectUserCandidate(usersElement, normalizedUsername);
        if (resolved is null)
        {
            using JsonDocument fallbackDoc = await SendGraphQlWithRetryAsync(
                operationName: "ResolveUserFallback",
                payload: new
                {
                    operationName = "ResolveUserFallback",
                    query = """
                        query ResolveUserFallback($query: String!) {
                          searchUsers2(query: $query, limit: 10) {
                            items {
                              id
                              handle
                              publicUsername
                              avatarFilePath
                            }
                          }
                        }
                        """,
                    variables = new { query = normalizedUsername },
                },
                ct);

            JsonElement fallbackUsersElement = GetRequiredByPaths(
                fallbackDoc.RootElement,
                "ResolveUserFallback",
                ["data", "searchUsers2", "items"]);
            resolved = SelectUserCandidate(fallbackUsersElement, normalizedUsername);
        }

        if (resolved is null)
        {
            throw new KeyNotFoundException($"Printables user '{normalizedUsername}' was not found.");
        }

        return resolved;
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

    private static PrintablesPagedResultDto<PrintablesModelCardDto> MapCursorPagedModelCards(
        JsonElement connectionElement,
        Func<JsonElement, PrintablesModelCardDto?>? itemTransform = null)
    {
        itemTransform ??= MapModelCard;
        List<PrintablesModelCardDto> items = [];
        foreach (JsonElement item in EnumerateConnectionNodes(connectionElement))
        {
            PrintablesModelCardDto? transformed = itemTransform(item);
            if (transformed is not null)
            {
                items.Add(transformed);
            }
        }

        string? endCursor = ReadOptionalString(connectionElement, "cursor");
        bool hasNextPage = !string.IsNullOrWhiteSpace(endCursor);
        return new PrintablesPagedResultDto<PrintablesModelCardDto>(items, endCursor, hasNextPage);
    }

    private static PrintablesSearchResultsDto MapSearchResults(JsonElement searchElement, int offset, int limit)
    {
        int totalCount = ReadOptionalInt(searchElement, "totalCount");
        List<PrintablesModelCardDto> items = [.. EnumerateConnectionNodes(searchElement).Select(MapModelCard)];
        bool hasMore = offset + items.Count < totalCount;

        List<PrintablesModelSummaryDto> summaries = [.. items.Select(item =>
            new PrintablesModelSummaryDto(
                Id: item.Id,
                Name: item.Name,
                Slug: item.Slug ?? string.Empty,
                AuthorHandle: item.Creator,
                AuthorName: null,
                ThumbnailUrl: item.ThumbnailUrl,
                LikesCount: item.LikeCount,
                DownloadCount: item.DownloadCount,
                SourceUrl: BuildModelUrl(item.Id, item.Slug)))];

        return new PrintablesSearchResultsDto(
            Items: summaries,
            TotalCount: totalCount,
            Offset: offset,
            Limit: limit,
            HasMore: hasMore);
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
        string? thumbnail = null;
        if (TryGetElement(collectionElement, out JsonElement thumbnails, "thumbnails") && thumbnails.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement thumb in thumbnails.EnumerateArray())
            {
                string? path = ReadOptionalString(thumb, "image", "filePath");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    thumbnail = BuildMediaUrl(path);
                    break;
                }
            }
        }

        return new PrintablesCollectionDto(
            Id: ReadRequiredString(collectionElement, "collection id", "id"),
            Name: ReadRequiredString(collectionElement, "collection name", "name"),
            Slug: ReadOptionalString(collectionElement, "slug"),
            Description: ReadOptionalString(collectionElement, "description", "summary"),
            ModelCount: ReadOptionalIntAny(collectionElement, "printsCount", "modelCount"),
            ThumbnailUrl: thumbnail ?? BuildMediaUrl(ReadOptionalString(collectionElement, "image", "filePath")));
    }

    private static PrintablesModelCardDto MapModelCard(JsonElement modelElement)
    {
        return new PrintablesModelCardDto(
            Id: ReadRequiredString(modelElement, "model id", "id"),
            Name: ReadRequiredString(modelElement, "model name", "name"),
            Slug: ReadOptionalString(modelElement, "slug"),
            Description: ReadOptionalString(modelElement, "summary", "description"),
            Creator: ReadOptionalString(modelElement, "user", "handle"),
            ThumbnailUrl: BuildMediaUrl(ReadOptionalString(modelElement, "image", "filePath")),
            LikeCount: ReadOptionalIntAny(modelElement, "likesCount", "likeCount"),
            DownloadCount: ReadOptionalIntAny(modelElement, "downloadCount", "downloadsCount"));
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
            ThumbnailUrl: BuildMediaUrl(ReadOptionalString(printElement, "image", "filePath")),
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

    private static int ReadOptionalIntAny(JsonElement element, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out JsonElement target))
            {
                return target.ValueKind switch
                {
                    JsonValueKind.Number when target.TryGetInt32(out int value) => value,
                    JsonValueKind.Number when target.TryGetInt64(out long value) && value <= int.MaxValue => (int)value,
                    _ => 0,
                };
            }
        }

        return 0;
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

    private static string NormalizeUsername(string value, string paramName)
    {
        string normalized = NormalizeRequired(value, paramName);
        normalized = WebUtility.UrlDecode(normalized).Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must not be empty.", paramName);
        }

        return normalized;
    }

    private static string? NormalizeOptionalOrdering(string? ordering)
    {
        if (string.IsNullOrWhiteSpace(ordering))
        {
            return null;
        }

        return ordering.Trim().ToLowerInvariant() switch
        {
            "added_to_collection" => "added_to_collection",
            "new_uploads" => "new_uploads",
            "downloads" => "downloads",
            "makes" => "makes",
            "likes" => "likes",
            "views" => "views",
            "rating" => "rating",
            _ => throw new ArgumentException($"Unsupported ordering value '{ordering}'.", nameof(ordering)),
        };
    }

    private static string? NormalizeOptionalSearchOrdering(string? ordering)
    {
        if (string.IsNullOrWhiteSpace(ordering))
        {
            return null;
        }

        return ordering.Trim().ToLowerInvariant() switch
        {
            "latest" => "latest",
            "popular" => "popular",
            "best_match" => "best_match",
            "rating" => "rating",
            "makes_count" => "makes_count",
            _ => throw new ArgumentException($"Unsupported search ordering value '{ordering}'.", nameof(ordering)),
        };
    }

    private static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        return Math.Min(limit, 100);
    }

    private static PrintablesUserProfileDto? SelectUserCandidate(JsonElement usersElement, string normalizedUsername)
    {
        if (usersElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<PrintablesUserProfileDto> candidates = [];
        foreach (JsonElement userElement in usersElement.EnumerateArray())
        {
            string? id = ReadOptionalString(userElement, "id");
            string? handle = ReadOptionalString(userElement, "handle");
            string? publicUsername = ReadOptionalString(userElement, "publicUsername");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(publicUsername))
            {
                continue;
            }

            candidates.Add(new PrintablesUserProfileDto(
                Id: id,
                Handle: handle,
                PublicUsername: publicUsername,
                AvatarUrl: BuildMediaUrl(ReadOptionalString(userElement, "avatarFilePath"))));
        }

        return candidates.FirstOrDefault(c =>
            string.Equals(c.Handle, normalizedUsername, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.PublicUsername, normalizedUsername, StringComparison.OrdinalIgnoreCase));
    }

    private static string? BuildMediaUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? absolute))
        {
            return absolute.ToString();
        }

        return $"{MediaBaseUrl}{path.TrimStart('/')}";
    }

    private static string BuildModelUrl(string id, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return $"{DefaultOrigin}/model/{id}";
        }

        return $"{DefaultOrigin}/model/{id}-{slug}";
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
