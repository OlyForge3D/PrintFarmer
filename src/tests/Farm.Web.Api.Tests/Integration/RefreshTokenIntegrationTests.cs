using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

public sealed class RefreshTokenTestFactory : CustomWebApplicationFactory
{
    public RefreshTokenTestFactory()
        : base(new Dictionary<string, string?>
        {
            ["RateLimiting:Authentication:MaxLoginAttemptsPerMinute"] = "100"
        })
    {
    }
}

public sealed class RefreshTokenIntegrationTests : IClassFixture<RefreshTokenTestFactory>, IAsyncLifetime
{
    private readonly RefreshTokenTestFactory _factory;

    public RefreshTokenIntegrationTests(RefreshTokenTestFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsRefreshTokenAndExpiry()
    {
        User user = await CreateUserAsync("refresh-login", "refresh-login@test.com");
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { usernameOrEmail = user.Username, password = "TestPassword123!" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string responseBody = await response.Content.ReadAsStringAsync();
        using JsonDocument payload = JsonDocument.Parse(responseBody);
        JsonElement root = payload.RootElement;
        root.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("refreshTokenExpires").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddDays(29));
        root.GetProperty("expiresAt").GetDateTime().Should().BeAfter(DateTime.UtcNow);
        AuthenticationResult result = JsonSerializer.Deserialize<AuthenticationResult>(
            responseBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        result.Success.Should().BeTrue();
        result.RefreshToken.Should().NotBeNullOrWhiteSpace();
        result.RefreshTokenExpires.Should().BeAfter(DateTime.UtcNow.AddDays(29));

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        RefreshToken stored = await context.RefreshTokens.SingleAsync(token => token.UserId == user.Id);
        stored.Token.Should().NotBe(result.RefreshToken);
        stored.Token.Should().HaveLength(64);
    }

    [Fact]
    public async Task Refresh_WithActiveToken_RotatesTokenAndReturnsAccessSession()
    {
        User user = await CreateUserAsync("refresh-rotation", "refresh-rotation@test.com");
        string refreshToken = await CreateRefreshTokenAsync(user.Id);
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AuthenticationResult result = (await response.Content.ReadFromJsonAsync<AuthenticationResult>())!;
        result.Success.Should().BeTrue();
        result.Token.Should().NotBeNullOrWhiteSpace();
        result.RefreshToken.Should().NotBeNullOrWhiteSpace().And.NotBe(refreshToken);
        result.RefreshTokenExpires.Should().BeAfter(DateTime.UtcNow.AddDays(29));

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<RefreshToken> rows = await context.RefreshTokens.Where(token => token.UserId == user.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(token => token.IsRevoked && token.ReplacedByToken == rows.Single(item => !item.IsRevoked).Token);
        rows.Single(token => !token.IsRevoked).Token.Should().NotBe(result.RefreshToken);
    }

    [Fact]
    public async Task Refresh_ReusedRotatedToken_RevokesAllActiveTokens()
    {
        User user = await CreateUserAsync("refresh-reuse", "refresh-reuse@test.com");
        string refreshToken = await CreateRefreshTokenAsync(user.Id);
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage firstResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AuthenticationResult firstResult = (await firstResponse.Content.ReadFromJsonAsync<AuthenticationResult>())!;

        using HttpResponseMessage replayResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        replayResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<RefreshToken> userTokens = await context.RefreshTokens.Where(token => token.UserId == user.Id).ToListAsync();
        userTokens.Should().HaveCount(2);
        userTokens.Should().OnlyContain(token => token.IsRevoked);
        firstResult.RefreshToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Refresh_WithExpiredToken_ReturnsUnauthorized()
    {
        User user = await CreateUserAsync("refresh-expired", "refresh-expired@test.com");
        string refreshToken = await CreateRefreshTokenAsync(user.Id);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            RefreshToken row = await context.RefreshTokens.SingleAsync(token => token.UserId == user.Id);
            row.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await context.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AuthenticationResult result = (await response.Content.ReadFromJsonAsync<AuthenticationResult>())!;
        result.Error.Should().Be("Invalid refresh token.");
    }

    [Fact]
    public async Task Refresh_WithRevokedToken_ReturnsUnauthorized()
    {
        User user = await CreateUserAsync("refresh-revoked", "refresh-revoked@test.com");
        string refreshToken = await CreateRefreshTokenAsync(user.Id);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            RefreshToken row = await context.RefreshTokens.SingleAsync(token => token.UserId == user.Id);
            row.IsRevoked = true;
            row.RevokedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_WithMissingBody_ReturnsBadRequest()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/auth/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refresh_WithUnknownToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_') });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_WithDisabledUser_ReturnsUnauthorized()
    {
        User user = await CreateUserAsync("refresh-disabled", "refresh-disabled@test.com");
        string refreshToken = await CreateRefreshTokenAsync(user.Id);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User persisted = await context.Users.SingleAsync(candidate => candidate.Id == user.Id);
            persisted.IsActive = false;
            await context.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithRefreshToken_RevokesOnlyCallersToken()
    {
        User user = await CreateUserAsync("refresh-logout", "refresh-logout@test.com");
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { usernameOrEmail = user.Username, password = "TestPassword123!" });
        AuthenticationResult loginResult = (await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Token);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/logout",
            new { refreshToken = loginResult.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        RefreshToken row = await context.RefreshTokens.SingleAsync(token => token.UserId == user.Id);
        row.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_WithLockedUser_ReturnsUnauthorized()
    {
        User user = await CreateUserAsync("refresh-locked", "refresh-locked@test.com");
        string refreshToken = await CreateRefreshTokenAsync(user.Id);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User persisted = await context.Users.SingleAsync(candidate => candidate.Id == user.Id);
            persisted.LockoutEnd = DateTime.UtcNow.AddMinutes(10);
            await context.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_WithDeletedUser_ReturnsUnauthorized()
    {
        User user = await CreateUserAsync("refresh-deleted", "refresh-deleted@test.com");
        string refreshToken = await CreateRefreshTokenAsync(user.Id);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User persisted = await context.Users.SingleAsync(candidate => candidate.Id == user.Id);
            context.Users.Remove(persisted);
            await context.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithoutBody_AndGetLogout_RemainSuccessful()
    {
        User user = await CreateUserAsync("refresh-logout-empty", "refresh-logout-empty@test.com");
        using IServiceScope scope = _factory.Services.CreateScope();
        IAuthenticationService authenticationService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        string accessToken = await authenticationService.GenerateJwtTokenAsync(user);
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage postResponse = await client.PostAsync("/api/auth/logout", content: null);
        using HttpResponseMessage getResponse = await client.GetAsync("/api/auth/logout");

        postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_AfterRevokeAll_ReturnsUnauthorized()
    {
        User user = await CreateUserAsync("refresh-revoke-all", "refresh-revoke-all@test.com");
        string refreshToken = await CreateRefreshTokenAsync(user.Id);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ITokenRevocationService revocationService = scope.ServiceProvider.GetRequiredService<ITokenRevocationService>();
            _ = await revocationService.RevokeAllUserTokensAsync(user.Id, user.Id, "test revoke all");
        }

        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<string> CreateRefreshTokenAsync(Guid userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IRefreshTokenService refreshTokenService = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        (string token, _) = await refreshTokenService.CreateAsync(userId, "127.0.0.1");
        return token;
    }

    private async Task<User> CreateUserAsync(string username, string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            PasswordHash = passwordHashing.HashPassword("TestPassword123!"),
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }
}
