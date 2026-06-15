using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Security;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services.Configuration;
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
        settings.PrintablesOAuthAccessToken = _sensitiveDataProtector.Protect(tokenResponse.AccessToken);
        settings.PrintablesOAuthRefreshToken = _sensitiveDataProtector.Protect(tokenResponse.RefreshToken);
        settings.PrintablesOAuthTokenType = tokenResponse.TokenType;
        settings.PrintablesOAuthScope = tokenResponse.Scope;
        settings.PrintablesOAuthTokenExpiresAtUtc = tokenResponse.ExpiresAtUtc;
        settings.PrintablesOAuthLinkedAtUtc = DateTime.UtcNow;
        settings.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

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
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Disconnected Printables OAuth2 account for user {UserId}", userId);
    }

    public async Task<PrintablesAuthenticatedCursorPageDto> GetLikedModelsAsync(Guid userId, int limit, string? cursor, CancellationToken ct)
    {
        (string _, bool isLinked) = await GetLinkedAccessTokenAsync(userId, ct);
        if (!isLinked)
        {
            throw new InvalidOperationException("Printables account is not linked.");
        }

        if (!_options.EnableAuthenticatedQueries)
        {
            throw new NotSupportedException(
                "Printables liked models retrieval is scaffolded but disabled. " +
                "Set PrintablesOAuth:EnableAuthenticatedQueries=true and wire final Printables authenticated query mapping.");
        }

        throw new NotSupportedException("TODO: Implement Printables liked models query mapping against authenticated API.");
    }

    public async Task<PrintablesAuthenticatedCursorPageDto> GetDownloadHistoryAsync(Guid userId, int limit, string? cursor, CancellationToken ct)
    {
        (string _, bool isLinked) = await GetLinkedAccessTokenAsync(userId, ct);
        if (!isLinked)
        {
            throw new InvalidOperationException("Printables account is not linked.");
        }

        if (!_options.EnableAuthenticatedQueries)
        {
            throw new NotSupportedException(
                "Printables download history retrieval is scaffolded but disabled. " +
                "Set PrintablesOAuth:EnableAuthenticatedQueries=true and wire final Printables authenticated query mapping.");
        }

        throw new NotSupportedException("TODO: Implement Printables download history query mapping against authenticated API.");
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

    private async Task<(string AccessToken, bool IsLinked)> GetLinkedAccessTokenAsync(Guid userId, CancellationToken ct)
    {
        UserSettings? settings = await _db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (settings is null || string.IsNullOrWhiteSpace(settings.PrintablesOAuthAccessToken))
        {
            return (string.Empty, false);
        }

        string? accessToken = _sensitiveDataProtector.Unprotect(settings.PrintablesOAuthAccessToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return (string.Empty, false);
        }

        return (accessToken, true);
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
        string responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new PrintablesApiException(
                $"Printables OAuth token exchange failed with HTTP {(int)response.StatusCode}.");
        }

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
        public string? AccessToken { get; set; }

        public string? RefreshToken { get; set; }

        public string? TokenType { get; set; }

        public string? Scope { get; set; }

        public int? ExpiresIn { get; set; }
    }
}
