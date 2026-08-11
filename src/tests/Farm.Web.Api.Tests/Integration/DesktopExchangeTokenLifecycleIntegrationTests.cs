using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Authentication;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Pins the authentication half of the Desktop exchange contract, which the scope-boundary suites
/// deliberately do not cover: token lifetime, the 401-vs-403 distinction, and what re-exchange does
/// after a token stops being usable.
/// </summary>
/// <remarks>
/// The distinction matters operationally. A desktop client must be able to tell "my token aged out,
/// exchange again" (401) from "this key was never granted that authority, stop retrying" (403).
/// Collapsing the two would either strand a client on a recoverable condition or send it into a
/// retry loop on an unrecoverable one.
/// </remarks>
[Trait("Category", "DbHeavy")]
[Collection(IntegrationTestCollection.Name)]
[TestTiming]
public class DesktopExchangeTokenLifecycleIntegrationTests : IAsyncLifetime
{
    private const string SigningKey = "DesktopExchangeLifecycleIntegrationTestSigningKey-0123456789";
    private const string Issuer = "PrintFarmer";
    private const string Audience = "PrintFarmer";

    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _anonymousClient = null!;
    private Guid _ownerId;

    public DesktopExchangeTokenLifecycleIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            // The GET bypass would mask the 401/403 distinction this class exists to pin.
            ["Security:DevModeBypassAuth"] = "false",
            // Pinned so the test can mint an already-expired token the host will accept as
            // well-formed and reject only on lifetime.
            ["Jwt:Key"] = SigningKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        });
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _anonymousClient = _factory.CreateClient();

        using HttpClient seedLogin = await _factory.CreateAuthenticatedClientAsync(
            "exchange-lifecycle-owner",
            "exchange-lifecycle-owner@example.com",
            "TestPassword123!");

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User owner = await context.Users.SingleAsync(u => u.Username == "exchange-lifecycle-owner");
        _ownerId = owner.Id;
    }

    public Task DisposeAsync()
    {
        _anonymousClient?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private static string ComputeSha256Hash(string rawData) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawData)));

    private async Task<(string RawKey, Guid KeyId)> SeedDesktopKeyAsync(ApiKeyScope scopes)
    {
        string rawKey = $"raw-{Guid.NewGuid():N}";
        Guid keyId = Guid.NewGuid();

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.ApiKeys.Add(new ApiKey
        {
            Id = keyId,
            UserId = _ownerId,
            Name = "exchange-lifecycle-key",
            KeyHash = ComputeSha256Hash(rawKey),
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = scopes,
            IsActive = true,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        });
        await context.SaveChangesAsync();
        return (rawKey, keyId);
    }

    private async Task<ApiKeyExchangeResponse> ExchangeAsync(string rawKey)
    {
        HttpResponseMessage response = await _anonymousClient.PostAsJsonAsync(
            "/api/auth/api-key/exchange", new { apiKey = rawKey });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ApiKeyExchangeResponse? body = await response.Content.ReadFromJsonAsync<ApiKeyExchangeResponse>();
        body.Should().NotBeNull();
        return body!;
    }

    private HttpClient BearerClient(string token)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return client;
    }

    /// <summary>
    /// Mints a token shaped exactly like an exchanged one but already expired, so the host accepts
    /// its signature/issuer/audience and rejects it solely on lifetime.
    /// </summary>
    private string CreateExpiredExchangeToken()
    {
        SecurityTokenDescriptor descriptor = new()
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _ownerId.ToString()),
                new Claim(ClaimTypes.Name, "exchange-lifecycle-owner"),
                new Claim(DesktopScopeClaims.TokenUse, DesktopScopeClaims.DesktopExchangeTokenUse),
                new Claim(DesktopScopeClaims.ApiKeyId, Guid.NewGuid().ToString()),
                new Claim(DesktopScopeClaims.Scope, nameof(ApiKeyScope.ModelRead)),
            ]),
            NotBefore = DateTime.UtcNow.AddMinutes(-30),
            Expires = DateTime.UtcNow.AddMinutes(-5),
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    #region (1) Default lifetime

    [Fact]
    public async Task ExchangedToken_DefaultLifetimeIsFifteenMinutes()
    {
        (string rawKey, _) = await SeedDesktopKeyAsync(ApiKeyScope.ModelRead);

        ApiKeyExchangeResponse body = await ExchangeAsync(rawKey);

        body.ExpiresAt.Should().BeCloseTo(
            DateTime.UtcNow.AddMinutes(ApiKeyExchangeService.DefaultLifetimeMinutes),
            TimeSpan.FromMinutes(1),
            "the Desktop exchange default lifetime is 15 minutes");
        ApiKeyExchangeService.DefaultLifetimeMinutes.Should().Be(15);
        ApiKeyExchangeService.MaxLifetimeMinutes.Should().Be(15);
    }

    #endregion

    #region (2) 401 for failed authentication, 403 for missing permission

    [Fact]
    public async Task ExpiredExchangeToken_IsRejectedAsUnauthorized()
    {
        using HttpClient client = BearerClient(CreateExpiredExchangeToken());

        using HttpResponseMessage response = await client.GetAsync("/api/gcode-library");

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "an expired token fails authentication, which is recoverable by re-exchanging");
    }

    [Fact]
    public async Task ForciblyRevokedExchangeToken_IsRejectedAsUnauthorized()
    {
        (string rawKey, _) = await SeedDesktopKeyAsync(ApiKeyScope.ModelRead);
        ApiKeyExchangeResponse body = await ExchangeAsync(rawKey);
        using HttpClient client = BearerClient(body.Token);

        // Sanity: the token works before revocation, so the 401 below cannot be explained by the
        // token never having been valid.
        using (HttpResponseMessage before = await client.GetAsync($"/api/gcode-library/{Guid.NewGuid()}"))
        {
            before.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
            before.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        }

        // Revoke in a strictly later second than the token's whole-second `nbf`, which is the
        // real-world case: an operator revokes some time after the token was issued.
        await Task.Delay(TimeSpan.FromSeconds(1.1));

        using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            ITokenRevocationService revocation =
                scope.ServiceProvider.GetRequiredService<ITokenRevocationService>();
            await revocation.RevokeAllUserTokensAsync(_ownerId, _ownerId, "test forced revocation");
        }

        using HttpResponseMessage after = await client.GetAsync($"/api/gcode-library/{Guid.NewGuid()}");
        after.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "an ALL_TOKENS_ revocation marker fails authentication for tokens issued before it");
    }

    /// <summary>
    /// The contrast that makes the contract useful: authenticated but under-scoped is 403, not 401.
    /// </summary>
    [Fact]
    public async Task AuthenticatedTokenMissingPermission_IsForbiddenNotUnauthorized()
    {
        (string rawKey, _) = await SeedDesktopKeyAsync(ApiKeyScope.ModelRead);
        ApiKeyExchangeResponse body = await ExchangeAsync(rawKey);
        using HttpClient client = BearerClient(body.Token);

        using HttpResponseMessage response = await client.GetAsync("/api/calibration-projects");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the token authenticated successfully; it simply lacks calibration:read");
    }

    #endregion

    #region (3) Re-exchange after the token stops being usable

    [Fact]
    public async Task ReExchange_AfterRevocation_SucceedsWhileKeyAndOwnerRemainValid()
    {
        (string rawKey, _) = await SeedDesktopKeyAsync(ApiKeyScope.ModelRead);
        _ = await ExchangeAsync(rawKey);

        using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            ITokenRevocationService revocation =
                scope.ServiceProvider.GetRequiredService<ITokenRevocationService>();
            await revocation.RevokeAllUserTokensAsync(_ownerId, _ownerId, "test forced revocation");
        }

        // No delay: a real client reacts to the 401 immediately. JWT `nbf` has whole-second
        // resolution while the revocation marker keeps fractional seconds, so a naive
        // `RevokedAt > issuedAt` comparison would reject this brand-new token and strand the
        // client in a retry loop until the clock ticked over. This asserts the recovery contract
        // holds at the moment it is actually exercised.
        ApiKeyExchangeResponse second = await ExchangeAsync(rawKey);
        second.Token.Should().NotBeNullOrWhiteSpace();

        using HttpClient client = BearerClient(second.Token);
        using HttpResponseMessage response = await client.GetAsync($"/api/gcode-library/{Guid.NewGuid()}");
        response.StatusCode.Should().NotBe(
            HttpStatusCode.Unauthorized,
            "revocation invalidates issued tokens, not the key: an immediately re-exchanged token must work");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReExchange_AfterTheKeyItselfIsRevoked_Fails()
    {
        (string rawKey, Guid keyId) = await SeedDesktopKeyAsync(ApiKeyScope.ModelRead);
        _ = await ExchangeAsync(rawKey);

        using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ApiKey key = await context.ApiKeys.SingleAsync(k => k.Id == keyId);
            key.IsActive = false;
            await context.SaveChangesAsync();
        }

        using HttpResponseMessage response = await _anonymousClient.PostAsJsonAsync(
            "/api/auth/api-key/exchange", new { apiKey = rawKey });

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "a revoked key can no longer be exchanged, and the failure stays generic");
    }

    [Fact]
    public async Task ReExchange_AfterTheOwnerIsDeactivated_Fails()
    {
        (string rawKey, _) = await SeedDesktopKeyAsync(ApiKeyScope.ModelRead);
        _ = await ExchangeAsync(rawKey);

        using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User owner = await context.Users.SingleAsync(u => u.Id == _ownerId);
            owner.IsActive = false;
            await context.SaveChangesAsync();
        }

        using HttpResponseMessage response = await _anonymousClient.PostAsJsonAsync(
            "/api/auth/api-key/exchange", new { apiKey = rawKey });

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "owner authority is re-resolved on every exchange");
    }

    #endregion
}
