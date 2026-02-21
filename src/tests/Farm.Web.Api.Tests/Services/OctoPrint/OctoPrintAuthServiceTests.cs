using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.OctoPrint;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.OctoPrint;

public class OctoPrintAuthServiceTests
{
    private static AppDbContext CreateInMemoryContext()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
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

    [Fact]
    public async Task ValidateApiKeyAsync_AllowsWhenRequireDisabled()
    {
        using AppDbContext ctx = CreateInMemoryContext();
        var settingsService = CreateMockSettingsService(new OctoPrintSettings { RequireApiKey = false });
        var repo = new Farm.Infrastructure.Repositories.Api.EfApiKeyRepository(ctx);
        IConfigurationRoot config = new ConfigurationBuilder().Build();
        var svc = new OctoPrintAuthService(settingsService, new NullLogger<OctoPrintAuthService>(), repo, config);

        bool ok = await svc.ValidateApiKeyAsync(null);
        Assert.True(ok);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_GlobalKeyWorks()
    {
        using AppDbContext ctx = CreateInMemoryContext();
        var settingsService = CreateMockSettingsService(new OctoPrintSettings { RequireApiKey = true });
        var repo = new Farm.Infrastructure.Repositories.Api.EfApiKeyRepository(ctx);
        var inMemory = new Dictionary<string, string?> { ["OctoPrint:GlobalApiKey"] = "supersecret" };
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var svc = new OctoPrintAuthService(settingsService, new NullLogger<OctoPrintAuthService>(), repo, config);

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
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw)));
        var ak = new ApiKey { KeyHash = hash, UserId = Guid.NewGuid(), Name = "test" };
        await repo.AddAsync(ak);

        var svc = new OctoPrintAuthService(settingsService, new NullLogger<OctoPrintAuthService>(), repo, config);
        bool ok = await svc.ValidateApiKeyAsync(raw);
        Assert.True(ok);
    }
}
