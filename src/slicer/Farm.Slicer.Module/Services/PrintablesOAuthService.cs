using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Security;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Printables OAuth2 linkage and guarded token-backed endpoint support.
/// </summary>
public sealed class PrintablesOAuthService(
    AppDbContext db,
    IMemoryCache cache,
    HttpClient httpClient,
    ISensitiveDataProtector sensitiveDataProtector,
    IOptions<PrintablesOAuthOptions> options,
    ILogger<PrintablesOAuthService> logger) : IPrintablesOAuthService
{
    private const string DefaultGraphQlEndpoint = "https://api.printables.com/graphql/";
    private const string DefaultOrigin = "https://www.printables.com";
    private const string MediaBaseUrl = "https://media.printables.com/";
    private static readonly TimeSpan TokenExpirySkew = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppDbContext _db = db;
    private readonly IMemoryCache _cache = cache;
    private readonly HttpClient _httpClient = httpClient;
    private readonly ISensitiveDataProtector _sensitiveDataProtector = sensitiveDataProtector;
    private readonly PrintablesOAuthOptions _options = options.Value;
    private readonly ILogger<PrintablesOAuthService> _logger = logger;

    public Task<PrintablesOAuthStatusDto> GetStatusAsync(Guid userId, CancellationToken ct) =>
        GetStatusInternalAsync(userId, ct);

    public async Task<PrintablesOAuthConnectResponseDto> BuildConnectUrlAsync(Guid userId, CancellationToken ct)
    {
        ValidateOAuthConfiguration();

        string state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
        string codeChallenge = ComputeCodeChallenge(codeVerifier);

        int ttlSeconds = Math.Max(60, _options.StateTtlSeconds);
        DateTime expiresAtUtc = DateTime.UtcNow.AddSeconds(ttlSeconds);

        _cache.Set(
            BuildStateCacheKey(state),
            new PendingOAuthState(userId, codeVerifier, expiresAtUtc),
            expiresAtUtc);

        string authorizationUrl = BuildAuthorizationUrl(state, codeChallenge);
        _logger.LogInformation("Prepared Printables OAuth2 connect request for user {UserId}", userId);

        return new PrintablesOAuthConnectResponseDto(authorizationUrl, expiresAtUtc);
    }

    private static bool IsNonRecoverableRefreshFailure(HttpStatusCode statusCode, string responseBody)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return true;
        }

        if (statusCode != HttpStatusCode.BadRequest)
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("error", out JsonElement errorElement) ||
                errorElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? error = errorElement.GetString();
            return string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(error, "unauthorized", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(error, "unauthorized_client", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(error, "invalid_client", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(error, "invalid_token", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<PrintablesOAuthStatusDto> HandleCallbackAsync(Guid userId, string code, string state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("state is required.", nameof(state));
        }

        ValidateOAuthConfiguration();

        string cacheKey = BuildStateCacheKey(state.Trim());
        if (!_cache.TryGetValue(cacheKey, out PendingOAuthState? pending) || pending is null)
        {
            throw new ArgumentException("OAuth state is invalid or expired.", nameof(state));
        }

        if (pending.UserId != userId)
        {
            throw new ArgumentException("OAuth state does not belong to the authenticated user.", nameof(state));
        }

        _cache.Remove(cacheKey);

        TokenExchangeResponse tokenResponse = await ExchangeCodeForTokensAsync(code.Trim(), pending.CodeVerifier, ct);

        UserSettings settings = await GetOrCreateUserSettingsAsync(userId, ct);
        ApplyLinkedOAuthTokens(settings, tokenResponse);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrent Printables OAuth callback write conflict for user {UserId}", userId);
            _db.ChangeTracker.Clear();

            UserSettings? latest = await _db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (!HasLinkedOAuthTokens(latest))
            {
                throw new PrintablesOAuthNotLinkedException(
                    "Printables account is no longer linked due to a concurrent update. Reconnect and try again.",
                    ex);
            }

            return MapStatus(latest!);
        }
        catch (DbUpdateException ex) when (IsUserSettingsUserIdUniqueViolation(ex))
        {
            _logger.LogWarning(ex, "Concurrent Printables OAuth callback first-link race for user {UserId}", userId);
            _db.ChangeTracker.Clear();

            UserSettings? latest = await _db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (latest is null)
            {
                throw new PrintablesOAuthNotLinkedException(
                    "Printables account is no longer linked due to a concurrent update. Reconnect and try again.",
                    ex);
            }

            if (!HasLinkedOAuthTokens(latest))
            {
                ApplyLinkedOAuthTokens(latest, tokenResponse);

                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException retryEx)
                {
                    _logger.LogWarning(retryEx, "Concurrent Printables OAuth callback race retry conflict for user {UserId}", userId);
                    _db.ChangeTracker.Clear();

                    UserSettings? latestAfterRetry = await _db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
                    if (HasLinkedOAuthTokens(latestAfterRetry))
                    {
                        return MapStatus(latestAfterRetry!);
                    }

                    throw new PrintablesOAuthTemporarilyUnavailableException(
                        "Printables authorization is temporarily unavailable due to concurrent updates. Please retry.",
                        retryEx);
                }
            }

            return MapStatus(latest);
        }

        _logger.LogInformation("Linked Printables OAuth2 account for user {UserId}", userId);
        return MapStatus(settings);
    }

    public async Task DisconnectAsync(Guid userId, CancellationToken ct)
    {
        UserSettings? settings = await _db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (settings is null)
        {
            return;
        }

        settings.PrintablesOAuthAccessToken = null;
        settings.PrintablesOAuthRefreshToken = null;
        settings.PrintablesOAuthTokenType = null;
        settings.PrintablesOAuthScope = null;
        settings.PrintablesOAuthTokenExpiresAtUtc = null;
        settings.PrintablesOAuthLinkedAtUtc = null;
        settings.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrent Printables OAuth disconnect conflict for user {UserId}", userId);
            _db.ChangeTracker.Clear();

            UserSettings? latest = await _db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (latest is null || !HasLinkedOAuthTokens(latest))
            {
                return;
            }

            ClearLinkedOAuthTokens(latest);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException retryEx)
            {
                throw new PrintablesOAuthTemporarilyUnavailableException(
                    "Printables authorization is temporarily unavailable due to concurrent updates. Please retry.",
                    retryEx);
            }
        }

        _logger.LogInformation("Disconnected Printables OAuth2 account for user {UserId}", userId);
    }

    public async Task<PrintablesAuthenticatedCursorPageDto> GetLikedModelsAsync(Guid userId, int limit, string? cursor, CancellationToken ct)
    {
        if (!_options.EnableAuthenticatedQueries)
        {
            throw new NotSupportedException(
                "Printables liked models retrieval is scaffolded but disabled. " +
                "Set PrintablesOAuth:EnableAuthenticatedQueries=true.");
        }

        string accessToken = await GetValidAccessTokenAsync(userId, ct);
        int normalizedLimit = Math.Min(Math.Max(limit, 1), 100);
        string? normalizedCursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();

        try
        {
            return await QueryAuthenticatedModelsAsync(
                accessToken,
                operationName: "AuthenticatedLikedModels",
                query: """
                    query AuthenticatedLikedModels($limit: Int, $cursor: String) {
                      viewer {
                        likedModels(limit: $limit, cursor: $cursor) {
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
                    }
                    """,
                variables: new { limit = normalizedLimit, cursor = normalizedCursor },
                connectionPaths:
                [
                    ["data", "viewer", "likedModels"],
                    ["data", "viewer", "likes"],
                    ["data", "likedModels"],
                    ["data", "me", "likedModels"],
                ],
                ct);
        }
        catch (UnauthorizedAccessException)
        {
            await InvalidateLinkedTokensAsync(userId, ct);
            throw new PrintablesOAuthNotLinkedException("Printables authorization expired or was revoked. Reconnect your Printables account.");
        }
        catch (PrintablesApiException ex) when (ex.IsTransient)
        {
            string message = "Printables liked models are temporarily unavailable. Please try again.";
            throw new PrintablesOAuthTemporarilyUnavailableException(message, ex);
        }
        catch (PrintablesApiException)
        {
            throw new NotSupportedException("Printables liked models data is unavailable from upstream at the moment.");
        }
    }

    public async Task<PrintablesAuthenticatedCursorPageDto> GetDownloadHistoryAsync(Guid userId, int limit, string? cursor, CancellationToken ct)
    {
        if (!_options.EnableAuthenticatedQueries)
        {
            throw new NotSupportedException(
                "Printables download history retrieval is scaffolded but disabled. " +
                "Set PrintablesOAuth:EnableAuthenticatedQueries=true.");
        }

        string accessToken = await GetValidAccessTokenAsync(userId, ct);
        int normalizedLimit = Math.Min(Math.Max(limit, 1), 100);
        string? normalizedCursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();

        try
        {
            return await QueryAuthenticatedModelsAsync(
                accessToken,
                operationName: "AuthenticatedDownloadHistory",
                query: """
                    query AuthenticatedDownloadHistory($limit: Int, $cursor: String) {
                      viewer {
                        downloadHistory(limit: $limit, cursor: $cursor) {
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
                    }
                    """,
                variables: new { limit = normalizedLimit, cursor = normalizedCursor },
                connectionPaths:
                [
                    ["data", "viewer", "downloadHistory"],
                    ["data", "viewer", "downloadsHistory"],
                    ["data", "downloadHistory"],
                    ["data", "me", "downloadHistory"],
                ],
                ct);
        }
        catch (UnauthorizedAccessException)
        {
            await InvalidateLinkedTokensAsync(userId, ct);
            throw new PrintablesOAuthNotLinkedException("Printables authorization expired or was revoked. Reconnect your Printables account.");
        }
        catch (PrintablesApiException ex) when (ex.IsTransient)
        {
            string message = "Printables download history is temporarily unavailable. Please try again.";
            throw new PrintablesOAuthTemporarilyUnavailableException(message, ex);
        }
        catch (PrintablesApiException)
        {
            throw new NotSupportedException("Printables download history data is unavailable from upstream at the moment.");
        }
    }

    private async Task<PrintablesOAuthStatusDto> GetStatusInternalAsync(Guid userId, CancellationToken ct)
    {
        UserSettings? settings = await _db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return settings is null ? new PrintablesOAuthStatusDto(false, null, null, false, null) : MapStatus(settings);
    }

    private static PrintablesOAuthStatusDto MapStatus(UserSettings settings)
    {
        bool isLinked = !string.IsNullOrWhiteSpace(settings.PrintablesOAuthAccessToken);
        return new PrintablesOAuthStatusDto(
            IsLinked: isLinked,
            AccessTokenExpiresAtUtc: settings.PrintablesOAuthTokenExpiresAtUtc,
            LinkedAtUtc: settings.PrintablesOAuthLinkedAtUtc,
            HasRefreshToken: !string.IsNullOrWhiteSpace(settings.PrintablesOAuthRefreshToken),
            Scope: settings.PrintablesOAuthScope);
    }

    private async Task<UserSettings> GetOrCreateUserSettingsAsync(Guid userId, CancellationToken ct)
    {
        UserSettings? settings = await _db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (settings is not null)
        {
            return settings;
        }

        settings = new UserSettings { UserId = userId };
        _db.UserSettings.Add(settings);
        return settings;
    }

    private async Task<string> GetValidAccessTokenAsync(Guid userId, CancellationToken ct)
    {
        UserSettings? settings = await _db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (settings is null || string.IsNullOrWhiteSpace(settings.PrintablesOAuthAccessToken))
        {
            throw new PrintablesOAuthNotLinkedException("Printables account is not linked.");
        }

        DateTime utcNow = DateTime.UtcNow;
        if (settings.PrintablesOAuthTokenExpiresAtUtc is DateTime expiresAtUtc &&
            expiresAtUtc <= utcNow.Add(TokenExpirySkew))
        {
            await RefreshOrInvalidateExpiredTokenAsync(settings, ct);
        }

        string? accessToken = _sensitiveDataProtector.Unprotect(settings.PrintablesOAuthAccessToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            await InvalidateLinkedTokensAsync(settings, ct);
            throw new PrintablesOAuthNotLinkedException("Stored Printables credentials are invalid. Reconnect your Printables account.");
        }

        return accessToken;
    }

    private async Task<TokenExchangeResponse> ExchangeCodeForTokensAsync(string code, string codeVerifier, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["redirect_uri"] = _options.RedirectUri,
                ["code_verifier"] = codeVerifier,
            }!)
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new PrintablesApiException(
                $"Printables OAuth token exchange failed with HTTP {(int)response.StatusCode}.");
        }

        string responseBody = await response.Content.ReadAsStringAsync(ct);
        return ParseTokenExchangeResponse(responseBody);
    }

    private async Task RefreshOrInvalidateExpiredTokenAsync(UserSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.PrintablesOAuthRefreshToken))
        {
            await InvalidateLinkedTokensAsync(settings, ct);
            throw new PrintablesOAuthNotLinkedException("Printables authorization has expired. Reconnect your Printables account.");
        }

        string? refreshToken = _sensitiveDataProtector.Unprotect(settings.PrintablesOAuthRefreshToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            await InvalidateLinkedTokensAsync(settings, ct);
            throw new PrintablesOAuthNotLinkedException("Stored Printables refresh token is invalid. Reconnect your Printables account.");
        }

        TokenExchangeResponse refreshed;
        try
        {
            refreshed = await ExchangeRefreshTokenAsync(refreshToken, ct);
        }
        catch (PrintablesOAuthNotLinkedException)
        {
            await InvalidateLinkedTokensAsync(settings, ct);
            throw;
        }
        catch (PrintablesApiException ex)
        {
            string message = "Printables authorization refresh is temporarily unavailable. Please try again.";
            throw new PrintablesOAuthTemporarilyUnavailableException(message, ex);
        }

        settings.PrintablesOAuthAccessToken = _sensitiveDataProtector.Protect(refreshed.AccessToken);
        settings.PrintablesOAuthRefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
            ? settings.PrintablesOAuthRefreshToken
            : _sensitiveDataProtector.Protect(refreshed.RefreshToken);
        settings.PrintablesOAuthTokenType = string.IsNullOrWhiteSpace(refreshed.TokenType)
            ? settings.PrintablesOAuthTokenType
            : refreshed.TokenType;
        settings.PrintablesOAuthScope = string.IsNullOrWhiteSpace(refreshed.Scope)
            ? settings.PrintablesOAuthScope
            : refreshed.Scope;
        settings.PrintablesOAuthTokenExpiresAtUtc = refreshed.ExpiresAtUtc;
        settings.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrent Printables OAuth refresh conflict for user {UserId}", settings.UserId);
            _db.ChangeTracker.Clear();

            UserSettings? latest = await _db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == settings.UserId, ct);
            if (!HasLinkedOAuthTokens(latest))
            {
                throw new PrintablesOAuthNotLinkedException(
                    "Printables authorization changed concurrently and is no longer linked. Reconnect your account.",
                    ex);
            }

            throw new PrintablesOAuthTemporarilyUnavailableException(
                "Printables authorization was updated concurrently. Please retry.",
                ex);
        }
    }

    private async Task<TokenExchangeResponse> ExchangeRefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
            }!)
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync(ct);
            bool isTransient = IsTransientStatusCode(response.StatusCode);
            if (isTransient)
            {
                throw new PrintablesOAuthTemporarilyUnavailableException(
                    "Printables authorization refresh is temporarily unavailable. Please try again.");
            }

            if (IsNonRecoverableRefreshFailure(response.StatusCode, responseBody))
            {
                throw new PrintablesOAuthNotLinkedException(
                    "Printables authorization refresh was rejected. Reconnect your Printables account.");
            }

            throw new PrintablesApiException($"Printables OAuth token refresh failed with HTTP {(int)response.StatusCode}.");
        }

        string successfulResponseBody = await response.Content.ReadAsStringAsync(ct);
        return ParseTokenExchangeResponse(successfulResponseBody);
    }

    private static TokenExchangeResponse ParseTokenExchangeResponse(string responseBody)
    {
        TokenExchangePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenExchangePayload>(responseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            throw new PrintablesApiException("Printables OAuth token response could not be parsed.", ex);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new PrintablesApiException("Printables OAuth token exchange returned no access token.");
        }

        DateTime? expiresAtUtc = payload.ExpiresIn.HasValue
            ? DateTime.UtcNow.AddSeconds(Math.Max(0, payload.ExpiresIn.Value))
            : null;

        return new TokenExchangeResponse(
            payload.AccessToken,
            payload.RefreshToken,
            payload.TokenType,
            payload.Scope,
            expiresAtUtc);
    }

    private async Task<PrintablesAuthenticatedCursorPageDto> QueryAuthenticatedModelsAsync(
        string accessToken,
        string operationName,
        string query,
        object variables,
        string[][] connectionPaths,
        CancellationToken ct)
    {
        using JsonDocument document = await SendAuthenticatedGraphQlAsync(
            accessToken,
            operationName,
            new
            {
                operationName,
                query,
                variables,
            },
            ct);

        JsonElement connectionElement = GetRequiredByPaths(
            document.RootElement,
            operationName,
            connectionPaths);

        List<PrintablesModelSummaryDto> items = [];
        foreach (JsonElement item in EnumerateConnectionNodes(connectionElement))
        {
            items.Add(MapModelSummary(item));
        }

        string? nextCursor = ReadOptionalString(connectionElement, "cursor");
        bool hasMore = !string.IsNullOrWhiteSpace(nextCursor);
        return new PrintablesAuthenticatedCursorPageDto(items, nextCursor, hasMore);
    }

    private async Task<JsonDocument> SendAuthenticatedGraphQlAsync(
        string accessToken,
        string operationName,
        object payload,
        CancellationToken ct)
    {
        string endpoint = DefaultGraphQlEndpoint;
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: _jsonOptions),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Origin", DefaultOrigin);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PrintablesApiException($"Printables '{operationName}' timed out.", ex, isTransient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new PrintablesApiException($"Failed to reach Printables for '{operationName}'.", ex, isTransient: true);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException("Printables OAuth token is unauthorized.");
            }

            if (!response.IsSuccessStatusCode)
            {
                bool transient = IsTransientStatusCode(response.StatusCode);
                throw new PrintablesApiException(
                    $"Printables API returned HTTP {(int)response.StatusCode} for '{operationName}'.",
                    isTransient: transient);
            }

            string raw = await response.Content.ReadAsStringAsync(ct);
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
                throw new PrintablesApiException($"Printables GraphQL error during '{operationName}': {message ?? "Unknown error"}");
            }

            return doc;
        }
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
            $"Printables response for '{operationName}' was missing required path(s): {string.Join(" | ", paths.Select(p => string.Join(".", p)))}.");
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

        throw new PrintablesApiException("Printables authenticated response had no collection edges/nodes/items.");
    }

    private static PrintablesModelSummaryDto MapModelSummary(JsonElement modelElement)
    {
        string id = ReadRequiredString(modelElement, "model id", "id");
        string slug = ReadOptionalString(modelElement, "slug") ?? string.Empty;
        return new PrintablesModelSummaryDto(
            Id: id,
            Name: ReadRequiredString(modelElement, "model name", "name"),
            Slug: slug,
            AuthorHandle: ReadOptionalString(modelElement, "user", "handle"),
            AuthorName: null,
            ThumbnailUrl: BuildMediaUrl(ReadOptionalString(modelElement, "image", "filePath")),
            LikesCount: ReadOptionalIntAny(modelElement, "likesCount", "likeCount"),
            DownloadCount: ReadOptionalIntAny(modelElement, "downloadCount", "downloadsCount"),
            SourceUrl: BuildModelUrl(id, slug));
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

    private async Task InvalidateLinkedTokensAsync(Guid userId, CancellationToken ct)
    {
        UserSettings? settings = await _db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (settings is null)
        {
            return;
        }

        await InvalidateLinkedTokensAsync(settings, ct);
    }

    private async Task InvalidateLinkedTokensAsync(UserSettings settings, CancellationToken ct)
    {
        bool hadToken = !string.IsNullOrWhiteSpace(settings.PrintablesOAuthAccessToken) ||
                        !string.IsNullOrWhiteSpace(settings.PrintablesOAuthRefreshToken);
        if (!hadToken)
        {
            return;
        }

        ClearLinkedOAuthTokens(settings);
        settings.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrent Printables OAuth invalidate conflict for user {UserId}", settings.UserId);
            _db.ChangeTracker.Clear();

            UserSettings? latest = await _db.UserSettings.FirstOrDefaultAsync(x => x.UserId == settings.UserId, ct);
            if (latest is null || !HasLinkedOAuthTokens(latest))
            {
                return;
            }

            ClearLinkedOAuthTokens(latest);
            latest.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException retryEx)
            {
                throw new PrintablesOAuthTemporarilyUnavailableException(
                    "Printables authorization is temporarily unavailable due to concurrent updates. Please retry.",
                    retryEx);
            }
        }
    }

    private static bool HasLinkedOAuthTokens(UserSettings? settings) =>
        settings is not null && !string.IsNullOrWhiteSpace(settings.PrintablesOAuthAccessToken);

    private void ApplyLinkedOAuthTokens(UserSettings settings, TokenExchangeResponse tokenResponse)
    {
        settings.PrintablesOAuthAccessToken = _sensitiveDataProtector.Protect(tokenResponse.AccessToken);
        settings.PrintablesOAuthRefreshToken = _sensitiveDataProtector.Protect(tokenResponse.RefreshToken);
        settings.PrintablesOAuthTokenType = tokenResponse.TokenType;
        settings.PrintablesOAuthScope = tokenResponse.Scope;
        settings.PrintablesOAuthTokenExpiresAtUtc = tokenResponse.ExpiresAtUtc;
        settings.PrintablesOAuthLinkedAtUtc = DateTime.UtcNow;
        settings.UpdatedAt = DateTime.UtcNow;
    }

    private static void ClearLinkedOAuthTokens(UserSettings settings)
    {
        settings.PrintablesOAuthAccessToken = null;
        settings.PrintablesOAuthRefreshToken = null;
        settings.PrintablesOAuthTokenType = null;
        settings.PrintablesOAuthScope = null;
        settings.PrintablesOAuthTokenExpiresAtUtc = null;
        settings.PrintablesOAuthLinkedAtUtc = null;
    }

    private static bool IsUserSettingsUserIdUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is SqliteException sqliteEx &&
            sqliteEx.SqliteErrorCode == 19 &&
            sqliteEx.Message.Contains("UNIQUE constraint failed: UserSettings.UserId", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ex.InnerException is System.Data.Common.DbException dbException)
        {
            string fullName = dbException.GetType().FullName ?? string.Empty;
            if (fullName.Contains("SqlException", StringComparison.OrdinalIgnoreCase) &&
                dbException.ErrorCode is 2601 or 2627 &&
                dbException.Message.Contains("IX_UserSettings_UserId", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ex.InnerException?.GetType().FullName?.Contains("PostgresException", StringComparison.OrdinalIgnoreCase) == true)
        {
            string? sqlState = ex.InnerException.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException)?.ToString();
            string? constraintName = ex.InnerException.GetType().GetProperty("ConstraintName")?.GetValue(ex.InnerException)?.ToString();
            if (sqlState == "23505" &&
                string.Equals(constraintName, "IX_UserSettings_UserId", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("IX_UserSettings_UserId", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code == 408 || code == 429 || code >= 500;
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

    private string BuildAuthorizationUrl(string state, string codeChallenge)
    {
        Dictionary<string, string?> query = new()
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        string queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value ?? string.Empty)}"));
        return $"{_options.AuthorizationEndpoint}?{queryString}";
    }

    private void ValidateOAuthConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.AuthorizationEndpoint) ||
            string.IsNullOrWhiteSpace(_options.TokenEndpoint) ||
            string.IsNullOrWhiteSpace(_options.ClientId) ||
            string.IsNullOrWhiteSpace(_options.ClientSecret) ||
            string.IsNullOrWhiteSpace(_options.RedirectUri))
        {
            throw new InvalidOperationException(
                "Printables OAuth is not configured. Set PrintablesOAuth:AuthorizationEndpoint, TokenEndpoint, ClientId, ClientSecret, and RedirectUri.");
        }
    }

    private static string ComputeCodeChallenge(string codeVerifier)
    {
        byte[] challengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        return Base64UrlEncode(challengeBytes);
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string BuildStateCacheKey(string state) => $"printables:oauth:state:{state}";

    private sealed record PendingOAuthState(Guid UserId, string CodeVerifier, DateTime ExpiresAtUtc);

    private sealed record TokenExchangeResponse(
        string AccessToken,
        string? RefreshToken,
        string? TokenType,
        string? Scope,
        DateTime? ExpiresAtUtc);

    private sealed class TokenExchangePayload
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        public string? Scope { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }
    }
}
