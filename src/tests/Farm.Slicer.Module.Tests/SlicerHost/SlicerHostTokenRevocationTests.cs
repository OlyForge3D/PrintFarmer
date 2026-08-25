extern alias SlicerHost;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Slicers.OrcaSlicer.v2_4_0;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SlicerHostProgram = SlicerHost::Program;

namespace Farm.Slicer.Module.Tests.SlicerHost;

/// <summary>
/// Verifies that the standalone slicer-host (<c>Farm.Slicer.Host</c>) honours forced token
/// revocation (#1469). The naive fix attempted in #1460 was reverted because
/// <c>ITokenRevocationService</c> was never registered in this host - resolving it with
/// <c>GetService</c> silently returned null and the revocation check no-oped. The existing
/// cross-host tests (<c>CrossHostJwtAcceptanceTests</c>) reconstruct
/// <see cref="TokenValidationParameters"/> rather than hosting <c>Farm.Slicer.Host</c> itself, so
/// they would not catch a regression here. This test hosts the real production entry point via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, exactly like
/// <c>StandaloneSlicerHostModelDownloadSecurityTests</c>.
/// </summary>
public class SlicerHostTokenRevocationTests(SlicerHostTokenRevocationApplicationFactory factory)
    : IClassFixture<SlicerHostTokenRevocationApplicationFactory>
{
    [Fact]
    public async Task RevokedToken_ViaAllTokensMarker_IsRejectedOnSlicerRoute()
    {
        DateTime tokenIssuedAt = DateTime.UtcNow.AddMinutes(-1);
        (Guid userId, string token) = await factory.CreateUserAndTokenAsync(tokenIssuedAt);

        await factory.RevokeAllTokensForUserAsync(userId, revokedAt: DateTime.UtcNow);

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/3d-models/file/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonRevokedToken_IsNotRejectedAtTheAuthenticationLayer()
    {
        (_, string token) = await factory.CreateUserAndTokenAsync(DateTime.UtcNow.AddMinutes(-1));

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/3d-models/file/{Guid.NewGuid()}");

        // The model doesn't exist, but that's a 404 from the controller - proving the request got
        // past JWT bearer authentication rather than being rejected as revoked (401).
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// Hosts the production standalone slicer entry point with a real SQLite-backed
/// <see cref="AppDbContext"/> so <c>RevokedTokens</c> lookups exercise the same code path
/// <c>OnTokenValidated</c> uses in production.
/// </summary>
public sealed class SlicerHostTokenRevocationApplicationFactory : WebApplicationFactory<SlicerHostProgram>
{
    private const string JwtKey =
        "PrintFarmerSlicerHostTokenRevocationTestsSigningKey-1234567890";
    private const string JwtIssuer = "PrintFarmer";
    private const string JwtAudience = "PrintFarmer";

    private readonly string _testRoot;
    private readonly string _databasePath;

    public SlicerHostTokenRevocationApplicationFactory()
    {
        _testRoot = Path.Join(Path.GetTempPath(), $"slicer-host-token-revocation-{Guid.NewGuid():N}");
        _databasePath = Path.Join(_testRoot, "slicer-host.db");
        Directory.CreateDirectory(_testRoot);

        // Forces the OrcaSlicer plugin assembly to load into the test process so the slicer host's
        // "zero registered slicer libraries" startup sanity check (#578) doesn't reject the host,
        // mirroring StandaloneSlicerHostModelDownloadSecurityTests.
        _ = typeof(OrcaSlicerLibrary_v2_4_0).Assembly.GetName();
    }

    public async Task<(Guid UserId, string Token)> CreateUserAndTokenAsync(DateTime issuedAt)
    {
        Guid userId = Guid.NewGuid();

        await using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // RevokedTokens.UserId has a foreign key to Users, so a real row must exist before a
            // revocation marker can reference this user.
            dbContext.Users.Add(new User
            {
                Id = userId,
                Username = $"slicer-host-revocation-test-{userId:N}",
                Email = $"{userId:N}@example.test",
                PasswordHash = "not-a-real-hash",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await dbContext.SaveChangesAsync();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, "slicer-host-revocation-test-user"),
            ]),
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = issuedAt.AddMinutes(15),
            Issuer = JwtIssuer,
            Audience = JwtAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
                SecurityAlgorithms.HmacSha256),
        };

        return (userId, new JsonWebTokenHandler().CreateToken(descriptor));
    }

    public async Task RevokeAllTokensForUserAsync(Guid userId, DateTime revokedAt)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Mirrors TokenRevocationService.RevokeAllUserTokensAsync's "ALL_TOKENS_" marker: any JWT
        // issued for this user before revokedAt is treated as revoked, without needing to know the
        // specific token hash.
        dbContext.RevokedTokens.Add(new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenHash = $"ALL_TOKENS_{userId}_{revokedAt.Ticks}",
            UserId = userId,
            RevokedAt = revokedAt,
            RevokedByUserId = null,
            Reason = "Test: all tokens revoked",
            ExpiresAt = revokedAt.AddDays(30),
        });
        _ = await dbContext.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _ = builder.UseEnvironment("Testing");
        _ = builder.UseSetting("Jwt:Key", JwtKey);
        _ = builder.UseSetting("Jwt:Issuer", JwtIssuer);
        _ = builder.UseSetting("Jwt:Audience", JwtAudience);
        _ = builder.UseSetting("DB_PROVIDER", "sqlite");
        _ = builder.UseSetting(
            "ConnectionStrings:Default",
            $"Data Source={_databasePath};Pooling=False");
        _ = builder.UseSetting("STORAGE_PATHS:UPLOADS", Path.Join(_testRoot, "models"));
        _ = builder.UseSetting("STORAGE_PATHS:GCODE", Path.Join(_testRoot, "gcode"));
        _ = builder.UseSetting("WorkerAuth:SharedKey", "slicer-host-token-revocation-test-worker-key");
        _ = builder.UseSetting("ArtifactStorage:RootPath", Path.Join(_testRoot, "artifacts"));
        _ = builder.UseSetting("ArtifactStorage:EnableStorageAlerts", "false");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // AppDbContext's schema must exist before the host starts: SlicerDbInitializationHostedService
        // runs SlicerDbContext migrations against the same SQLite file as soon as the host starts, and
        // EnsureCreated() is a no-op once the database file already contains any tables. Building a
        // standalone AppDbContext here (outside the host's DI container) guarantees ordering without
        // depending on the production migration pipeline, which the slicer host deliberately does not
        // run for AppDbContext (the main API owns those migrations).
        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        _ = optionsBuilder.UseSqlite($"Data Source={_databasePath};Pooling=False");
        using (AppDbContext dbContext = new(optionsBuilder.Options))
        {
            _ = dbContext.Database.EnsureCreated();
        }

        return base.CreateHost(builder);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
