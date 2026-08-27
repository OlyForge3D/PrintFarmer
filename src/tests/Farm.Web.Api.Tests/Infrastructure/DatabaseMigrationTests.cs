using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Data.Migrations;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Startup;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Infrastructure;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task CoreMigration_CreatesFreshDatabaseAndIsIdempotent()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);

        DatabaseMigrationResult first = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);
        DatabaseMigrationResult second = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);

        first.LegacySchemaBaselined.Should().BeFalse();
        first.AppliedMigrations.Should().Equal(
            "20260730231403_InitialV2",
            "20260806232640_CanonicalizePrintJobPriority",
            "20260807023655_UsePortableRevisionConcurrency",
            "20260808054302_AddNfcDeviceApproval",
            "20260808162833_AddPowerReadingCompositeIndex",
            "20260811235527_AddRoleUpdatedAtConcurrencyToken",
            "20260812020851_HardenBedClearReplayStorage",
            "20260816094836_RenameHasHeatedChamberToCalibrationHasHeatedChamber",
            "20260820064025_AddQueueRetentionIndexes",
            "20260821152002_AddNozzleHardnessOverride",
            "20260821205923_AddNozzleMaterialCatalog",
            "20260823210035_AddPrinterModelAccelerationFields",
            "20260824020853_RenameCalibrationHasHeatedChamberToHasHeatedChamber",
            "20260825084406_MakeCalibrationAttemptSnapshotIdOptional",
            "20260825110105_RemoveDeprecatedCalibrationPrinterColumns",
            "20260825141109_DropGeneratedProfileRevisionTables",
            "20260825150550_DeletePrinterConfigurationSnapshot",
            "20260825185839_DropDeadCalibrationOrchestrationColumns",
            "20260826051847_AddPrinterModelAliasNormalizedLookup",
            "20260827005237_EnforceNormalizedPrinterModelAliasUniqueness",
            "20260827161050_AddPrinterRotationCursors");
        second.LegacySchemaBaselined.Should().BeFalse();
        second.AppliedMigrations.Should().BeEquivalentTo(first.AppliedMigrations);
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CoreMigration_BackfillsNormalizedPrinterModelAliasLookupColumns()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);
        IMigrator migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260825185839_DropDeadCalibrationOrchestrationColumns");

        Guid manufacturerId = Guid.NewGuid();
        Guid printerModelId = Guid.NewGuid();
        Guid aliasId = Guid.NewGuid();
        context.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Prusa Research",
        });
        context.PrinterModels.Add(new PrinterModel
        {
            Id = printerModelId,
            ManufacturerId = manufacturerId,
            Name = "CORE One",
        });
        _ = await context.SaveChangesAsync();
        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "PrinterModelAliases"
                 ("Id", "PrinterModelId", "SlicerModelName", "SlicerType", "CreatedAt")
             VALUES
                 ({aliasId}, {printerModelId}, {"  Prusa Core One  "}, {"  OrcaSlicer  "}, {DateTime.UtcNow});
             """);

        await migrator.MigrateAsync();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "SlicerModelNameNormalized", "SlicerTypeNormalized"
            FROM "PrinterModelAliases"
            WHERE "Id" = $id;
            """;
        _ = command.Parameters.AddWithValue("$id", aliasId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("PRUSA CORE ONE");
        reader.GetString(1).Should().Be("ORCASLICER");
    }

    [Fact]
    public async Task CoreMigration_RejectsCaseAndWhitespaceVariantDuplicatePrinterModelAlias()
    {
        // #2080 N-NORM-1: EnsureModelAliasAsync persists the trimmed raw name while
        // ResolveModelAliasAsync matches on the normalized columns, so the unique constraint must
        // live on the normalized columns too -- otherwise the DB can silently accept two aliases
        // that differ only by case/whitespace, which the read path can never tell apart.
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);
        _ = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);

        Guid manufacturerId = Guid.NewGuid();
        Guid printerModelId = Guid.NewGuid();
        context.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Prusa Research",
        });
        context.PrinterModels.Add(new PrinterModel
        {
            Id = printerModelId,
            ManufacturerId = manufacturerId,
            Name = "CORE One",
        });
        _ = await context.SaveChangesAsync();

        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "PrinterModelAliases"
                 ("Id", "PrinterModelId", "SlicerModelName", "SlicerModelNameNormalized",
                  "SlicerType", "SlicerTypeNormalized", "CreatedAt")
             VALUES
                 ({Guid.NewGuid()}, {printerModelId}, {"Prusa Core One"}, {"PRUSA CORE ONE"},
                  {"OrcaSlicer"}, {"ORCASLICER"}, {DateTime.UtcNow});
             """);

        Func<Task> duplicateInsert = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "PrinterModelAliases"
                 ("Id", "PrinterModelId", "SlicerModelName", "SlicerModelNameNormalized",
                  "SlicerType", "SlicerTypeNormalized", "CreatedAt")
             VALUES
                 ({Guid.NewGuid()}, {printerModelId}, {"  prusa core one  "}, {"PRUSA CORE ONE"},
                  {"orcaslicer"}, {"ORCASLICER"}, {DateTime.UtcNow});
             """);

        _ = await duplicateInsert.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task CoreMigration_DeduplicatesPreExistingCaseVariantAliasesBeforeEnforcingUniqueness()
    {
        // #2080 N-NORM-1 (review finding, Vasquez): a database that already accumulated
        // case/whitespace-variant duplicate aliases under the old raw-column unique index would
        // otherwise fail outright when EnforceNormalizedPrinterModelAliasUniqueness tries to
        // create its normalized-column unique index. The migration must dedupe first.
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);
        IMigrator migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260826051847_AddPrinterModelAliasNormalizedLookup");

        Guid manufacturerId = Guid.NewGuid();
        Guid printerModelId = Guid.NewGuid();
        context.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Prusa Research",
        });
        context.PrinterModels.Add(new PrinterModel
        {
            Id = printerModelId,
            ManufacturerId = manufacturerId,
            Name = "CORE One",
        });
        _ = await context.SaveChangesAsync();

        // Migration dedup keeps the row with the textually smaller Id per duplicate group (see
        // the migration's own dedup SQL, which self-joins on "Id" > "Id"). Guid.NewGuid() values
        // are random, so assign deterministically here (review finding, Vasquez): otherwise this
        // assertion is flaky -- whichever of the two randomly-generated ids happens to sort lower
        // survives, not necessarily the one this test labels "survivor".
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        (Guid survivorId, Guid duplicateId) = string.CompareOrdinal(first.ToString(), second.ToString()) <= 0
            ? (first, second)
            : (second, first);
        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "PrinterModelAliases"
                 ("Id", "PrinterModelId", "SlicerModelName", "SlicerModelNameNormalized",
                  "SlicerType", "SlicerTypeNormalized", "CreatedAt")
             VALUES
                 ({survivorId}, {printerModelId}, {"Prusa Core One"}, {"PRUSA CORE ONE"},
                  {"OrcaSlicer"}, {"ORCASLICER"}, {DateTime.UtcNow});
             """);
        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "PrinterModelAliases"
                 ("Id", "PrinterModelId", "SlicerModelName", "SlicerModelNameNormalized",
                  "SlicerType", "SlicerTypeNormalized", "CreatedAt")
             VALUES
                 ({duplicateId}, {printerModelId}, {"  prusa core one  "}, {"PRUSA CORE ONE"},
                  {"orcaslicer"}, {"ORCASLICER"}, {DateTime.UtcNow});
             """);

        Func<Task> migrateFurther = async () => await migrator.MigrateAsync(
            "20260827005237_EnforceNormalizedPrinterModelAliasUniqueness");

        _ = await migrateFurther.Should().NotThrowAsync(
            "the migration must deduplicate pre-existing case/whitespace-variant rows before " +
            "creating the normalized-column unique index");

        List<Guid> remainingIds = await context.Set<PrinterModelAlias>()
            .Where(a => a.PrinterModelId == printerModelId)
            .Select(a => a.Id)
            .ToListAsync();
        _ = remainingIds.Should().ContainSingle()
            .Which.Should().Be(survivorId, "the dedup step must keep the lowest-Id row per group");
    }

    [Fact]
    public async Task CoreMigration_SeedsBuiltInNozzleMaterialsMatchingPreCatalogHardnessBaseline()
    {
        // #1827 dispatch/backward-compat parity: prior to this test, no test asserted the actual
        // seeded IsHardened values from the AddNozzleMaterialCatalog migration's raw SQL --
        // DatabaseMigrationTests only asserted migration name ordering. Baseline recovered from
        // the pre-catalog IsHardenedByMaterial(NozzleType) static switch (commit eb2804eb1's
        // predecessor), which this migration's SQL and DataSeedService.SeedNozzleMaterialsAsync
        // must both continue to reproduce exactly.
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);

        _ = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);
        context.ChangeTracker.Clear();

        var seeded = await context.NozzleMaterials
            .OrderBy(m => m.Name)
            .Select(m => new { m.Name, m.IsHardened, m.IsBuiltIn })
            .ToListAsync();

        seeded.Should().BeEquivalentTo(new[]
        {
            new { Name = "Abrasive", IsHardened = true, IsBuiltIn = true },
            new { Name = "Brass", IsHardened = false, IsBuiltIn = true },
            new { Name = "Diamond", IsHardened = true, IsBuiltIn = true },
            new { Name = "HardenedSteel", IsHardened = true, IsBuiltIn = true },
            new { Name = "PlatedCopper", IsHardened = false, IsBuiltIn = true },
            new { Name = "Ruby", IsHardened = true, IsBuiltIn = true },
            new { Name = "StainlessSteel", IsHardened = false, IsBuiltIn = true },
            new { Name = "ToolSteel", IsHardened = true, IsBuiltIn = true },
            new { Name = "TungstenCarbide", IsHardened = true, IsBuiltIn = true },
        });
    }

    [Fact]
    public async Task CoreMigration_BackfillsLegacyNozzleTypeToMatchingNozzleMaterial()
    {
        // #1827: locks in that a pre-existing NozzleModelDefinition row (created before the
        // catalog existed, with the legacy int NozzleType column) is backfilled to the
        // NozzleMaterial with the matching Name -- and that the resulting IsHardened value is
        // unchanged from what the pre-catalog NozzleType would have implied.
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);
        IMigrator migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260821152002_AddNozzleHardnessOverride");

        Guid manufacturerId = Guid.NewGuid();
        context.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Legacy Mfg",
        });
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Legacy NozzleType enum value 5 = Diamond (Brass=0, HardenedSteel=1, StainlessSteel=2,
        // TungstenCarbide=3, Abrasive=4, Diamond=5, Ruby=6, PlatedCopper=7, ToolSteel=8).
        Guid nozzleId = Guid.NewGuid();
        _ = await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "NozzleModelDefinitions"
                ("Id", "Name", "Diameter", "NozzleType", "HardnessOverride", "NozzleInterface", "MaxTemp", "ManufacturerId")
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7});
            """,
            nozzleId, "Legacy Diamond 0.4", 0.4, 5, 0, 0, 500, manufacturerId);

        _ = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);
        context.ChangeTracker.Clear();

        NozzleModelDefinition migrated = await context.NozzleModelDefinitions
            .Include(n => n.NozzleMaterial)
            .SingleAsync(n => n.Id == nozzleId);

        _ = migrated.NozzleMaterial!.Name.Should().Be(nameof(NozzleType.Diamond));
        _ = migrated.IsHardened.Should().BeTrue(
            "the pre-catalog NozzleType=Diamond row must backfill to the Diamond " +
            "NozzleMaterial, which is hardened");
    }

    [Fact]
    public async Task CoreMigration_NormalizesLegacyPrintJobPrioritiesBeforeAddingConstraint()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);
        IMigrator migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260730231403_InitialV2");

        DateTime now = new(2026, 8, 6, 23, 30, 0, DateTimeKind.Utc);
        context.PrintJobs.AddRange(
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "Legacy negative priority",
                Status = PrintJobStatus.Queued,
                Priority = -1,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "Legacy oversized priority",
                Status = PrintJobStatus.Queued,
                Priority = 100,
                CreatedAt = now,
                UpdatedAt = now,
            });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        _ = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);

        int[] priorities = await context.PrintJobs
            .OrderBy(job => job.Name)
            .Select(job => job.Priority)
            .ToArrayAsync();
        priorities.Should().Equal((int)PrintJobPriority.Low, (int)PrintJobPriority.Urgent);
    }

    [Fact]
    public async Task CoreMigration_SeedsOutboxSequenceFenceWithPortableRevision()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);

        _ = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);
        context.ChangeTracker.Clear();

        OutboxSequenceState seeded = await context.OutboxSequenceStates.SingleAsync(s => s.Id == 1);
        seeded.NextSequence.Should().Be(0L);
        seeded.Revision.Should().Be(1L);
        seeded.RowVersion.Should().Equal(RevisionETag.EncodeBytes(1));

        seeded.NextSequence = 1L;
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        OutboxSequenceState stamped = await context.OutboxSequenceStates.SingleAsync(s => s.Id == 1);
        stamped.RowVersion.Should().NotBeNullOrEmpty(
            "StampRowVersions() must write a concurrency token once the application saves the row");
    }

    [Fact]
    public async Task CoreMigration_BackfillsExistingZeroRevisionAndAllowsUpdate()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);
        IMigrator migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260806232640_CanonicalizePrintJobPriority");
        _ = await context.Database.ExecuteSqlRawAsync(
            "UPDATE \"DispatchSettings\" SET \"Revision\" = 0;");

        _ = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);
        context.ChangeTracker.Clear();

        DispatchSettings settings = await context.DispatchSettings.SingleAsync();
        settings.Revision.Should().Be(1);
        settings.IdleThresholdSeconds++;
        _ = await context.SaveChangesAsync();
        settings.Revision.Should().Be(2);
    }

    [Fact]
    public async Task CoreMigration_BaselinesVerifiedEnsureCreatedDatabase()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);
        _ = await context.Database.EnsureCreatedAsync();

        DatabaseMigrationResult result = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);

        result.LegacySchemaBaselined.Should().BeTrue();
        result.AppliedMigrations.Should().NotBeEmpty();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_ArbitrarilyNamedFirstMigration_WritesHistoryRow()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using ArbitraryBaselineDbContext context = CreateArbitraryBaselineContext(connection);
        _ = await context.Database.EnsureCreatedAsync();

        _ = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            new DatabaseMigrationTarget("test", "BaselineEntities"),
            NullLogger.Instance);

        context.Database.GetMigrations().Should().Equal(ArbitraryNamedBaselineMigration.MigrationId);
        (await ReadAppliedMigrationIdsAsync(connection))
            .Should().Equal(ArbitraryNamedBaselineMigration.MigrationId);
    }

    [Fact]
    public async Task CoreMigration_WithoutMigrationAssembly_FailsBeforeTouchingSchema()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContextWithoutMigrations(connection);
        _ = await context.Database.EnsureCreatedAsync();

        Func<Task> migrate = async () => await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);

        DatabaseMigrationContractException exception =
            (await migrate.Should().ThrowAsync<DatabaseMigrationContractException>()).Which;
        exception.Code.Should().Be("migration_assembly_missing");
        exception.Message.Should().Contain("No SQLite migrations were found");
        (await TableExistsAsync(connection, "Printers")).Should().BeTrue();
        (await TableExistsAsync(connection, "__EFMigrationsHistory")).Should().BeFalse();
    }

    [Fact]
    public async Task ProgramHelpersInitialization_PopulatedSchemaWithoutMigrationAssembly_PropagatesFailure()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        _ = builder.Services.AddLogging();
        _ = builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        _ = builder.Services.AddScoped(_ => Mock.Of<IDatabaseInitializer>());
        _ = builder.Services.AddSingleton<IStartupStatus, StartupStatus>();
        await using WebApplication app = builder.Build();

        await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = await context.Database.EnsureCreatedAsync();
        }

        Func<Task> initialize = async () => await ProgramHelpers.InitializeDatabaseAsync(app);

        DatabaseMigrationContractException exception =
            (await initialize.Should().ThrowAsync<DatabaseMigrationContractException>()).Which;
        exception.Code.Should().Be("migration_assembly_missing");
        exception.Message.Should().Contain("No SQLite migrations were found");
        (await TableExistsAsync(connection, "Printers")).Should().BeTrue();
        (await TableExistsAsync(connection, "__EFMigrationsHistory")).Should().BeFalse();
    }

    [Fact]
    public async Task ProgramHelpersInitialization_NativePushPreStepFailure_PropagatesBeforeMigration()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteConnection connection = database.Connection;
        var initializer = new Mock<IDatabaseInitializer>();
        var startupStatus = new StartupStatus();
        await using WebApplication app = CreateTestApplication(connection, initializer, startupStatus);
        await EnsureLegacySchemaAsync(app);
        await ExecuteSqlAsync(
            connection,
            """
            DROP TABLE "DeviceTokens";
            CREATE VIEW "DeviceTokens" AS SELECT 'blocked' AS "Id";
            """);

        Func<Task> initialize = async () => await ProgramHelpers.InitializeDatabaseAsync(app);

        SqliteException exception = (await initialize.Should().ThrowAsync<SqliteException>()).Which;
        exception.Message.Should().Contain("Cannot add a column to a view");
        initializer.Verify(
            service => service.InitializeAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Never);
        startupStatus.Phase.Should().Be(StartupPhase.Starting);
        (await TableExistsAsync(connection, "__EFMigrationsHistory")).Should().BeFalse();
    }

    [Fact]
    public async Task ProgramHelpersInitialization_MutationWatermarkPreStepFailure_PropagatesAfterNativePushBeforeMigration()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteConnection connection = database.Connection;
        var initializer = new Mock<IDatabaseInitializer>();
        var startupStatus = new StartupStatus();
        await using WebApplication app = CreateTestApplication(connection, initializer, startupStatus);
        await EnsureLegacySchemaAsync(app);
        await ExecuteSqlAsync(
            connection,
            """
            ALTER TABLE "DeviceTokens" DROP COLUMN "RegistrationVersion";
            DROP TABLE "MutationCounters";
            CREATE VIEW "MutationCounters" AS SELECT 1 AS "Id", 0 AS "Value";
            """);

        Func<Task> initialize = async () => await ProgramHelpers.InitializeDatabaseAsync(app);

        SqliteException exception = (await initialize.Should().ThrowAsync<SqliteException>()).Which;
        exception.Message.Should().Contain("MutationCounters");
        (await ColumnExistsAsync(connection, "DeviceTokens", "RegistrationVersion")).Should().BeTrue(
            "the native-push pre-step must commit before the mutation-watermark pre-step runs");
        initializer.Verify(
            service => service.InitializeAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Never);
        startupStatus.Phase.Should().Be(StartupPhase.Starting);
        (await TableExistsAsync(connection, "__EFMigrationsHistory")).Should().BeFalse();
    }

    [Fact]
    public async Task ProgramHelpersInitialization_LegacyPreStepsCompleteBeforeMigrationHistoryIsRecorded()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteConnection connection = database.Connection;
        var initializer = new Mock<IDatabaseInitializer>();
        var startupStatus = new StartupStatus();
        await using WebApplication app = CreateTestApplication(connection, initializer, startupStatus);
        await EnsureLegacySchemaAsync(app);
        await ExecuteSqlAsync(
            connection,
            """ALTER TABLE "DeviceTokens" DROP COLUMN "RegistrationVersion";""");

        await ProgramHelpers.InitializeDatabaseAsync(app);

        (await ColumnExistsAsync(connection, "DeviceTokens", "RegistrationVersion")).Should().BeTrue();
        (await ReadAppliedMigrationIdsAsync(connection)).Should().Equal(
            "20260730231403_InitialV2",
            "20260806232640_CanonicalizePrintJobPriority",
            "20260807023655_UsePortableRevisionConcurrency",
            "20260808054302_AddNfcDeviceApproval",
            "20260808162833_AddPowerReadingCompositeIndex",
            "20260811235527_AddRoleUpdatedAtConcurrencyToken",
            "20260812020851_HardenBedClearReplayStorage",
            "20260816094836_RenameHasHeatedChamberToCalibrationHasHeatedChamber",
            "20260820064025_AddQueueRetentionIndexes",
            "20260821152002_AddNozzleHardnessOverride",
            "20260821205923_AddNozzleMaterialCatalog",
            "20260823210035_AddPrinterModelAccelerationFields",
            "20260824020853_RenameCalibrationHasHeatedChamberToHasHeatedChamber",
            "20260825084406_MakeCalibrationAttemptSnapshotIdOptional",
            "20260825110105_RemoveDeprecatedCalibrationPrinterColumns",
            "20260825141109_DropGeneratedProfileRevisionTables",
            "20260825150550_DeletePrinterConfigurationSnapshot",
            "20260825185839_DropDeadCalibrationOrchestrationColumns",
            "20260826051847_AddPrinterModelAliasNormalizedLookup",
            "20260827005237_EnforceNormalizedPrinterModelAliasUniqueness",
            "20260827161050_AddPrinterRotationCursors");
        startupStatus.IsDatabaseSchemaReady.Should().BeTrue();
        startupStatus.Phase.Should().Be(StartupPhase.Ready);
    }

    [Fact]
    public async Task ProgramHelpersInitialization_SeedingFailure_RemainsNonFatal()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteConnection connection = database.Connection;
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        var loggerProvider = new RecordingLoggerProvider();
        _ = builder.Logging.AddProvider(loggerProvider);
        _ = builder.Services.AddDbContext<AppDbContext>(
            options => options.UseSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")));
        var initializer = new Mock<IDatabaseInitializer>();
        var seedingException = new InvalidOperationException("Synthetic seeding failure");
        _ = initializer
            .Setup(service => service.InitializeAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ThrowsAsync(seedingException);
        _ = builder.Services.AddScoped(_ => initializer.Object);
        var startupStatus = new StartupStatus();
        _ = builder.Services.AddSingleton<IStartupStatus>(startupStatus);
        await using WebApplication app = builder.Build();

        Func<Task> initialize = async () => await ProgramHelpers.InitializeDatabaseAsync(app);

        _ = await initialize.Should().NotThrowAsync();
        initializer.Verify(
            service => service.InitializeAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Once);
        startupStatus.Phase.Should().Be(
            StartupPhase.Starting,
            "reference-data seeding failure keeps startup degraded rather than reporting ready");
        startupStatus.IsDatabaseSchemaReady.Should().BeTrue(
            "database-backed infrastructure may start after migration even when optional reference-data seeding fails");
        (await TableExistsAsync(connection, "__EFMigrationsHistory")).Should().BeTrue(
            "migration and schema validation must complete before reference-data seeding");
        loggerProvider.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning
            && ReferenceEquals(entry.Exception, seedingException)
            && entry.Message == "[Startup] Database seeding failed (non-fatal)");
    }

    [Fact]
    public async Task CoreMigration_BaselinePreservesPopulatedLegacyData()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);
        _ = await context.Database.EnsureCreatedAsync();
        Guid manufacturerId = Guid.NewGuid();
        _ = context.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Preserved legacy manufacturer",
        });
        _ = await context.SaveChangesAsync();

        DatabaseMigrationResult result = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);

        _ = result.LegacySchemaBaselined.Should().BeTrue();
        _ = (await context.Manufacturers.AsNoTracking()
                .AnyAsync(manufacturer => manufacturer.Id == manufacturerId))
            .Should().BeTrue();
    }

    [Fact]
    public async Task CoreMigration_RejectsPartialLegacySchemaWithoutRecordingHistory()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using (SqliteCommand createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = "CREATE TABLE Printers (Id TEXT NOT NULL PRIMARY KEY)";
            _ = await createCommand.ExecuteNonQueryAsync();
        }

        await using AppDbContext context = CreateCoreContext(connection);

        Func<Task> migrate = async () => await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);

        DatabaseMigrationContractException exception =
            (await migrate.Should().ThrowAsync<DatabaseMigrationContractException>()).Which;
        exception.Code.Should().Be("schema_validation_failed");
        (await TableExistsAsync(connection, "__EFMigrationsHistory")).Should().BeFalse();
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithMissingIndexWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            """DROP INDEX "IX_UserTasks_SourceKind_SourceId";""",
            "UserTasks (unique index: SOURCEKIND|SOURCEID)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithWrongIndexNameWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            """
            DROP INDEX "IX_FileHealthAudits_AuditType";
            CREATE INDEX "IX_FileHealthAudits_AuditType_Wrong"
                ON "FileHealthAudits" ("AuditType");
            """,
            "FileHealthAudits (index: AUDITTYPE) " +
            "(name: IX_FILEHEALTHAUDITS_AUDITTYPE; sort: ASC; collation: BINARY; source: explicit)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithWrongIndexDirectionWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            """
            DROP INDEX "IX_FileHealthAudits_AuditType_AuditDate";
            CREATE INDEX "IX_FileHealthAudits_AuditType_AuditDate"
                ON "FileHealthAudits" ("AuditType" ASC, "AuditDate" ASC);
            """,
            "FileHealthAudits (index: AUDITTYPE|AUDITDATE) " +
            "(name: IX_FILEHEALTHAUDITS_AUDITTYPE_AUDITDATE; sort: ASC|DESC; " +
            "collation: BINARY|BINARY; source: explicit)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithWrongIndexCollationWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            """
            DROP INDEX "IX_Bins_Code";
            CREATE UNIQUE INDEX "IX_Bins_Code" ON "Bins" ("Code" COLLATE NOCASE);
            """,
            "Bins (unique index: CODE) " +
            "(name: IX_BINS_CODE; sort: ASC; collation: BINARY; source: explicit)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithWrongPrimaryKeyDirectionWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            BuildRetryPoliciesReplacementSql(
                """INTEGER NOT NULL DEFAULT 3""",
                """TEXT NOT NULL CONSTRAINT "PK_RetryPolicies" PRIMARY KEY DESC"""),
            "RetryPolicies (unique index: ID) " +
            "(name: <sqlite-autoindex>; sort: ASC; collation: BINARY; source: primary key)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithWrongPrimaryKeyCollationWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            BuildRetryPoliciesReplacementSql(
                """INTEGER NOT NULL DEFAULT 3""",
                """TEXT COLLATE NOCASE NOT NULL CONSTRAINT "PK_RetryPolicies" PRIMARY KEY"""),
            "RetryPolicies (unique index: ID) " +
            "(name: <sqlite-autoindex>; sort: ASC; collation: BINARY; source: primary key)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithWrongColumnTypeWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            BuildRetryPoliciesReplacementSql("""TEXT NOT NULL DEFAULT 3"""),
            "RetryPolicies.MaxRetries (store type)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithWrongNullabilityWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            BuildRetryPoliciesReplacementSql("""INTEGER NULL DEFAULT 3"""),
            "RetryPolicies.MaxRetries (nullability)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithoutAutoIncrementWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            """
            PRAGMA writable_schema = ON;
            UPDATE sqlite_schema
            SET sql = replace(sql, ' AUTOINCREMENT', '')
            WHERE type = 'table' AND name = 'AppSettingsEntities';
            PRAGMA writable_schema = OFF;
            """,
            "AppSettingsEntities.Id (autoincrement)");
    }

    [Fact]
    public Task CoreMigration_RejectsNullableTextPrimaryKeyWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            BuildRetryPoliciesReplacementSql(
                """INTEGER NOT NULL DEFAULT 3""",
                """TEXT CONSTRAINT "PK_RetryPolicies" PRIMARY KEY"""),
            "RetryPolicies.Id (nullability)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithWrongForeignKeyWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            BuildUserRolesReplacementSql("ON DELETE NO ACTION"),
            "UserRoles (foreign key: ROLEID -> ROLES.ID)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithWrongForeignKeyUpdateWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            BuildUserRolesReplacementSql("ON UPDATE CASCADE ON DELETE CASCADE"),
            "UserRoles (foreign key: ROLEID -> ROLES.ID)");
    }

    [Fact]
    public Task CoreMigration_RejectsLegacySchemaWithMissingCheckConstraintWithoutRecordingHistory()
    {
        return AssertCorruptedLegacySchemaRejectedAsync(
            """
            PRAGMA foreign_keys = OFF;
            CREATE TABLE "Bins_replacement" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Bins" PRIMARY KEY,
                "Code" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Location" TEXT NULL,
                "Notes" TEXT NULL,
                "IsActive" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
                /* CHECK ("Code" = UPPER("Code")) */
            );
            DROP TABLE "Bins";
            ALTER TABLE "Bins_replacement" RENAME TO "Bins";
            CREATE UNIQUE INDEX "IX_Bins_Code" ON "Bins" ("Code");
            CREATE INDEX "IX_Bins_IsActive" ON "Bins" ("IsActive");
            PRAGMA foreign_keys = ON;
            """,
            """Bins (check constraint: "Code" = UPPER("Code"))""");
    }

    [Fact]
    public async Task SlicerMigration_UsesIndependentSqliteMigrationSet()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using SlicerDbContext context = CreateSlicerContext(connection);

        DatabaseMigrationResult result = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Slicer,
            NullLogger.Instance);

        result.LegacySchemaBaselined.Should().BeFalse();
        result.AppliedMigrations.Should().Equal(
            "20260730231419_SlicerInitialV2",
            "20260807023701_UsePortableRevisionConcurrency",
            "20260809000302_AddSliceJobNormalizedEngineStatusIndex",
            "20260814001455_AddUniqueIndexOnSlicerServiceInstanceId",
            "20260821071431_AddSliceJobLayoutDegradationReason",
            "20260821151953_AddSliceJobFailureReason",
            "20260822141641_AddWorkerDisableSource",
            "20260824024658_AddSliceJobCalibrationFields",
            "20260826021050_AddCustomProfileFamilyRenderingState",
            "20260826063137_EnforceNormalizedMachineModelProfileNames");
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Proves the <c>AddWorkerDisableSource</c> backfill actually classifies pre-existing rows.
    /// </summary>
    /// <remarks>
    /// The other slicer migration tests either migrate an empty database or seed through the
    /// current model, so neither ever produces a Worker row written before the column existed —
    /// the only rows the backfill acts on. Without this the classification SQL could be deleted
    /// or misclassify every legacy row and the whole suite would still pass.
    ///
    /// The stakes are asymmetric: a row left <c>None</c> is an administrator ban the next
    /// registration silently lifts, and a row wrongly promoted to <c>Administrator</c> is a
    /// worker the stale sweep then refuses to collect forever.
    /// </remarks>
    [Fact]
    public async Task SlicerMigration_AddWorkerDisableSource_AttributesLegacyDisablesFromReasonText()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using SlicerDbContext context = CreateSlicerContext(connection);

        // Stop one migration short of the column so the inserts below are genuinely legacy rows.
        await context.GetService<IMigrator>().MigrateAsync("20260821151953_AddSliceJobFailureReason");

        await InsertLegacyWorkerAsync(connection, "admin-ban", isDisabled: true, reason: "Banned by an operator");
        await InsertLegacyWorkerAsync(connection, "admin-ban-oddly-worded", isDisabled: true, reason: "circuit breaker tripped too often, benching it");
        await InsertLegacyWorkerAsync(connection, "deregistered", isDisabled: true, reason: "Slicer service deregistered");
        await InsertLegacyWorkerAsync(connection, "circuit-breaker", isDisabled: true, reason: "Circuit breaker: 5 failures in 60s");
        await InsertLegacyWorkerAsync(connection, "blank-reason", isDisabled: true, reason: "   ");
        await InsertLegacyWorkerAsync(connection, "null-reason", isDisabled: true, reason: null);
        await InsertLegacyWorkerAsync(connection, "enabled", isDisabled: false, reason: null);
        await InsertLegacyWorkerAsync(connection, "enabled-with-stale-reason", isDisabled: false, reason: "Banned by an operator");

        await context.GetService<IMigrator>().MigrateAsync();

        Dictionary<string, int> sources = await ReadDisableSourcesAsync(connection);

        _ = sources["admin-ban"].Should().Be((int)WorkerDisableSource.Administrator,
            "an administrator's ban is the one thing the backfill must never lose");
        _ = sources["deregistered"].Should().Be((int)WorkerDisableSource.Deregistration);
        _ = sources["circuit-breaker"].Should().Be((int)WorkerDisableSource.CircuitBreaker);

        // 'Circuit breaker:' with the colon is the literal the breaker writes. This row only
        // resembles it, so it must stay an administrator ban rather than be swept as automatic.
        _ = sources["admin-ban-oddly-worded"].Should().Be((int)WorkerDisableSource.Administrator,
            "only the exact automatic prefix may be treated as automatic");

        // No reason text is no evidence of an administrator, and DisableWorkerAsync rejects a
        // blank reason, so these cannot be bans. Leaving them clearable is the safe direction.
        _ = sources["blank-reason"].Should().Be((int)WorkerDisableSource.None);
        _ = sources["null-reason"].Should().Be((int)WorkerDisableSource.None);

        // Enabled rows must be untouched even when they still carry a stale reason, or the
        // stale sweep would refuse to collect a worker that is not banned at all.
        _ = sources["enabled"].Should().Be((int)WorkerDisableSource.None);
        _ = sources["enabled-with-stale-reason"].Should().Be((int)WorkerDisableSource.None,
            "the backfill is guarded on IsDisabled, not on the reason text alone");
    }

    /// <summary>
    /// Pins the shape of the backfill in every dialect.
    /// </summary>
    /// <remarks>
    /// PostgreSQL and SQL Server need this because their SQL is hand-written per provider, cannot
    /// run against SQLite, and the live-provider jobs only migrate an empty database.
    ///
    /// SQLite needs it too, despite the behavioural test above, because that test cannot see a
    /// missing exclusion: dropping <c>NOT LIKE 'Circuit breaker:%'</c> from the administrator pass
    /// leaves it over-broad, but the later circuit-breaker pass overwrites the row and hides it.
    /// The exclusions are what make the three passes order-independent, so they are asserted
    /// directly rather than inferred from the end state.
    /// </remarks>
    [Theory]
    [InlineData("postgres", "Farm.Slicer.Migrations.PostgreSQL", "20260821151926_AddSliceJobFailureReason", "20260822141629_AddWorkerDisableSource")]
    [InlineData("sqlserver", "Farm.Slicer.Migrations.SqlServer", "20260821151939_AddSliceJobFailureReason", "20260822141635_AddWorkerDisableSource")]
    [InlineData("sqlite", "Farm.Slicer.Migrations.Sqlite", "20260821151953_AddSliceJobFailureReason", "20260822141641_AddWorkerDisableSource")]
    public void SlicerMigration_AddWorkerDisableSource_ClassifiesEveryCategoryOnEveryProvider(
        string provider,
        string slicerAssembly,
        string fromMigration,
        string toMigration)
    {
        DbContextOptionsBuilder<SlicerDbContext> options = new();
        if (provider == "postgres")
        {
            _ = options.UseNpgsql(
                "Host=localhost;Database=printfarmer;Username=test;******",
                npgsql => npgsql.MigrationsAssembly(slicerAssembly));
        }
        else if (provider == "sqlserver")
        {
            _ = options.UseSqlServer(
                "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                sqlServer => sqlServer.MigrationsAssembly(slicerAssembly));
        }
        else
        {
            _ = options.UseSqlite(
                "Data Source=:memory:",
                sqlite => sqlite.MigrationsAssembly(slicerAssembly));
        }

        using SlicerDbContext slicer = new(options.Options);
        string script = slicer.GetService<IMigrator>().GenerateScript(fromMigration, toMigration);

        // Normalise away the per-provider identifier quoting so one assertion covers all three.
        string normalized = script
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);

        _ = normalized.Should().Contain("DisableSource = 1", "administrator bans must be backfilled");
        _ = normalized.Should().Contain("DisableSource = 2", "deregistration disables must be backfilled");
        _ = normalized.Should().Contain("DisableSource = 3", "circuit-breaker disables must be backfilled");

        // The administrator pass is the dangerous one: it must exclude both automatic patterns,
        // or a circuit-broken worker is promoted to a ban the stale sweep will never collect.
        _ = normalized.Should().Contain("DisabledReason <> 'Slicer service deregistered'");
        _ = normalized.Should().Contain("DisabledReason NOT LIKE 'Circuit breaker:%'");
    }

    private static async Task InsertLegacyWorkerAsync(
        SqliteConnection connection,
        string name,
        bool isDisabled,
        string? reason)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "Workers"
                ("Id", "ActiveJobs", "ArtifactBytesProduced", "ArtifactsProduced", "CapabilitiesJson",
                 "CompletedJobs", "CreatedAt", "DisabledReason", "EndpointUrl", "FailedJobs",
                 "IsDisabled", "Name", "RegisteredAt", "ServiceId", "Status", "TotalSlots", "UpdatedAt")
            VALUES
                ($id, 0, 0, 0, '[]', 0, $now, $reason, 'http://worker.invalid', 0,
                 $isDisabled, $name, $now, $serviceId, 'Online', 1, $now);
            """;
        _ = command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        _ = command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        _ = command.Parameters.AddWithValue("$reason", reason is null ? DBNull.Value : reason);
        _ = command.Parameters.AddWithValue("$isDisabled", isDisabled ? 1 : 0);
        _ = command.Parameters.AddWithValue("$name", name);
        _ = command.Parameters.AddWithValue("$serviceId", Guid.NewGuid().ToString());
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, int>> ReadDisableSourcesAsync(SqliteConnection connection)
    {
        Dictionary<string, int> sources = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """SELECT "Name", "DisableSource" FROM "Workers";""";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sources[reader.GetString(0)] = reader.GetInt32(1);
        }

        return sources;
    }

    [Fact]
    public async Task SlicerMigration_BaselinePreservesPopulatedLegacyData()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using SlicerDbContext context = CreateSlicerContext(connection);
        _ = await context.Database.EnsureCreatedAsync();
        Guid jobId = Guid.NewGuid();
        _ = context.SliceJobs.Add(new SliceJob
        {
            Id = jobId,
            UserId = Guid.NewGuid(),
            ModelFileUrl = "file:///legacy/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = (int)SlicerType.OrcaSlicer,
            Status = SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = await context.SaveChangesAsync();

        DatabaseMigrationResult result = await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Slicer,
            NullLogger.Instance);

        _ = result.LegacySchemaBaselined.Should().BeTrue();
        _ = (await context.SliceJobs.AsNoTracking()
                .AnyAsync(job => job.Id == jobId))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("postgres", "Farm.Migrations.PostgreSQL", "Farm.Slicer.Migrations.PostgreSQL")]
    [InlineData("sqlserver", "Farm.Migrations.SqlServer", "Farm.Slicer.Migrations.SqlServer")]
    public void ServerProviderMigrationSets_ArePresentAndIndependent(
        string provider,
        string coreAssembly,
        string slicerAssembly)
    {
        DbContextOptionsBuilder<AppDbContext> coreOptions = new();
        DbContextOptionsBuilder<SlicerDbContext> slicerOptions = new();

        if (provider == "postgres")
        {
            _ = coreOptions.UseNpgsql(
                "Host=localhost;Database=printfarmer;Username=test;Password=test",
                options => options.MigrationsAssembly(coreAssembly));
            _ = slicerOptions.UseNpgsql(
                "Host=localhost;Database=printfarmer;Username=test;Password=test",
                options => options.MigrationsAssembly(slicerAssembly));
        }
        else
        {
            _ = coreOptions.UseSqlServer(
                "Server=localhost;Database=printfarmer;User Id=test;Password=test;TrustServerCertificate=true",
                options => options.MigrationsAssembly(coreAssembly));
            _ = slicerOptions.UseSqlServer(
                "Server=localhost;Database=printfarmer;User Id=test;Password=test;TrustServerCertificate=true",
                options => options.MigrationsAssembly(slicerAssembly));
        }

        using AppDbContext core = new(coreOptions.Options);
        using SlicerDbContext slicer = new(slicerOptions.Options);
        string[] coreMigrations = [.. core.Database.GetMigrations()];
        string[] slicerMigrations = [.. slicer.Database.GetMigrations()];

        _ = core.GetService<IMigrationsAssembly>().Assembly.GetName().Name
            .Should().Be(coreAssembly);
        _ = slicer.GetService<IMigrationsAssembly>().Assembly.GetName().Name
            .Should().Be(slicerAssembly);
        _ = coreMigrations.Should().NotBeEmpty();
        _ = slicerMigrations.Should().NotBeEmpty();
        _ = coreMigrations.Should().NotIntersectWith(slicerMigrations);

        string[] expectedCoreMigrations = provider == "postgres"
            ?
            [
                "20260730231346_InitialV2",
                "20260806230920_CanonicalizePrintJobPriority",
                "20260807023649_UsePortableRevisionConcurrency",
                "20260808052051_AddNfcDeviceApproval",
                "20260808162502_AddPowerReadingCompositeIndex",
                "20260811230934_AddRoleUpdatedAtConcurrencyToken",
                "20260812020851_HardenBedClearReplayStorage",
                "20260816094354_RenameHasHeatedChamberToCalibrationHasHeatedChamber",
                "20260820051034_AddQueueRetentionIndexes",
                "20260821151937_AddNozzleHardnessOverride",
                "20260821205704_AddNozzleMaterialCatalog",
                "20260823205528_AddPrinterModelAccelerationFields",
                "20260824020759_RenameCalibrationHasHeatedChamberToHasHeatedChamber",
                "20260825084151_MakeCalibrationAttemptSnapshotIdOptional",
                "20260825110032_RemoveDeprecatedCalibrationPrinterColumns",
                "20260825141045_DropGeneratedProfileRevisionTables",
                "20260825150521_DeletePrinterConfigurationSnapshot",
                "20260825185802_DropDeadCalibrationOrchestrationColumns",
                "20260826051825_AddPrinterModelAliasNormalizedLookup",
                "20260827005201_EnforceNormalizedPrinterModelAliasUniqueness",
                "20260827161010_AddPrinterRotationCursors",
            ]
            :
            [
                "20260730231359_InitialV2",
                "20260806230929_CanonicalizePrintJobPriority",
                "20260807023652_UsePortableRevisionConcurrency",
                "20260808052059_AddNfcDeviceApproval",
                "20260808162518_AddPowerReadingCompositeIndex",
                "20260811230948_AddRoleUpdatedAtConcurrencyToken",
                "20260812011119_UseBinaryBedClearIdempotencyKeys",
                "20260812020851_HardenBedClearReplayStorage",
                "20260816094405_RenameHasHeatedChamberToCalibrationHasHeatedChamber",
                "20260820051046_AddQueueRetentionIndexes",
                "20260821151949_AddNozzleHardnessOverride",
                "20260821205829_AddNozzleMaterialCatalog",
                "20260823205544_AddPrinterModelAccelerationFields",
                "20260824020821_RenameCalibrationHasHeatedChamberToHasHeatedChamber",
                "20260825084201_MakeCalibrationAttemptSnapshotIdOptional",
                "20260825110042_RemoveDeprecatedCalibrationPrinterColumns",
                "20260825141057_DropGeneratedProfileRevisionTables",
                "20260825150540_DeletePrinterConfigurationSnapshot",
                "20260825185812_DropDeadCalibrationOrchestrationColumns",
                "20260826051836_AddPrinterModelAliasNormalizedLookup",
                "20260827005219_EnforceNormalizedPrinterModelAliasUniqueness",
                "20260827161031_AddPrinterRotationCursors",
            ];
        _ = coreMigrations.Should().Equal(expectedCoreMigrations,
            $"the {provider} core migration set must apply in the exact recorded order, including provider-specific schema guarantees");

        string[] expectedSlicerMigrations = provider == "postgres"
            ?
            [
                "20260730231413_SlicerInitialV2",
                "20260807023657_UsePortableRevisionConcurrency",
                "20260809000323_AddSliceJobNormalizedEngine",
                "20260809000341_AddSliceJobNormalizedEngineStatusIndex",
                "20260814001416_AddUniqueIndexOnSlicerServiceInstanceId",
                "20260821065610_AddSliceJobLayoutDegradationReason",
                "20260821151926_AddSliceJobFailureReason",
                "20260822141629_AddWorkerDisableSource",
                "20260824023212_AddSliceJobCalibrationFields",
                "20260826021028_AddCustomProfileFamilyRenderingState",
                "20260826063137_EnforceNormalizedMachineModelProfileNames",
            ]
            :
            [
                "20260730231416_SlicerInitialV2",
                "20260807023659_UsePortableRevisionConcurrency",
                "20260809000253_AddSliceJobNormalizedEngineStatusIndex",
                "20260814001437_AddUniqueIndexOnSlicerServiceInstanceId",
                "20260821065621_AddSliceJobLayoutDegradationReason",
                "20260821151939_AddSliceJobFailureReason",
                "20260822141635_AddWorkerDisableSource",
                "20260824023226_AddSliceJobCalibrationFields",
                "20260826021040_AddCustomProfileFamilyRenderingState",
                "20260826063137_EnforceNormalizedMachineModelProfileNames",
            ];
        _ = slicerMigrations.Should().Equal(expectedSlicerMigrations,
            $"the {provider} slicer migration set must apply in the exact recorded order");
    }

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static AppDbContext CreateCoreContext(SqliteConnection connection)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options;
        return new AppDbContext(options);
    }

    private static AppDbContext CreateCoreContextWithoutMigrations(SqliteConnection connection)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        return new AppDbContext(options);
    }

    private static ArbitraryBaselineDbContext CreateArbitraryBaselineContext(
        SqliteConnection connection)
    {
        string migrationAssembly = typeof(DatabaseMigrationTests).Assembly.GetName().Name!;
        DbContextOptions<ArbitraryBaselineDbContext> options =
            new DbContextOptionsBuilder<ArbitraryBaselineDbContext>()
                .UseSqlite(
                    connection,
                    sqlite => sqlite.MigrationsAssembly(migrationAssembly))
                .Options;
        return new ArbitraryBaselineDbContext(options);
    }

    private static SlicerDbContext CreateSlicerContext(SqliteConnection connection)
    {
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"))
            .Options;
        return new SlicerDbContext(options);
    }

    private static WebApplication CreateTestApplication(
        SqliteConnection connection,
        Mock<IDatabaseInitializer> initializer,
        StartupStatus startupStatus)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        _ = builder.Services.AddLogging();
        _ = builder.Services.AddDbContext<AppDbContext>(
            options => options.UseSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")));
        _ = builder.Services.AddScoped(_ => initializer.Object);
        _ = builder.Services.AddSingleton<IStartupStatus>(startupStatus);
        return builder.Build();
    }

    private static async Task EnsureLegacySchemaAsync(WebApplication app)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _ = await context.Database.EnsureCreatedAsync();
    }

    private static async Task ExecuteSqlAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await EnsureConnectionOpenAsync(connection);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @tableName)";
        _ = command.Parameters.AddWithValue("@tableName", tableName);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToBoolean(result);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName)
    {
        await EnsureConnectionOpenAsync(connection);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = tableName switch
        {
            "DeviceTokens" =>
                "SELECT 1 FROM pragma_table_info('DeviceTokens') WHERE name = @columnName",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, null),
        };
        _ = command.Parameters.AddWithValue("@columnName", columnName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<IReadOnlyList<string>> ReadAppliedMigrationIdsAsync(
        SqliteConnection connection)
    {
        await EnsureConnectionOpenAsync(connection);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";""";
        var migrationIds = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            migrationIds.Add(reader.GetString(0));
        }

        return migrationIds;
    }

    private static async Task EnsureConnectionOpenAsync(SqliteConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
    }

    private static async Task AssertCorruptedLegacySchemaRejectedAsync(
        string corruptionSql,
        string expectedDetail)
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        await using AppDbContext context = CreateCoreContext(connection);
        _ = await context.Database.EnsureCreatedAsync();
        await using (SqliteCommand corruptCommand = connection.CreateCommand())
        {
            corruptCommand.CommandText = corruptionSql;
            _ = await corruptCommand.ExecuteNonQueryAsync();
        }

        Func<Task> migrate = async () => await ProviderAwareMigrationRunner.MigrateAsync(
            context,
            DatabaseMigrationTarget.Core,
            NullLogger.Instance);

        DatabaseMigrationContractException exception =
            (await migrate.Should().ThrowAsync<DatabaseMigrationContractException>()).Which;
        exception.Code.Should().Be("schema_validation_failed");
        exception.Message.Should().Contain(expectedDetail);
        (await TableExistsAsync(connection, "__EFMigrationsHistory")).Should().BeFalse();
    }

    private static string BuildRetryPoliciesReplacementSql(
        string maxRetriesDefinition,
        string idDefinition = """TEXT NOT NULL CONSTRAINT "PK_RetryPolicies" PRIMARY KEY""")
    {
        return $"""
            PRAGMA foreign_keys = OFF;
            CREATE TABLE "RetryPolicies_replacement" (
                "Id" {idDefinition},
                "IsEnabled" INTEGER NOT NULL DEFAULT 1,
                "MaxRetries" {maxRetriesDefinition},
                "InitialDelaySeconds" INTEGER NOT NULL DEFAULT 60,
                "ExponentialBase" REAL NOT NULL DEFAULT 2.0,
                "MaxDelaySeconds" INTEGER NOT NULL DEFAULT 3600,
                "RetryOnErrorCategories" TEXT NOT NULL DEFAULT 'Recoverable',
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            DROP TABLE "RetryPolicies";
            ALTER TABLE "RetryPolicies_replacement" RENAME TO "RetryPolicies";
            PRAGMA foreign_keys = ON;
            """;
    }

    private static string BuildUserRolesReplacementSql(string roleForeignKeyActions)
    {
        return $"""
            PRAGMA foreign_keys = OFF;
            CREATE TABLE "UserRoles_replacement" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_UserRoles" PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "RoleId" TEXT NOT NULL,
                "AssignedAt" TEXT NOT NULL,
                "ExpiresAt" TEXT NULL,
                "IsActive" INTEGER NOT NULL,
                CONSTRAINT "FK_UserRoles_Roles_RoleId"
                    FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") {roleForeignKeyActions},
                CONSTRAINT "FK_UserRoles_Users_UserId"
                    FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
            );
            DROP TABLE "UserRoles";
            ALTER TABLE "UserRoles_replacement" RENAME TO "UserRoles";
            CREATE INDEX "IX_UserRoles_ExpiresAt" ON "UserRoles" ("ExpiresAt");
            CREATE INDEX "IX_UserRoles_IsActive" ON "UserRoles" ("IsActive");
            CREATE INDEX "IX_UserRoles_RoleId" ON "UserRoles" ("RoleId");
            CREATE UNIQUE INDEX "IX_UserRoles_UserId_RoleId" ON "UserRoles" ("UserId", "RoleId");
            PRAGMA foreign_keys = ON;
            """;
    }
}

internal sealed class ArbitraryBaselineDbContext(
    DbContextOptions<ArbitraryBaselineDbContext> options) : DbContext(options)
{
    public DbSet<ArbitraryBaselineEntity> BaselineEntities => Set<ArbitraryBaselineEntity>();
}

internal sealed class ArbitraryBaselineEntity
{
    public int Id { get; set; }
}

internal sealed class TemporarySqliteDatabase : IAsyncDisposable
{
    private TemporarySqliteDatabase(string path, SqliteConnection connection)
    {
        Path = path;
        Connection = connection;
    }

    internal SqliteConnection Connection { get; }

    private string Path { get; }

    internal static async Task<TemporarySqliteDatabase> CreateAsync()
    {
        string path = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            $"printfarmer-database-migration-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        return new TemporarySqliteDatabase(path, connection);
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        File.Delete(Path);
    }
}

internal sealed record RecordedLogEntry(
    LogLevel Level,
    Exception? Exception,
    string Message);

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    internal List<RecordedLogEntry> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName)
    {
        return new RecordingLogger(Entries);
    }

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(List<RecordedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Add(new RecordedLogEntry(logLevel, exception, formatter(state, exception)));
        }
    }
}

[DbContext(typeof(ArbitraryBaselineDbContext))]
[Migration(MigrationId)]
internal sealed class ArbitraryNamedBaselineMigration : Migration
{
    public const string MigrationId = "20200101000000_ZzzArbitraryBaseline";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.CreateTable(
            name: "BaselineEntities",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_BaselineEntities", entity => entity.Id);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.DropTable(name: "BaselineEntities");
    }
}
