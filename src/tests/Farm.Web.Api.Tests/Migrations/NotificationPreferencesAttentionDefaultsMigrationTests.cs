using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Migrations;

public sealed class NotificationPreferencesAttentionDefaultsMigrationTests
{
    private static readonly string[] ChannelPrefixes = ["Email", "Push", "Telegram", "InApp"];
    private static readonly string[] EventSuffixes =
    [
        "JobStarted",
        "JobCompleted",
        "JobFailed",
        "JobPaused",
        "PrinterFailure",
        "FilamentRunout",
        "HarvestReady",
        "MaintenanceDue",
        "PrinterOffline",
    ];

    [Fact]
    public async Task Up_ExistingOptOuts_BackfillsAllChannelRowsWithoutChangingCanonicalUser()
    {
        Migration[] migrations =
        [
            new Farm.Migrations.PostgreSQL.Migrations.HicksV5NotificationPrefsAttentionDefaults(),
            new Farm.Migrations.SqlServer.Migrations.HicksV5NotificationPrefsAttentionDefaults(),
        ];

        foreach (Migration migration in migrations)
        {
            await AssertBackfillAsync(migration);
        }
    }

    private static async Task AssertBackfillAsync(Migration migration)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        string[] attentionColumns = ChannelPrefixes
            .SelectMany(prefix => EventSuffixes.Select(suffix => $"{prefix}On{suffix}"))
            .ToArray();
        string attentionDefinitions = string.Join(
            ", ",
            attentionColumns.Select(column => $"\"{column}\" INTEGER NOT NULL"));
        await ExecuteAsync(
            connection,
            $"""
            CREATE TABLE "NotificationPreferences" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "EnableEmailNotifications" INTEGER NOT NULL,
                "EnablePushNotifications" INTEGER NOT NULL,
                "EnableTelegramNotifications" INTEGER NOT NULL,
                "EnableInAppNotifications" INTEGER NOT NULL,
                {attentionDefinitions}
            );
            """);

        string allColumns = string.Join(", ", attentionColumns.Select(column => $"\"{column}\""));
        string allEnabledValues = string.Join(", ", attentionColumns.Select(_ => "1"));
        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO "NotificationPreferences"
                ("Id", "EnableEmailNotifications", "EnablePushNotifications",
                 "EnableTelegramNotifications", "EnableInAppNotifications", {allColumns})
            VALUES ('opted-out', 0, 0, 0, 0, {allEnabledValues});
            INSERT INTO "NotificationPreferences"
                ("Id", "EnableEmailNotifications", "EnablePushNotifications",
                 "EnableTelegramNotifications", "EnableInAppNotifications", {allColumns})
            VALUES ('canonical', 1, 1, 1, 1, {allEnabledValues});
            """);

        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        MethodInfo up = migration.GetType().GetMethod(
            "Up",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Migration {migration.GetType().Name} has no Up method.");
        _ = up.Invoke(migration, [builder]);
        foreach (SqlOperation operation in builder.Operations.OfType<SqlOperation>())
        {
            await ExecuteAsync(connection, operation.Sql);
        }

        foreach (string column in attentionColumns)
        {
            (await ReadBoolAsync(connection, "opted-out", column)).Should().BeFalse(
                $"{migration.GetType().FullName} must preserve the existing channel opt-out for {column}");
            (await ReadBoolAsync(connection, "canonical", column)).Should().BeTrue(
                $"{migration.GetType().FullName} must retain canonical enabled defaults for {column}");
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ReadBoolAsync(
        SqliteConnection connection,
        string id,
        string column)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT \"{column}\" FROM \"NotificationPreferences\" WHERE \"Id\" = $id";
        _ = command.Parameters.AddWithValue("$id", id);
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }
}
