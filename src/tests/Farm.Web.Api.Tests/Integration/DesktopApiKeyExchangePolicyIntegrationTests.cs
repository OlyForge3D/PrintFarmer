using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// End-to-end coverage for issue #839: exercises the full Desktop API key exchange flow
/// (key -&gt; JWT -&gt; scope-gated controller access) through the real HTTP pipeline, proving
/// that <see cref="Farm.Infrastructure.Authorization.DesktopScopeRequirement"/>-backed
/// policies (ModelRead/ModelWrite/LibrarySync) are enforced per exchanged token, that
/// regular login sessions are unaffected, that revoked/expired/unscoped/wrong-purpose keys
/// are rejected, and that both success and failure exchanges are recorded in the audit log.
/// </summary>
[Trait("Category", "DbHeavy")]
[TestTiming]
[Collection(RateLimiterEnvSerialCollection.Name)]
public class DesktopApiKeyExchangePolicyIntegrationTests : IClassFixture<DesktopApiKeyExchangePolicyIntegrationTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory()
            : base(new Dictionary<string, string?>
            {
                ["Security:DevModeBypassAuth"] = "false",
                // This class's host (and its singleton in-memory rate limiter) is shared
                // across every test via IClassFixture, and several tests here each exchange
                // an API key once. Raise the ceiling well above what the shared-factory tests
                // perform so their cumulative attempts never trip the default limit (5/minute)
                // meant for a single client in production. The one test that specifically
                // validates the default limit (RepeatedExchangeAttempts_ExceedingLimit_AreRateLimited)
                // uses its own dedicated factory with the default rate-limit config instead.
                ["RateLimiting:Authentication:MaxApiKeyExchangeAttemptsPerMinute"] = "1000"
            })
        {
        }
    }

    private readonly Factory _factory;
    private HttpClient _anonymousClient = null!;
    private HttpClient _loginClient = null!;
    private Guid _ownerId;

    public DesktopApiKeyExchangePolicyIntegrationTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _anonymousClient = _factory.CreateClient();
        _loginClient = await _factory.CreateAuthenticatedClientAsync(
            "desktop-policy-owner",
            "desktop-policy-owner@example.com",
            "TestPassword123!");

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User owner = await context.Users.SingleAsync(u => u.Username == "desktop-policy-owner");
        _ownerId = owner.Id;
    }

    public Task DisposeAsync()
    {
        _anonymousClient?.Dispose();
        _loginClient?.Dispose();
        return Task.CompletedTask;
    }

    private static string ComputeSha256Hash(string rawData)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(rawData);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private async Task<string> SeedApiKeyAsync(
        ApiKeyPurpose purpose,
        ApiKeyScope scopes,
        bool isActive = true,
        DateTime? expiresAt = null,
        Guid? ownerId = null)
    {
        string rawKey = $"raw-{Guid.NewGuid():N}";
        string hash = ComputeSha256Hash(rawKey);

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            UserId = ownerId ?? _ownerId,
            Name = "issue-839-test-key",
            KeyHash = hash,
            Purpose = purpose,
            Scopes = scopes,
            IsActive = isActive,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(30)
        });
        await context.SaveChangesAsync();
        return rawKey;
    }

    private async Task<HttpResponseMessage> ExchangeAsync(string rawKey) =>
        await _anonymousClient.PostAsJsonAsync("/api/auth/api-key/exchange", new { apiKey = rawKey });

    private async Task<string> ExchangeForTokenAsync(string rawKey)
    {
        HttpResponseMessage response = await ExchangeAsync(rawKey);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seeded key is an active, in-scope Desktop key");
        ApiKeyExchangeResponse? body = await response.Content.ReadFromJsonAsync<ApiKeyExchangeResponse>();
        body.Should().NotBeNull();
        body!.Token.Should().NotBeNullOrWhiteSpace();
        return body.Token;
    }

    private HttpClient CreateBearerClient(string token)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return client;
    }

    [Fact]
    public async Task Exchange_WithLibrarySyncScope_CanListLibraryButNotReadIndividualFile()
    {
        string rawKey = await SeedApiKeyAsync(ApiKeyPurpose.Desktop, ApiKeyScope.LibrarySync);
        string token = await ExchangeForTokenAsync(rawKey);
        using HttpClient client = CreateBearerClient(token);

        HttpResponseMessage listResponse = await client.GetAsync("/api/gcode-library");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the token carries LibrarySync, which the endpoint requires");

        HttpResponseMessage getResponse = await client.GetAsync($"/api/gcode-library/{Guid.NewGuid()}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, "ModelRead was not granted to this token");
    }

    [Fact]
    public async Task Exchange_WithModelReadScope_CanReadIndividualFileButNotListLibrary()
    {
        string rawKey = await SeedApiKeyAsync(ApiKeyPurpose.Desktop, ApiKeyScope.ModelRead);
        string token = await ExchangeForTokenAsync(rawKey);
        using HttpClient client = CreateBearerClient(token);

        HttpResponseMessage getResponse = await client.GetAsync($"/api/gcode-library/{Guid.NewGuid()}");
        getResponse.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "ModelRead was granted (a 404 for the random id is expected instead)");

        HttpResponseMessage listResponse = await client.GetAsync("/api/gcode-library");
        listResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, "LibrarySync was not granted to this token");
    }

    [Fact]
    public async Task Exchange_WithModelWriteScope_PassesUploadAuthorizationButNotListPolicy()
    {
        string rawKey = await SeedApiKeyAsync(ApiKeyPurpose.Desktop, ApiKeyScope.ModelWrite);
        string token = await ExchangeForTokenAsync(rawKey);
        using HttpClient client = CreateBearerClient(token);

        using var content = new MultipartFormDataContent();
        HttpResponseMessage uploadResponse = await client.PostAsync("/api/gcode-library/upload", content);
        uploadResponse.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            "ModelWrite was granted, so authorization passes even though the empty body fails model validation");

        HttpResponseMessage listResponse = await client.GetAsync("/api/gcode-library");
        listResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, "LibrarySync was not granted to this token");
    }

    [Fact]
    public async Task Exchange_WithNoScopes_IsRejectedBeforeAnyTokenIsIssued()
    {
        string rawKey = await SeedApiKeyAsync(ApiKeyPurpose.Desktop, ApiKeyScope.None);

        HttpResponseMessage response = await ExchangeAsync(rawKey);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an unscoped Desktop key must never yield a usable token");
    }

    [Fact]
    public async Task Exchange_WithOctoPrintPurposeKey_IsRejected()
    {
        string rawKey = await SeedApiKeyAsync(ApiKeyPurpose.OctoPrint, ApiKeyScope.None);

        HttpResponseMessage response = await ExchangeAsync(rawKey);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "legacy/OctoPrint-purpose keys must never exchange for a Desktop JWT");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid API key").And.NotContain("purpose", "the error must not leak why the key was rejected");
    }

    [Fact]
    public async Task Exchange_WithRevokedKey_IsRejected()
    {
        string rawKey = await SeedApiKeyAsync(ApiKeyPurpose.Desktop, ApiKeyScope.ModelRead, isActive: false);

        HttpResponseMessage response = await ExchangeAsync(rawKey);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Exchange_WithExpiredKey_IsRejected()
    {
        string rawKey = await SeedApiKeyAsync(ApiKeyPurpose.Desktop, ApiKeyScope.ModelRead, expiresAt: DateTime.UtcNow.AddMinutes(-5));

        HttpResponseMessage response = await ExchangeAsync(rawKey);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Exchange_WithUnknownKey_IsRejected()
    {
        HttpResponseMessage response = await ExchangeAsync("this-key-was-never-issued");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegularLoginSession_StillPassesDesktopScopePolicies()
    {
        // Regression: the DesktopScopeAuthorizationHandler must pass through non-desktop-exchange
        // principals (a regular login JWT carries no token_use claim) unchanged, so #837/#838
        // did not break existing session-based access to these endpoints.
        HttpResponseMessage listResponse = await _loginClient.GetAsync("/api/gcode-library");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage getResponse = await _loginClient.GetAsync($"/api/gcode-library/{Guid.NewGuid()}");
        getResponse.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SuccessfulExchange_PersistsAuditRecordWithoutRawKeyMaterial()
    {
        string rawKey = await SeedApiKeyAsync(ApiKeyPurpose.Desktop, ApiKeyScope.ModelRead);
        await ExchangeForTokenAsync(rawKey);

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthAuditLog? log = await context.AuthAuditLogs
            .Where(l => l.EventType == AuthEventType.ApiKeyExchange && l.UserId == _ownerId)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefaultAsync();

        log.Should().NotBeNull("a successful exchange must be audited");
        log!.Success.Should().BeTrue();
        log.Metadata.Should().NotContain(rawKey, "the raw API key must never be persisted in audit metadata");
    }

    [Fact]
    public async Task FailedExchange_PersistsAuditRecordWithoutRawKeyMaterial()
    {
        string rawKey = await SeedApiKeyAsync(ApiKeyPurpose.Desktop, ApiKeyScope.None);
        await ExchangeAsync(rawKey);

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthAuditLog? log = await context.AuthAuditLogs
            .Where(l => l.EventType == AuthEventType.ApiKeyExchangeFailed)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefaultAsync();

        log.Should().NotBeNull("a failed exchange must be audited");
        log!.Success.Should().BeFalse();
        log.FailureReason.Should().Be("no_scopes_granted");
        (log.Metadata ?? string.Empty).Should().NotContain(rawKey);
    }

    [Fact]
    public async Task RepeatedExchangeAttempts_ExceedingLimit_AreRateLimited()
    {
        // The shared class Factory raises MaxApiKeyExchangeAttemptsPerMinute so the other
        // tests in this class (which share its host/rate-limiter singleton) never trip the
        // limit. Validating the actual default limit therefore needs its own dedicated
        // factory/host with the default (unmodified) rate-limit config, isolated from every
        // other test's exchange attempts.
        await using var factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false"
        });
        await factory.ResetDataAsync();

        HttpClient loginClient = await factory.CreateAuthenticatedClientAsync(
            "rate-limit-owner",
            "rate-limit-owner@example.com",
            "TestPassword123!");
        loginClient.Dispose();

        Guid ownerId;
        using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User owner = await context.Users.SingleAsync(u => u.Username == "rate-limit-owner");
            ownerId = owner.Id;

            string rawKeySeed = $"raw-{Guid.NewGuid():N}";
            context.ApiKeys.Add(new ApiKey
            {
                Id = Guid.NewGuid(),
                UserId = ownerId,
                Name = "rate-limit-test-key",
                KeyHash = ComputeSha256Hash(rawKeySeed),
                Purpose = ApiKeyPurpose.Desktop,
                Scopes = ApiKeyScope.ModelRead,
                IsActive = true,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            });
            await context.SaveChangesAsync();

            using HttpClient anonymousClient = factory.CreateClient();

            // AuthenticationRateLimitOptions.MaxApiKeyExchangeAttemptsPerMinute defaults to 5.
            for (int i = 0; i < 5; i++)
            {
                HttpResponseMessage response = await anonymousClient.PostAsJsonAsync(
                    "/api/auth/api-key/exchange", new { apiKey = rawKeySeed });
                response.StatusCode.Should().Be(HttpStatusCode.OK, $"attempt {i + 1} is within the per-minute limit");
            }

            HttpResponseMessage limitedResponse = await anonymousClient.PostAsJsonAsync(
                "/api/auth/api-key/exchange", new { apiKey = rawKeySeed });
            limitedResponse.StatusCode.Should().Be((HttpStatusCode)429, "the 6th attempt within the same minute must be rate limited");
        }
    }
}
