using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.OctoPrint;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.OctoPrint;

public class OctoPrintAuthServiceTests : IDisposable
{
    private readonly List<SqliteConnection> _connectionsToDispose = [];

    public void Dispose()
    {
        foreach (SqliteConnection connection in _connectionsToDispose)
        {
            connection.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private AppDbContext CreateInMemoryContext()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        _connectionsToDispose.Add(conn);
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;
        var ctx = new AppDbContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private static ISettingsService CreateMockSettingsService(OctoPrintSettings settings)
    {
        var mock = new Mock<ISettingsService>();
        mock.Setup(s => s.Get<OctoPrintSettings>()).Returns(settings);
        return mock.Object;
    }

    private static IUsersRepository CreateMockUsersRepository() => Mock.Of<IUsersRepository>();

    [Fact]
    public async Task ValidateApiKeyAsync_AllowsWhenRequireDisabled()
    {
        using AppDbContext ctx = CreateInMemoryContext();
        var settingsService = CreateMockSettingsService(new OctoPrintSettings { RequireApiKey = false });
        var repo = new Farm.Infrastructure.Repositories.Api.EfApiKeyRepository(ctx);
        IConfigurationRoot config = new ConfigurationBuilder().Build();
        var svc = new OctoPrintAuthService(settingsService, new NullLogger<OctoPrintAuthService>(), repo, CreateMockUsersRepository(), config);

        bool ok = await svc.ValidateApiKeyAsync(null);
        Assert.True(ok);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_RequiredForAnonymous_RejectsMissingKeyWhenSettingDisabled()
    {
        using AppDbContext ctx = CreateInMemoryContext();
        var settingsService = CreateMockSettingsService(new OctoPrintSettings { RequireApiKey = false });
        var repo = new Farm.Infrastructure.Repositories.Api.EfApiKeyRepository(ctx);
        IConfigurationRoot config = new ConfigurationBuilder().Build();
        var svc = new OctoPrintAuthService(settingsService, new NullLogger<OctoPrintAuthService>(), repo, CreateMockUsersRepository(), config);

        bool ok = await svc.ValidateApiKeyAsync(null, requireValidKey: true);

        Assert.False(ok);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_GlobalKeyWorks()
    {
        using AppDbContext ctx = CreateInMemoryContext();
        var settingsService = CreateMockSettingsService(new OctoPrintSettings { RequireApiKey = true });
        var repo = new Farm.Infrastructure.Repositories.Api.EfApiKeyRepository(ctx);
        var inMemory = new Dictionary<string, string?> { ["OctoPrint:GlobalApiKey"] = "supersecret" };
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var svc = new OctoPrintAuthService(settingsService, new NullLogger<OctoPrintAuthService>(), repo, CreateMockUsersRepository(), config);

        bool ok = await svc.ValidateApiKeyAsync("supersecret");
        Assert.True(ok);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_DbStoredKeyWorks()
    {
        using AppDbContext ctx = CreateInMemoryContext();
        var settingsService = CreateMockSettingsService(new OctoPrintSettings { RequireApiKey = true });
        var repo = new Farm.Infrastructure.Repositories.Api.EfApiKeyRepository(ctx);
        IConfigurationRoot config = new ConfigurationBuilder().Build();
        // create key
        string raw = "mygeneratedkey";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        var ak = new ApiKey { KeyHash = hash, UserId = Guid.NewGuid(), Name = "test" };
        await repo.AddAsync(ak);

        var svc = new OctoPrintAuthService(settingsService, new NullLogger<OctoPrintAuthService>(), repo, CreateMockUsersRepository(), config);
        bool ok = await svc.ValidateApiKeyAsync(raw);
        Assert.True(ok);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_DesktopPurposeKey_IsRejected()
    {
        using AppDbContext ctx = CreateInMemoryContext();
        var settingsService = CreateMockSettingsService(new OctoPrintSettings { RequireApiKey = true });
        var repo = new Farm.Infrastructure.Repositories.Api.EfApiKeyRepository(ctx);
        IConfigurationRoot config = new ConfigurationBuilder().Build();
        string raw = "desktopscopedkey";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        var ak = new ApiKey
        {
            KeyHash = hash,
            UserId = Guid.NewGuid(),
            Name = "desktop",
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = ApiKeyScope.ModelRead,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        };
        await repo.AddAsync(ak);

        var svc = new OctoPrintAuthService(settingsService, new NullLogger<OctoPrintAuthService>(), repo, CreateMockUsersRepository(), config);
        bool ok = await svc.ValidateApiKeyAsync(raw);

        Assert.False(ok);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_ExpiredOctoPrintKey_IsRejected()
    {
        using AppDbContext ctx = CreateInMemoryContext();
        var settingsService = CreateMockSettingsService(new OctoPrintSettings { RequireApiKey = true });
        var repo = new Farm.Infrastructure.Repositories.Api.EfApiKeyRepository(ctx);
        IConfigurationRoot config = new ConfigurationBuilder().Build();
        string raw = "expiredkey";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        var ak = new ApiKey
        {
            KeyHash = hash,
            UserId = Guid.NewGuid(),
            Name = "expired",
            Purpose = ApiKeyPurpose.OctoPrint,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5),
        };
        await repo.AddAsync(ak);

        var svc = new OctoPrintAuthService(settingsService, new NullLogger<OctoPrintAuthService>(), repo, CreateMockUsersRepository(), config);
        bool ok = await svc.ValidateApiKeyAsync(raw);

        Assert.False(ok);
    }
}
