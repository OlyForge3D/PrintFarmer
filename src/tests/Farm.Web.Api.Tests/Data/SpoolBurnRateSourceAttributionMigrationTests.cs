using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Spoolman;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Farm.Web.Api.Tests.Data;

public sealed class SpoolBurnRateSourceAttributionMigrationTests
{
    [Fact]
    public void PostgreSqlMigration_PreservesBoundsAndProjectionKey()
    {
        AssertMigration(
            new Farm.Migrations.PostgreSQL.Migrations.AddSpoolBurnRateSourceAttribution(),
            "character varying(256)",
            "character varying(32)",
            "boolean");
    }

    [Fact]
    public void SqlServerMigration_PreservesBoundsAndProjectionKey()
    {
        AssertMigration(
            new Farm.Migrations.SqlServer.Migrations.AddSpoolBurnRateSourceAttribution(),
            "nvarchar(256)",
            "nvarchar(32)",
            "bit");
    }

    [Fact]
    public void EfModel_PreservesSourceBoundsAndCompletionIdempotencyIndex()
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        using AppDbContext db = new(options);
        IEntityType usage = db.Model.FindEntityType(
            typeof(PrintJobToolheadUsage))!;

        Assert.Equal(
            CanonicalSpoolIdentity.MaxSourceIdentityLength,
            usage.FindProperty(nameof(PrintJobToolheadUsage.SpoolSourceIdentity))!
                .GetMaxLength());
        Assert.Equal(
            32,
            usage.FindProperty(nameof(PrintJobToolheadUsage.SpoolSourceKind))!
                .GetMaxLength());

        IIndex idempotencyIndex = Assert.Single(
            usage.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual(
                    [
                        nameof(PrintJobToolheadUsage.PrintJobId),
                        nameof(PrintJobToolheadUsage.ToolheadIndex),
                    ]));
        Assert.True(idempotencyIndex.IsUnique);

        IIndex projectionIndex = Assert.Single(
            usage.GetIndexes(),
            index => index.GetDatabaseName()
                == "IX_PrintJobToolheadUsages_SpoolProjection");
        Assert.False(projectionIndex.IsUnique);
        Assert.Equal(
            [
                nameof(PrintJobToolheadUsage.SpoolSourceKind),
                nameof(PrintJobToolheadUsage.SpoolSourceIdentity),
                nameof(PrintJobToolheadUsage.SpoolmanSpoolId),
                nameof(PrintJobToolheadUsage.IsFilamentUsageAuthoritative),
            ],
            projectionIndex.Properties.Select(property => property.Name));
    }

    private static void AssertMigration(
        Migration migration,
        string sourceIdentityType,
        string sourceKindType,
        string authoritativeType)
    {
        IReadOnlyList<MigrationOperation> up = migration.UpOperations;
        List<AddColumnOperation> columns = up.OfType<AddColumnOperation>().ToList();
        Assert.Equal(3, columns.Count);

        AddColumnOperation sourceIdentity = Assert.Single(
            columns,
            column => column.Name
                == nameof(PrintJobToolheadUsage.SpoolSourceIdentity));
        Assert.Equal(
            CanonicalSpoolIdentity.MaxSourceIdentityLength,
            sourceIdentity.MaxLength);
        Assert.Equal(sourceIdentityType, sourceIdentity.ColumnType);
        Assert.True(sourceIdentity.IsNullable);

        AddColumnOperation sourceKind = Assert.Single(
            columns,
            column => column.Name
                == nameof(PrintJobToolheadUsage.SpoolSourceKind));
        Assert.Equal(32, sourceKind.MaxLength);
        Assert.Equal(sourceKindType, sourceKind.ColumnType);
        Assert.True(sourceKind.IsNullable);

        AddColumnOperation authoritative = Assert.Single(
            columns,
            column => column.Name
                == nameof(PrintJobToolheadUsage.IsFilamentUsageAuthoritative));
        Assert.Equal(authoritativeType, authoritative.ColumnType);
        Assert.False(authoritative.IsNullable);
        Assert.Equal(false, authoritative.DefaultValue);

        CreateIndexOperation projectionIndex = Assert.Single(
            up.OfType<CreateIndexOperation>());
        Assert.Equal(
            "IX_PrintJobToolheadUsages_SpoolProjection",
            projectionIndex.Name);
        Assert.Equal("PrintJobToolheadUsages", projectionIndex.Table);
        Assert.Equal(
            [
                nameof(PrintJobToolheadUsage.SpoolSourceKind),
                nameof(PrintJobToolheadUsage.SpoolSourceIdentity),
                nameof(PrintJobToolheadUsage.SpoolmanSpoolId),
                nameof(PrintJobToolheadUsage.IsFilamentUsageAuthoritative),
            ],
            projectionIndex.Columns);
        Assert.False(projectionIndex.IsUnique);

        Assert.Equal(4, migration.DownOperations.Count);
        Assert.IsType<DropIndexOperation>(migration.DownOperations[0]);
        Assert.All(
            migration.DownOperations.Skip(1),
            operation => Assert.IsType<DropColumnOperation>(operation));
    }
}
