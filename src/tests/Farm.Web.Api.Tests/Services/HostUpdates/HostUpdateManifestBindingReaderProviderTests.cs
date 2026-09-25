using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Services.HostUpdates;

/// <summary>
/// Proves the recovery CLI's read-only manifest-binding reader (issue #3050) against the real
/// schema on every supported provider. PostgreSQL and SQL Server run in the CI provider job,
/// which migrates both databases and sets the connection variables.
/// </summary>
public sealed class HostUpdateManifestBindingReaderProviderTests
{
    private const string PostgresConnectionVariable = "PFARM_TEST_POSTGRES_CONN";
    private const string SqlServerConnectionVariable = "PFARM_TEST_SQLSERVER_CONN";
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Sqlite_reads_the_binding_without_modifying_the_database()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pf-binding-{Guid.NewGuid():N}.db");
        string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            await using (var context = new AppDbContext(options))
            {
                _ = await context.Database.EnsureCreatedAsync();
            }

            var database = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = connectionString };
            string releaseId = "stable:" + Guid.NewGuid().ToString("N");
            await SeedAsync(options, releaseId);
            byte[] before = await File.ReadAllBytesAsync(path);
            byte[]? walBefore = File.Exists(path + "-wal") ? await File.ReadAllBytesAsync(path + "-wal") : null;
            var reader = new ReadOnlyHostUpdateManifestBindingReader(database);

            string observed = await reader.ReadAsync(releaseId, CancellationToken.None);
            string absent = await reader.ReadAsync("stable:0.0.0", CancellationToken.None);

            Assert.Equal(Digest, observed);
            Assert.Equal(ReadOnlyHostUpdateManifestBindingReader.NoBinding, absent);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));

            // A WAL-mode database may need SQLite's own sidecars to be opened, but the read never
            // appends a frame: the write-ahead log is unchanged (or empty when SQLite created it).
            byte[] walAfter = File.Exists(path + "-wal") ? await File.ReadAllBytesAsync(path + "-wal") : [];
            Assert.Equal(walBefore ?? [], walAfter);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Sqlite_missing_database_throws_instead_of_reporting_no_binding()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pf-binding-missing-{Guid.NewGuid():N}.db");
        var database = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = "Data Source=" + path };

        _ = await Assert.ThrowsAsync<SqliteException>(
            () => new ReadOnlyHostUpdateManifestBindingReader(database).ReadAsync("stable:1.2.3", CancellationToken.None));
        Assert.False(File.Exists(path), "a read-only open must never create the database");
    }

    [Fact]
    public async Task Invalid_binding_json_throws_instead_of_reporting_no_binding()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pf-binding-invalid-{Guid.NewGuid():N}.db");
        string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            string releaseId = "stable:" + Guid.NewGuid().ToString("N");
            await using (var context = new AppDbContext(options))
            {
                _ = await context.Database.EnsureCreatedAsync();
                _ = context.AppSettingsEntities.Add(new AppSettingsEntity { Key = VerifiedReleaseManifestBindingStore.KeyFor(releaseId), SettingsJson = "{", UpdatedAt = DateTime.UtcNow });
                _ = await context.SaveChangesAsync();
            }

            var database = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = connectionString };

            _ = await Assert.ThrowsAsync<InvalidDataException>(
                () => new ReadOnlyHostUpdateManifestBindingReader(database).ReadAsync(releaseId, CancellationToken.None));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PostgreSql_reads_the_binding_read_only()
    {
        string? connectionString = Environment.GetEnvironmentVariable(PostgresConnectionVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            $"PostgreSQL provider verification did not run: set {PostgresConnectionVariable}.");

        await AssertProviderReadAsync(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options,
            new DatabaseProviderConfiguration { Provider = "postgres", ConnectionString = connectionString! });
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SqlServer_reads_the_binding_read_only()
    {
        string? connectionString = Environment.GetEnvironmentVariable(SqlServerConnectionVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            $"SQL Server provider verification did not run: set {SqlServerConnectionVariable}.");

        await AssertProviderReadAsync(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options,
            new DatabaseProviderConfiguration { Provider = "sqlserver", ConnectionString = connectionString! });
    }

    private static async Task AssertProviderReadAsync(DbContextOptions<AppDbContext> options, DatabaseProviderConfiguration database)
    {
        string releaseId = "stable:" + Guid.NewGuid().ToString("N");
        await SeedAsync(options, releaseId);
        try
        {
            var reader = new ReadOnlyHostUpdateManifestBindingReader(database);

            Assert.Equal(Digest, await reader.ReadAsync(releaseId, CancellationToken.None));
            Assert.Equal(ReadOnlyHostUpdateManifestBindingReader.NoBinding, await reader.ReadAsync(releaseId + "-absent", CancellationToken.None));
        }
        finally
        {
            await using var context = new AppDbContext(options);
            string key = VerifiedReleaseManifestBindingStore.KeyFor(releaseId);
            _ = await context.AppSettingsEntities.Where(entity => entity.Key == key).ExecuteDeleteAsync();
        }
    }

    private static void DeleteDatabase(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(file);
        }
    }

    private static async Task SeedAsync(DbContextOptions<AppDbContext> options, string releaseId)
    {
        await using var context = new AppDbContext(options);
        _ = context.AppSettingsEntities.Add(new AppSettingsEntity
        {
            Key = VerifiedReleaseManifestBindingStore.KeyFor(releaseId),
            SettingsJson = JsonSerializer.Serialize(new { ReleaseId = releaseId, ManifestDigest = Digest }),
            UpdatedAt = DateTime.UtcNow,
        });
        _ = await context.SaveChangesAsync();
    }
}
