using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.OctoPrint;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Services.OctoPrint;

public class OctoPrintAuthServiceTests
{
    private static AppDbContext CreateInMemoryContext()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;
        var ctx = new AppDbContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public async Task ValidateApiKeyAsync_AllowsWhenRequireDisabled()
    {
        using var ctx = CreateInMemoryContext();
        var settings = Options.Create(new OctoPrintSettings { RequireApiKey = false });
        var repo = new Farm.Web.Api.Data.Repositories.EfApiKeyRepositoryAdapter(ctx);
        var config = new ConfigurationBuilder().Build();
        var svc = new OctoPrintAuthService(settings, new NullLogger<OctoPrintAuthService>(), repo, config);

        var ok = await svc.ValidateApiKeyAsync(null);
        Assert.True(ok);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_GlobalKeyWorks()
    {
        using var ctx = CreateInMemoryContext();
        var settings = Options.Create(new OctoPrintSettings { RequireApiKey = true });
        var repo = new Farm.Web.Api.Data.Repositories.EfApiKeyRepositoryAdapter(ctx);
        var inMemory = new Dictionary<string, string> { ["OctoPrint:GlobalApiKey"] = "supersecret" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var svc = new OctoPrintAuthService(settings, new NullLogger<OctoPrintAuthService>(), repo, config);

        var ok = await svc.ValidateApiKeyAsync("supersecret");
        Assert.True(ok);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_DbStoredKeyWorks()
    {
        using var ctx = CreateInMemoryContext();
        var settings = Options.Create(new OctoPrintSettings { RequireApiKey = true });
        var repo = new Farm.Web.Api.Data.Repositories.EfApiKeyRepositoryAdapter(ctx);
        var config = new ConfigurationBuilder().Build();
        // create key
        var raw = "mygeneratedkey";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw)));
        var ak = new ApiKey { KeyHash = hash, UserId = Guid.NewGuid(), Name = "test" };
        await repo.AddAsync(ak);

        var svc = new OctoPrintAuthService(settings, new NullLogger<OctoPrintAuthService>(), repo, config);
        var ok = await svc.ValidateApiKeyAsync(raw);
        Assert.True(ok);
    }
}
