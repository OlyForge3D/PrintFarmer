using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Issue #3126: on a fresh host the pre-migration host-update reads (drain, manifest binding,
/// physical inventory) run before any table exists. A missing table is "no work" only when the
/// context's migration history table is provably absent too; anything else stays a failure.
/// </summary>
public sealed class HostUpdateFreshSchemaTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pf-fresh-schema-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string file in new[] { _path, _path + "-wal", _path + "-shm", _path + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();

    private DbContextOptions<AppDbContext> Options => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString).Options;

    [Fact]
    public async Task A_database_without_history_or_tables_is_proven_never_migrated()
    {
        await CreateEmptyDatabaseAsync();
        await using var db = new AppDbContext(Options);

        (await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(db, [typeof(PrintJob)], CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Existing_tables_without_history_are_not_a_fresh_host()
    {
        await using (var seed = new AppDbContext(Options))
        {
            _ = await seed.Database.EnsureCreatedAsync();
        }

        await using var db = new AppDbContext(Options);

        (await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(db, [typeof(PrintJob)], CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_history_table_without_the_work_tables_is_damaged_not_fresh_and_the_drain_fails()
    {
        await CreateEmptyDatabaseAsync(withHistory: true);
        await using var db = new AppDbContext(Options);

        (await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(db, [typeof(PrintJob)], CancellationToken.None)).Should().BeFalse();
        Func<Task> drain = async () => await new DbActiveWorkObservationPort(db).CountActiveAsync(CancellationToken.None);
        await drain.Should().ThrowAsync<SqliteException>("a missing table next to migration history is never read as no work");
    }

    [Fact]
    public async Task Drain_on_a_fresh_host_observes_no_active_work()
    {
        await CreateEmptyDatabaseAsync();
        await using var db = new AppDbContext(Options);

        (await new DbActiveWorkObservationPort(db).CountActiveAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Physical_inventory_on_a_fresh_host_is_empty_and_on_a_damaged_schema_throws()
    {
        await CreateEmptyDatabaseAsync();
        await using (var fresh = new AppDbContext(Options))
        {
            HostUpdatePrinterCommandInventory inventory = await new DbHostUpdatePrinterCommandInventoryReader(fresh).ReadAsync(CancellationToken.None);
            inventory.Printers.Should().BeEmpty();
            inventory.UncertainOutcomeCount.Should().Be(0);
        }

        await CreateHistoryTableAsync();
        await using var damaged = new AppDbContext(Options);
        Func<Task> read = async () => await new DbHostUpdatePrinterCommandInventoryReader(damaged).ReadAsync(CancellationToken.None);
        await read.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Manifest_binding_on_a_fresh_host_is_no_binding_and_on_a_damaged_schema_throws()
    {
        await CreateEmptyDatabaseAsync();
        var database = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = ConnectionString };
        var reader = new ReadOnlyHostUpdateManifestBindingReader(database);

        (await reader.ReadAsync("stable:1.2.3", CancellationToken.None)).Should().Be(ReadOnlyHostUpdateManifestBindingReader.NoBinding);

        await CreateHistoryTableAsync();
        Func<Task> read = () => reader.ReadAsync("stable:1.2.3", CancellationToken.None);
        await read.Should().ThrowAsync<SqliteException>();
    }

    private async Task CreateEmptyDatabaseAsync(bool withHistory = false)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE pf_probe (x INTEGER); DROP TABLE pf_probe;";
        _ = await command.ExecuteNonQueryAsync();
        if (withHistory)
        {
            await connection.CloseAsync();
            await CreateHistoryTableAsync();
        }
    }

    private async Task CreateHistoryTableAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);";
        _ = await command.ExecuteNonQueryAsync();
    }
}
