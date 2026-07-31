using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Spoolman;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Farm.Web.Api.Tests.Data;

public sealed class SpoolBurnRateSourceAttributionModelTests
{

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
}
