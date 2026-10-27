using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Farm.Infrastructure.Tests.Migrations;

[Collection(ProviderDatabaseTestCollection.Name)]
public sealed class PrinterFileCatalogPersistenceTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void CatalogModel_AllProviders_UsesPortableIdentityAndNoncascadingAssets(string provider)
    {
        using AppDbContext context = CreateProviderContext(provider);
        IModel model = context.GetService<IDesignTimeModel>().Model;
        IEntityType location = model.FindEntityType(typeof(PrinterFileLocation))!;
        Assert.Contains(location.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["SourcePrinterId", "IdentityKey"]));
        Assert.Equal(64, location.FindProperty(nameof(PrinterFileLocation.IdentityKey))!.GetMaxLength());
        Assert.Equal(32768, location.FindProperty(nameof(PrinterFileLocation.Metadata))!.GetMaxLength());
        Assert.True(location.FindProperty(nameof(PrinterFileLocation.Metadata))!.IsNullable);
        Assert.True(location.FindProperty(nameof(PrinterFileLocation.DetailsFetchedAtUtc))!.IsNullable);
        Assert.True(location.FindProperty(nameof(PrinterFileLocation.Revision))!.IsConcurrencyToken);
        Assert.Equal(DeleteBehavior.SetNull, Assert.Single(location.GetForeignKeys()).DeleteBehavior);

        IEntityType origin = model.FindEntityType(typeof(PrinterFileOrigin))!;
        Assert.All(origin.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.SetNull, key.DeleteBehavior));
        Assert.Equal(PropertySaveBehavior.Throw,
            origin.FindProperty(nameof(PrinterFileOrigin.ContentSha256))!.GetAfterSaveBehavior());
        IEntityType operation = model.FindEntityType(typeof(PrinterFileOperation))!;
        Assert.Contains(operation.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(["ActorId", "CommandKeyHash"]));
        Assert.Equal(PropertySaveBehavior.Throw,
            operation.FindProperty(nameof(PrinterFileOperation.PayloadSha256))!.GetAfterSaveBehavior());
        Assert.All(model.FindEntityType(typeof(PrinterFileOperationJob))!.GetForeignKeys(),
            key => Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task MigrateAsync_PopulatedSqlite_PreservesAssetsAndIndependentOrigins()
    {
        await using SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using AppDbContext context = CreateSqliteContext(connection);
        await AssertPopulatedUpgradeAsync(context);
    }

    [Theory]
    [InlineData("postgres", "PFARM_TEST_POSTGRES_CONN")]
    [InlineData("sqlserver", "PFARM_TEST_SQLSERVER_CONN")]
    [Trait("Category", "DbHeavy")]
    public async Task MigrateAsync_PopulatedProductionProvider_PreservesIdentityAssetsAndOrigins(
        string provider,
        string connectionVariable)
    {
        string? connectionString = Environment.GetEnvironmentVariable(connectionVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString),
            $"Catalog provider verification did not run: set {connectionVariable}.");
        string databaseName = $"pfarm_catalog_{Guid.NewGuid():N}";
        string isolatedConnection = provider == "postgres"
            ? new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString
            : new SqlConnectionStringBuilder(connectionString) { InitialCatalog = databaseName }.ConnectionString;
        await using AppDbContext context = CreateProviderContext(provider, isolatedConnection);
        try
        {
            await AssertPopulatedUpgradeAsync(context);
            await AssertDistinctIdentityAsync(context);
        }
        finally
        {
            // Only this test's newly named database is eligible for cleanup.
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static async Task AssertPopulatedUpgradeAsync(AppDbContext context)
    {
        string previous = context.Database.GetMigrations()
            .TakeWhile(name => !name.EndsWith("_AddPrinterFileCatalog", StringComparison.Ordinal))
            .Last();
        await context.GetService<IMigrator>().MigrateAsync(previous);

        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "Catalog test" };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            Name = "Catalog test",
            Manufacturer = manufacturer,
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Source",
            ServerUrl = "http://catalog-test.invalid",
            Manufacturer = manufacturer,
            Model = model,
        };
        FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/catalog-test", FolderType = "gcode" };
        GcodeFile asset = new()
        {
            Id = Guid.NewGuid(),
            Name = "Owned file",
            FileName = "owned.gcode",
            FileHash = new string('a', 64),
            FilePath = "owned.gcode",
            Folder = folder,
            FileSizeBytes = 17,
        };
        context.AddRange(printer, asset);
        await context.SaveChangesAsync();
        await AssertDetailsUpgradeAsync(context);
        Assert.Equal(asset.Id, (await context.GcodeFiles.SingleAsync(x => x.Id == asset.Id)).Id);

        PrinterFileLocation location = NewLocation(printer.Id, "gcodes", "one/model.gcode");
        location.RegisteredPrinter = printer;
        PrinterFileScanState scan = new()
        {
            Id = Guid.NewGuid(),
            SourcePrinterId = printer.Id,
            RegisteredPrinter = printer,
            NextScanAtUtc = DateTime.UtcNow,
        };
        PrinterFileOrigin origin = new()
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            SourcePrinterId = printer.Id,
            ActorId = Guid.NewGuid(),
            Location = location,
            GcodeFile = asset,
            StorageRoot = location.StorageRoot,
            RelativePath = location.RelativePath,
            ContentSha256 = asset.FileHash,
            SizeBytes = asset.FileSizeBytes,
        };
        context.AddRange(location, scan, origin);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Printers.Where(x => x.Id == printer.Id).ExecuteDeleteAsync();
        PrinterFileLocation retainedLocation = await context.PrinterFileLocations.SingleAsync();
        Assert.Null(retainedLocation.RegisteredPrinterId);
        Assert.Equal(printer.Id, retainedLocation.SourcePrinterId);
        Assert.Null((await context.PrinterFileScanStates.SingleAsync()).RegisteredPrinterId);
        Assert.True(await context.GcodeFiles.AnyAsync(x => x.Id == asset.Id));

        await context.PrinterFileLocations.ExecuteDeleteAsync();
        await context.GcodeFiles.Where(x => x.Id == asset.Id).ExecuteDeleteAsync();
        context.ChangeTracker.Clear();
        PrinterFileOrigin retainedOrigin = await context.PrinterFileOrigins.SingleAsync();
        Assert.Null(retainedOrigin.LocationId);
        Assert.Null(retainedOrigin.GcodeFileId);
        Assert.Equal(printer.Id, retainedOrigin.SourcePrinterId);
        Assert.Equal("one/model.gcode", retainedOrigin.RelativePath);
        Assert.Equal(new string('a', 64), retainedOrigin.ContentSha256);
    }

    private static async Task AssertDetailsUpgradeAsync(AppDbContext context)
    {
        string previous = context.Database.GetMigrations()
            .TakeWhile(name => !name.EndsWith("_PersistPrinterFileDetails", StringComparison.Ordinal))
            .Last();
        await context.GetService<IMigrator>().MigrateAsync(previous);
        PrinterFileLocation old = NewLocation(Guid.NewGuid(), "usb", "PART~1.BGC");
        DateTime observed = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        // Insert using the pre-upgrade schema, which has neither detail column.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PrinterFileLocations"
                ("Id", "Revision", "SourcePrinterId", "StorageRoot", "RelativePath", "IdentityKey",
                 "DisplayName", "SizeBytes", "ObservationRevision", "ConfigurationGeneration",
                 "MutationGeneration", "FirstSeenAtUtc", "LastSeenAtUtc", "Availability")
            VALUES ({old.Id}, {old.Revision}, {old.SourcePrinterId}, {old.StorageRoot},
                    {old.RelativePath}, {old.IdentityKey}, {"Long printable part.bgcode"}, {123L},
                    {old.ObservationRevision}, {old.ConfigurationGeneration}, {old.MutationGeneration},
                    {observed}, {observed}, {"Available"})
            """);
        await context.Database.MigrateAsync();

        PrinterFileLocation stored = await context.PrinterFileLocations.SingleAsync(x => x.Id == old.Id);
        Assert.Equal(old.RelativePath, stored.RelativePath);
        Assert.Equal(old.IdentityKey, stored.IdentityKey);
        Assert.Equal("Long printable part.bgcode", stored.DisplayName);
        Assert.Equal(123L, stored.SizeBytes);
        Assert.Null(stored.Metadata);
        Assert.Null(stored.DetailsFetchedAtUtc);

        stored.Metadata = new()
        {
            BrimWidthMm = 0,
            QuietLeftPresent = false,
            FilamentUsedPerToolG = [0, 2.5],
            FilamentTypePerTool = ["PLA", "PETG"],
            Profile = "Fine detail",
        };
        stored.DetailsFetchedAtUtc = observed;
        await context.SaveChangesAsync();
        context.Entry(stored).State = EntityState.Detached;
        stored = await context.PrinterFileLocations.SingleAsync(x => x.Id == old.Id);
        Assert.Equal(observed, stored.DetailsFetchedAtUtc);
        Assert.NotNull(stored.Metadata);
        Assert.Equal(0, stored.Metadata.BrimWidthMm);
        Assert.False(stored.Metadata.QuietLeftPresent);
        Assert.Equal(new double[] { 0, 2.5 }, stored.Metadata.FilamentUsedPerToolG!);
        Assert.Equal(new[] { "PLA", "PETG" }, stored.Metadata.FilamentTypePerTool!);
        Assert.Equal("Fine detail", stored.Metadata.Profile);

        stored.Metadata.FilamentUsedPerToolG![1] = 4;
        await context.SaveChangesAsync();
        context.Entry(stored).State = EntityState.Detached;
        stored = await context.PrinterFileLocations.SingleAsync(x => x.Id == old.Id);
        Assert.Equal(new double[] { 0, 4 }, stored.Metadata!.FilamentUsedPerToolG!);

        stored.Metadata = null;
        await context.SaveChangesAsync();
        context.Entry(stored).State = EntityState.Detached;
        stored = await context.PrinterFileLocations.SingleAsync(x => x.Id == old.Id);
        Assert.Null(stored.Metadata);
        Assert.Equal(observed, stored.DetailsFetchedAtUtc);
        context.Remove(stored);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SaveChangesAsync_CaseRootsAndNestedNames_RetainsDistinctLocations()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext context = CreateSqliteContext(connection);
        await context.Database.MigrateAsync();
        await AssertDistinctIdentityAsync(context);
    }

    private static async Task AssertDistinctIdentityAsync(AppDbContext context)
    {
        Guid printer = Guid.NewGuid();
        PrinterFileLocation[] locations =
        [
            NewLocation(printer, "gcodes", "one/model.gcode"),
            NewLocation(printer, "gcodes", "two/model.gcode"),
            NewLocation(printer, "gcodes", "one/MODEL.gcode"),
            NewLocation(printer, "sdcard", "one/model.gcode"),
            NewLocation(Guid.NewGuid(), "gcodes", "one/model.gcode"),
            NewLocation(printer, "gcodes", "one/Caf\u00e9.gcode"),
            NewLocation(printer, "gcodes", "one/Cafe\u0301.gcode"),
        ];
        context.PrinterFileLocations.AddRange(locations);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        PrinterFileLocation[] stored = await context.PrinterFileLocations.ToArrayAsync();
        Assert.Equal(7, stored.Length);
        foreach (PrinterFileLocation expected in locations)
        {
            PrinterFileLocation actual = Assert.Single(stored, x => x.Id == expected.Id);
            Assert.Equal(expected.SourcePrinterId, actual.SourcePrinterId);
            Assert.Equal(expected.StorageRoot, actual.StorageRoot);
            Assert.Equal(expected.RelativePath, actual.RelativePath);
            Assert.Equal(expected.IdentityKey, actual.IdentityKey);
        }
        PrinterFileLocation changed = await context.PrinterFileLocations.SingleAsync(x => x.Id == locations[0].Id);
        long previousRevision = changed.Revision;
        changed.ConfigurationGeneration++;
        changed.ObservationRevision++;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        PrinterFileLocation retained = await context.PrinterFileLocations.SingleAsync(x => x.Id == changed.Id);
        Assert.Equal(2, retained.ConfigurationGeneration);
        Assert.Equal(2, retained.ObservationRevision);
        Assert.True(retained.Revision > previousRevision);
        Assert.Equal(locations[0].IdentityKey, retained.IdentityKey);
        context.PrinterFileLocations.Add(NewLocation(printer, "gcodes", "one/model.gcode"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_CommandReplay_RejectsDuplicateActorKeyButAllowsAnotherActor()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext context = CreateSqliteContext(connection);
        await context.Database.MigrateAsync();
        PrinterFileLocation location = NewLocation(Guid.NewGuid(), "gcodes", "model.gcode");
        context.Add(location);
        Guid actor = Guid.NewGuid();
        context.AddRange(NewOperation(actor, location), NewOperation(Guid.NewGuid(), location));
        await context.SaveChangesAsync();
        context.Add(NewOperation(actor, location));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_OriginSnapshotMutation_IsRejected()
    {
        await using AppDbContext context = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        PrinterFileOrigin origin = new()
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            ContentSha256 = new string('a', 64),
        };
        context.Add(origin);
        await context.SaveChangesAsync();
        origin.ContentSha256 = new string('b', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Theory]
    [InlineData(nameof(PrinterFileLocation.SourcePrinterId))]
    [InlineData(nameof(PrinterFileLocation.StorageRoot))]
    [InlineData(nameof(PrinterFileLocation.RelativePath))]
    [InlineData(nameof(PrinterFileLocation.IdentityKey))]
    public async Task SaveChangesAsync_LocationIdentityMutation_IsRejected(string property)
    {
        await using AppDbContext context = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        PrinterFileLocation location = NewLocation(Guid.NewGuid(), "gcodes", "one.gcode");
        context.Add(location);
        await context.SaveChangesAsync();
        context.Entry(location).Property(property).CurrentValue =
            property == nameof(PrinterFileLocation.SourcePrinterId) ? Guid.NewGuid() : "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    private static PrinterFileOperation NewOperation(Guid actor, PrinterFileLocation location) => new()
    {
        Id = Guid.NewGuid(),
        ActorId = actor,
        Location = location,
        SourcePrinterId = location.SourcePrinterId,
        CommandKeyHash = new string('c', 64),
        PayloadSha256 = new string('d', 64),
        Action = PrinterFileAction.Copy,
    };

    private static PrinterFileLocation NewLocation(Guid printer, string root, string path) => new()
    {
        Id = Guid.NewGuid(),
        SourcePrinterId = printer,
        StorageRoot = root,
        RelativePath = path,
        IdentityKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root + "\0" + path))),
        DisplayName = path,
    };

    private static AppDbContext CreateSqliteContext(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection, provider => provider.MigrationsAssembly("Farm.Migrations.Sqlite")).Options);

    private static AppDbContext CreateProviderContext(string provider, string? connectionString = null)
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                options.UseSqlite("Data Source=:memory:",
                    sql => sql.MigrationsAssembly("Farm.Migrations.Sqlite"));
                break;
            case "postgres":
                options.UseNpgsql(connectionString ?? "Host=localhost;Database=catalog_model;Username=model",
                    sql => sql.MigrationsAssembly("Farm.Migrations.PostgreSQL"));
                break;
            case "sqlserver":
                options.UseSqlServer(connectionString ?? "Server=localhost;Database=catalog_model;Integrated Security=True",
                    sql => sql.MigrationsAssembly("Farm.Migrations.SqlServer"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }

        return new AppDbContext(options.Options);
    }
}
