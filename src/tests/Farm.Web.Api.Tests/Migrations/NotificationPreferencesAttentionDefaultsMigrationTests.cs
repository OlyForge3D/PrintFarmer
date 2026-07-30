using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Migrations;

public sealed class NotificationPreferencesAttentionDefaultsMigrationTests
{
    [Fact]
    public void Up_BothProviders_ChangeOnlyFreshEmailDefaultsAndNeverRewriteExistingOptOuts()
    {
        Migration[] migrations =
        [
            new Farm.Migrations.PostgreSQL.Migrations.HicksV5NotificationPrefsAttentionDefaults(),
            new Farm.Migrations.SqlServer.Migrations.HicksV5NotificationPrefsAttentionDefaults(),
        ];

        foreach (Migration migration in migrations)
        {
            var builder = new MigrationBuilder(migration.GetType().Namespace!.Contains("PostgreSQL", StringComparison.Ordinal)
                ? "Npgsql.EntityFrameworkCore.PostgreSQL"
                : "Microsoft.EntityFrameworkCore.SqlServer");
            MethodInfo up = migration.GetType().GetMethod(
                "Up",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Migration {migration.GetType().Name} has no Up method.");

            _ = up.Invoke(migration, [builder]);

            builder.Operations.OfType<SqlOperation>().Should().BeEmpty(
                "provider upgrades must not destructively rewrite retained per-cell choices");
            AlterColumnOperation[] alters = builder.Operations
                .OfType<AlterColumnOperation>()
                .ToArray();
            alters.Select(operation => operation.Name).Should().BeEquivalentTo(
                "EmailOnJobCompleted",
                "EmailOnJobFailed",
                "EmailOnJobPaused");
            alters.Should().OnlyContain(operation =>
                object.Equals(operation.DefaultValue, false)
                && object.Equals(operation.OldColumn.DefaultValue, true));
            alters.Should().NotContain(operation =>
                operation.Name.StartsWith("Enable", StringComparison.Ordinal),
                "an existing global opt-out must remain byte-for-byte unchanged");
        }
    }
}
