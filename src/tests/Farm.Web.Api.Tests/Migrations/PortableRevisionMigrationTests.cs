using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Farm.Web.Api.Tests.Migrations;

public sealed class PortableRevisionMigrationTests
{
    public static TheoryData<Type, string, string> ProviderMigrations =>
        new()
        {
            {
                typeof(Farm.Migrations.PostgreSQL.Migrations.UsePortableRevisionConcurrency),
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                "\""
            },
            {
                typeof(Farm.Migrations.SqlServer.Migrations.UsePortableRevisionConcurrency),
                "Microsoft.EntityFrameworkCore.SqlServer",
                "["
            },
            {
                typeof(Farm.Migrations.Sqlite.Migrations.UsePortableRevisionConcurrency),
                "Microsoft.EntityFrameworkCore.Sqlite",
                "\""
            },
        };

    [Theory]
    [MemberData(nameof(ProviderMigrations))]
    public void CoreMigration_BackfillsEveryPreExistingRevisionBeforeAlteringDefault(
        Type migrationType,
        string activeProvider,
        string quote)
    {
        var migration = (Migration)Activator.CreateInstance(migrationType)!;
        var builder = new MigrationBuilder(activeProvider);
        MethodInfo up = migrationType.GetMethod(
            "Up",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Migration {migrationType.FullName} has no Up method.");
        _ = up.Invoke(migration, [builder]);

        foreach (string table in new[]
                 {
                     "PrintJobs",
                     "PrinterDispatchStates",
                     "DispatchSettings",
                 })
        {
            int backfillIndex = builder.Operations.FindIndex(
                operation => operation is SqlOperation sql
                    && sql.Sql.Contains(table, StringComparison.Ordinal)
                    && sql.Sql.Contains("Revision", StringComparison.Ordinal)
                    && sql.Sql.Contains("< 1", StringComparison.Ordinal));
            int alterIndex = builder.Operations.FindIndex(
                operation => operation is AlterColumnOperation alter
                    && alter.Table == table
                    && alter.Name == "Revision");

            backfillIndex.Should().BeGreaterThanOrEqualTo(
                0,
                $"{quote}{table} must backfill invalid legacy revisions");
            alterIndex.Should().BeGreaterThan(
                backfillIndex,
                $"{quote}{table} must be backfilled before its default is altered");
        }
    }
}
