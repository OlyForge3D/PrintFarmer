using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Farm.Infrastructure.Tests.Migrations;

public sealed class PrinterFileEnrichmentPersistenceTests
{
    [Theory]
    [InlineData("Sqlite")]
    [InlineData("PostgreSQL")]
    [InlineData("SqlServer")]
    public void Model_AllProviders_UsesPortableKeysConcurrencyAndDeleteBoundaries(string provider)
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        switch (provider)
        {
            case "Sqlite":
                options.UseSqlite("Data Source=:memory:", x => x.MigrationsAssembly("Farm.Migrations.Sqlite"));
                break;
            case "PostgreSQL":
                options.UseNpgsql("Host=localhost;Database=model;Username=model",
                    x => x.MigrationsAssembly("Farm.Migrations.PostgreSQL"));
                break;
            case "SqlServer":
                options.UseSqlServer("Server=localhost;Database=model;Integrated Security=True",
                    x => x.MigrationsAssembly("Farm.Migrations.SqlServer"));
                break;
        }

        using AppDbContext db = new(options.Options);
        IModel model = db.GetService<IDesignTimeModel>().Model;
        IEntityType enrichment = model.FindEntityType(typeof(PrinterFileEnrichment))!;
        Assert.Equal(nameof(PrinterFileEnrichment.LocationId), Assert.Single(enrichment.FindPrimaryKey()!.Properties).Name);
        Assert.True(enrichment.FindProperty(nameof(PrinterFileEnrichment.Revision))!.IsConcurrencyToken);
        Assert.Equal(64, enrichment.FindProperty(nameof(PrinterFileEnrichment.SourceIdentity))!.GetMaxLength());
        Assert.Equal(DeleteBehavior.Cascade, enrichment.GetForeignKeys()
            .Single(x => x.PrincipalEntityType.ClrType == typeof(PrinterFileLocation)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.SetNull, enrichment.GetForeignKeys()
            .Single(x => x.PrincipalEntityType.ClrType == typeof(PrinterFileThumbnailArtifact)).DeleteBehavior);
        IEntityType artifact = model.FindEntityType(typeof(PrinterFileThumbnailArtifact))!;
        Assert.Equal(PropertySaveBehavior.Throw,
            artifact.FindProperty(nameof(PrinterFileThumbnailArtifact.ContentSha256))!.GetAfterSaveBehavior());
        Assert.Equal(typeof(long), artifact.FindProperty(nameof(PrinterFileThumbnailArtifact.SizeBytes))!.ClrType);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Migrate_PopulatedSqlite_PreservesLegacyLocationsAndEnforcesNewForeignKeys()
    {
        await using SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection, x => x.MigrationsAssembly("Farm.Migrations.Sqlite")).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261027134803_PersistPrinterFileDetails");
        PrinterFileLocation location = new()
        {
            Id = Guid.NewGuid(),
            SourcePrinterId = Guid.NewGuid(),
            StorageRoot = "gcodes",
            RelativePath = "legacy.gcode",
            IdentityKey = new string('a', 64),
            SizeBytes = 456,
        };
        db.PrinterFileLocations.Add(location);
        await db.SaveChangesAsync();

        await db.Database.MigrateAsync();

        Assert.Equal(456, (await db.PrinterFileLocations.SingleAsync()).SizeBytes);
        Assert.Empty(await db.PrinterFileEnrichments.ToListAsync());
        Assert.Equal(1, (await db.PrinterFileThumbnailStorageLocks.SingleAsync()).Id);
        PrinterFileThumbnailArtifact artifact = new()
        {
            Id = Guid.NewGuid(),
            ContentSha256 = new string('b', 64),
            ContentType = "image/png",
            Width = 1,
            Height = 1,
            SizeBytes = 70,
            CreatedAtUtc = DateTime.UtcNow,
        };
        PrinterFileEnrichment enrichment = new()
        {
            Location = location,
            Artifact = artifact,
            SourceIdentity = PrinterFileEnrichmentIdentity.For(location),
            MetadataState = PrinterFileEnrichmentState.Ready,
            ThumbnailState = PrinterFileEnrichmentState.Ready,
        };
        db.PrinterFileEnrichments.Add(enrichment);
        await db.SaveChangesAsync();
        long revision = enrichment.Revision;
        enrichment.ThumbnailReason = "refresh";
        await db.SaveChangesAsync();
        Assert.True(enrichment.Revision > revision);

        db.PrinterFileThumbnailArtifacts.Remove(artifact);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Null((await db.PrinterFileEnrichments.SingleAsync()).ArtifactId);
        db.PrinterFileLocations.Remove(await db.PrinterFileLocations.SingleAsync());
        await db.SaveChangesAsync();
        Assert.Empty(await db.PrinterFileEnrichments.ToListAsync());
    }

    [Fact]
    public void SourceIdentity_ExcludesObservationAndMutationFences_ButIncludesEveryContentComponent()
    {
        PrinterFileLocation location = new()
        {
            SourcePrinterId = Guid.NewGuid(),
            StorageRoot = "gcodes",
            RelativePath = "one.gcode",
            SizeBytes = 1,
            ModifiedValue = "mtime",
            ProviderVersion = "version",
            ProviderDigest = "digest",
        };
        string identity = PrinterFileEnrichmentIdentity.For(location);
        location.ObservationRevision++;
        location.MutationGeneration++;
        location.Revision++;
        Assert.Equal(identity, PrinterFileEnrichmentIdentity.For(location));
        Action<PrinterFileLocation>[] changes =
        [
            x => x.SourcePrinterId = Guid.NewGuid(), x => x.StorageRoot += "2", x => x.RelativePath += "2",
            x => x.ConfigurationGeneration++, x => x.SizeBytes++, x => x.ModifiedValue += "2",
            x => x.ProviderVersion += "2", x => x.ProviderDigest += "2",
        ];
        foreach (Action<PrinterFileLocation> change in changes)
        {
            string previous = PrinterFileEnrichmentIdentity.For(location);
            change(location);
            Assert.NotEqual(previous, PrinterFileEnrichmentIdentity.For(location));
        }
    }

    [Theory]
    [InlineData(2097151, false)]
    [InlineData(2097152, true)]
    [InlineData(536870912, true)]
    [InlineData(1099511627776, true)]
    [InlineData(1099511627777, false)]
    public void Settings_ValidateSharedCapacityBounds(long capacity, bool valid)
    {
        PrinterFileIndexSettings settings = new() { ThumbnailCacheMaxBytes = capacity };
        if (valid)
        {
            settings.Validate();
        }
        else
        {
            Assert.Throws<ValidationException>(() => settings.Validate());
        }
    }
}
