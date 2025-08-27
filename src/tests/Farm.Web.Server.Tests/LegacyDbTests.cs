using System.Data;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Server.Tests;

public class LegacyDbTests
{
    private static string CreateLegacyDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"farm_legacy_{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Legacy schema without Backend column
            cmd.CommandText = @"CREATE TABLE Printers (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                ServerUrl TEXT,
                OriginalServerUrl TEXT,
                IpAddress TEXT NULL,
                Notes TEXT NULL,
                ApiKey TEXT NULL,
                ManufacturerId TEXT NULL,
                ModelId TEXT NULL,
                DateAcquired TEXT NULL
            );";
            cmd.ExecuteNonQuery();
            // Seed a row with null Backend (column doesn't exist); app should add column + backfill to 0
            cmd.CommandText = @"INSERT INTO Printers (Id, Name, ServerUrl) VALUES ($id, 'legacy', 'http://localhost:7125');";
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    private static string CreateDbWithNullBackend()
    {
        var path = Path.Combine(Path.GetTempPath(), $"farm_legacy_null_{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Schema with Backend column present but allowing NULL
            cmd.CommandText = @"CREATE TABLE Printers (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                ServerUrl TEXT,
                OriginalServerUrl TEXT,
                IpAddress TEXT NULL,
                Notes TEXT NULL,
                Backend INTEGER NULL,
                ApiKey TEXT NULL,
                ManufacturerId TEXT NULL,
                ModelId TEXT NULL,
                DateAcquired TEXT NULL
            );";
            cmd.ExecuteNonQuery();
            // Insert a row without specifying Backend (NULL)
            cmd.CommandText = @"INSERT INTO Printers (Id, Name, ServerUrl, Backend) VALUES ($id, 'legacy-null', 'http://localhost:7125', NULL);";
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    private sealed class LegacyFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath;
        public LegacyFactory(string dbPath) { _dbPath = dbPath; }
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
            {
                var dict = new Dictionary<string, string?>
                {
            ["ConnectionStrings:Default"] = $"Data Source={_dbPath}",
            ["DISABLE_EF_MIGRATIONS"] = "true"
                };
                config.AddInMemoryCollection(dict!);
            });
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Get_printers_should_not_500_when_backend_missing_or_null()
    {
        var dbPath = CreateLegacyDb();
        try
        {
            await using var factory = new LegacyFactory(dbPath);
            var client = factory.CreateClient();
            var resp = await client.GetAsync("/api/printers");
            resp.IsSuccessStatusCode.Should().BeTrue();
            var list = await resp.Content.ReadFromJsonAsync<List<Farm.Web.Shared.PrinterDto>>();
            list.Should().NotBeNull();
            list!.Count.Should().BeGreaterThan(0);
            // Ensure backend deserializes to a valid enum (Moonraker=0) for legacy row
            list[0].Backend.Should().Be(Farm.Web.Shared.PrinterBackend.Moonraker);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task Get_printers_should_not_500_when_backend_column_exists_but_is_null()
    {
        var dbPath = CreateDbWithNullBackend();
        try
        {
            await using var factory = new LegacyFactory(dbPath);
            var client = factory.CreateClient();
            var resp = await client.GetAsync("/api/printers");
            resp.IsSuccessStatusCode.Should().BeTrue();
            var list = await resp.Content.ReadFromJsonAsync<List<Farm.Web.Shared.PrinterDto>>();
            list.Should().NotBeNull();
            list!.Count.Should().BeGreaterThan(0);
            list[0].Backend.Should().Be(Farm.Web.Shared.PrinterBackend.Moonraker);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}
