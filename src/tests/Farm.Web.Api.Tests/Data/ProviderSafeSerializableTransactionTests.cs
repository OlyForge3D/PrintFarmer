using System.Data;
using Farm.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Data;

public sealed class ProviderSafeSerializableTransactionTests
{
    [Fact]
    public async Task DisposeAsync_OwnedSqliteConnection_RollsBackAndClosesConnection()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"printfarmer-transaction-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath}";
        try
        {
            DbContextOptions<AppDbContext> options =
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connectionString)
                    .Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                _ = await db.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE TransactionProbe (Id INTEGER PRIMARY KEY);");
                db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);

                await using (ProviderSafeSerializableTransactionScope transaction =
                             await ProviderSafeSerializableTransaction.BeginAsync(
                                 db,
                                 CancellationToken.None))
                {
                    _ = await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO TransactionProbe (Id) VALUES (1);");
                }

                db.Database.CurrentTransaction.Should().BeNull();
                db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
            }

            await using var verificationDb = new AppDbContext(options);
            await verificationDb.Database.OpenConnectionAsync();
            await using var command =
                verificationDb.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM TransactionProbe;";
            object? count = await command.ExecuteScalarAsync();
            Convert.ToInt32(count).Should().Be(0);
        }
        finally
        {
            // Scope the pool clear to this test's own connection string instead of
            // calling the process-wide ClearAllPools(), which would disrupt other
            // tests' pooled SQLite connections running concurrently now that this
            // assembly is no longer fully serialized.
            using (var pooledConnection = new SqliteConnection(connectionString))
            {
                SqliteConnection.ClearPool(pooledConnection);
            }

            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_ExternalSqliteConnection_PreservesConnectionAndReleasesTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await using (ProviderSafeSerializableTransactionScope transaction =
                     await ProviderSafeSerializableTransaction.BeginAsync(
                         db,
                         CancellationToken.None))
        {
            db.Database.CurrentTransaction.Should().NotBeNull();
        }

        db.Database.CurrentTransaction.Should().BeNull();
        connection.State.Should().Be(ConnectionState.Open);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite has no asynchronous transaction overload.
        await using SqliteTransaction subsequentTransaction =
            connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
#pragma warning restore CA1849
        await subsequentTransaction.RollbackAsync();
    }

    [Fact]
    public async Task BeginAsync_NonRelationalProvider_RejectsTransaction()
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        await using var db = new AppDbContext(options);

        Func<Task> act = async () =>
        {
            await using ProviderSafeSerializableTransactionScope _ =
                await ProviderSafeSerializableTransaction.BeginAsync(
                    db,
                    CancellationToken.None);
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*relational database*");
    }
}
