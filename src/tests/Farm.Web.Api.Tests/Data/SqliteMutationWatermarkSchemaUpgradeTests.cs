using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Data;

public sealed class SqliteMutationWatermarkSchemaUpgradeTests
{
    [Fact]
    public async Task ApplyAsync_ExistingDatabase_IsAdditiveAndIdempotent()
    {
        string databasePath = Path.Join(
            Path.GetTempPath(),
            $"mutation-watermark-upgrade-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Pooling=False";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        Guid taskId = Guid.NewGuid();

        try
        {
            await using (AppDbContext setup = new(options))
            {
                _ = await setup.Database.EnsureCreatedAsync();
                _ = setup.UserTasks.Add(NewTask(taskId));
                _ = await setup.SaveChangesAsync();
                _ = await setup.Database.ExecuteSqlRawAsync("DROP TABLE \"MutationCounters\";");
                _ = await setup.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"UserTasks\" DROP COLUMN \"LastMutationSequence\";");
            }

            await using (AppDbContext upgraded = new(options))
            {
                await SqliteMutationWatermarkSchemaUpgrade.ApplyAsync(
                    upgraded,
                    NullLogger.Instance);
                await SqliteMutationWatermarkSchemaUpgrade.ApplyAsync(
                    upgraded,
                    NullLogger.Instance);

                (await ReadScalarAsync<long>(
                    upgraded,
                    "SELECT \"Value\" FROM \"MutationCounters\" WHERE \"Id\" = 1;"))
                    .Should().Be(0);
                UserTask existing = await upgraded.UserTasks.AsNoTracking().SingleAsync();
                existing.LastMutationSequence.Should().Be(0);
                existing.Title.Should().Be("existing");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    private static async Task<T> ReadScalarAsync<T>(AppDbContext db, string sql)
    {
        DbConnection connection = db.Database.GetDbConnection();
        bool closeConnection = connection.State != System.Data.ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            object result = await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("Expected SQLite scalar query to return a value.");
            return (T)(Convert.ChangeType(
                result,
                typeof(T),
                System.Globalization.CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("Expected SQLite scalar conversion to return a value."));
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static UserTask NewTask(Guid id) => new()
    {
        Id = id,
        Title = "existing",
        TaskType = UserTaskType.ProfileImport,
        Status = UserTaskStatus.Pending,
        Priority = UserTaskPriority.Normal,
        AnchorKind = UserTaskAnchorKind.Now,
        EntityType = "Printer",
        EntityId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
