using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Server.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _dbPath;

    public CustomWebApplicationFactory()
    {
        var dbFile = $"farm_test_{Guid.NewGuid():N}.db";
        _dbPath = Path.Combine(Path.GetTempPath(), dbFile);
        TryDelete();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((context, config) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={_dbPath}",
                // Avoid running EF Core Migrate() in tests when using ad-hoc SQLite files; rely on startup safety + EnsureCreated
                ["DISABLE_EF_MIGRATIONS"] = "true"
            };
            config.AddInMemoryCollection(dict!);
        });
        return base.CreateHost(builder);
    }

    private void TryDelete()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    public new void Dispose()
    {
        base.Dispose();
        TryDelete();
    }
}
