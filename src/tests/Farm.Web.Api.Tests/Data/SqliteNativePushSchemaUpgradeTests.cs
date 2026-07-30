using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Web.Api.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Data;

/// <summary>
/// Integration coverage for additive native-push upgrades on existing SQLite databases.
/// </summary>
public sealed class SqliteNativePushSchemaUpgradeTests
{
    [Fact]
    public async Task ApplyAsync_PreRegistrationVersionDatabase_PreservesAndUpdatesExistingRegistration()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"native-push-sqlite-upgrade-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Pooling=False";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        Guid userId = Guid.NewGuid();
        Guid tokenId = Guid.NewGuid();

        try
        {
            await using (AppDbContext setup = new(options))
            {
                _ = await setup.Database.EnsureCreatedAsync();
                setup.Users.Add(new User
                {
                    Id = userId,
                    Username = $"sqlite-upgrade-{userId:N}",
                    Email = $"sqlite-upgrade-{userId:N}@test.local",
                    PasswordHash = "x",
                });
                setup.NotificationPreferences.Add(new NotificationPreferences
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = userId,
                    EnablePushNotifications = true,
                    PushOnPrinterOffline = false,
                    AttentionPushCategoryPreferencesJson = "{\"offline\":false}",
                });
                setup.DeviceTokens.Add(new DeviceToken
                {
                    Id = tokenId,
                    UserId = userId,
                    RegistrationVersion = 7,
                    InstallationId = "Install-A",
                    Token = new string('a', 64),
                    Platform = "ios",
                    Environment = "production",
                    AppBundleId = "com.example.app",
                    CreatedAt = DateTime.UtcNow.AddDays(-1),
                    LastUsedAt = DateTime.UtcNow.AddHours(-1),
                    IsActive = true,
                });
                await setup.SaveChangesAsync();

                _ = await setup.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"DeviceTokens\" DROP COLUMN \"RegistrationVersion\";");
                _ = await setup.Database.ExecuteSqlRawAsync(
                    "DROP INDEX \"IX_DeviceTokens_Token\";");
                _ = await setup.Database.ExecuteSqlRawAsync(
                    "DROP INDEX \"IX_DeviceTokens_InstallationId\";");
                _ = await setup.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX \"IX_DeviceTokens_UserId_InstallationId\" "
                        + "ON \"DeviceTokens\" (\"UserId\", \"InstallationId\");");
                _ = await setup.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"NotificationPreferences\" "
                        + "DROP COLUMN \"AttentionPushCategoryPreferencesJson\";");
                _ = await setup.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"NotificationPreferences\" "
                        + "DROP COLUMN \"PushOnPrinterOffline\";");
            }

            await using (AppDbContext upgraded = new(options))
            {
                await SqliteNativePushSchemaUpgrade.ApplyAsync(
                    upgraded,
                    NullLogger.Instance);

                var repository = new EfDeviceTokenRepository(upgraded);
                IReadOnlyList<DeviceToken> existing = await repository.GetActiveByUserAsync(userId);
                existing.Should().ContainSingle();
                existing.Single().Id.Should().Be(tokenId);
                existing.Single().RegistrationVersion.Should().Be(0);
                existing.Single().Token.Should().Be(new string('a', 64));

                DeviceToken refreshed = await repository.UpsertAsync(
                    userId,
                    "Install-A",
                    new string('b', 64),
                    "ios",
                    "development",
                    "com.example.app");

                refreshed.Id.Should().Be(tokenId);
                refreshed.RegistrationVersion.Should().Be(1);
                refreshed.Token.Should().Be(new string('b', 64));
                NotificationPreferences preferences = await upgraded.NotificationPreferences
                    .AsNoTracking()
                    .SingleAsync(value => value.UserId == userId);
                preferences.AttentionPushCategoryPreferencesJson.Should().BeNull();
                preferences.PushOnPrinterOffline.Should().BeTrue();

                IReadOnlyCollection<string> indexes = await ReadDeviceTokenIndexesAsync(upgraded);
                indexes.Should().Contain("IX_DeviceTokens_Token");
                indexes.Should().Contain("IX_DeviceTokens_InstallationId");
                indexes.Should().Contain("IX_DeviceTokens_UserId");
                indexes.Should().NotContain("IX_DeviceTokens_UserId_InstallationId");
                (await ReadIndexSqlAsync(upgraded, "IX_DeviceTokens_InstallationId"))
                    .Should().Contain("WHERE \"IsActive\" = 1");
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

    private static async Task<IReadOnlyCollection<string>> ReadDeviceTokenIndexesAsync(
        AppDbContext db)
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
            command.CommandText = "SELECT name FROM pragma_index_list('DeviceTokens');";
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<string?> ReadIndexSqlAsync(AppDbContext db, string indexName)
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
            command.CommandText =
                "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;";
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = indexName;
            _ = command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync() as string;
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }
}
