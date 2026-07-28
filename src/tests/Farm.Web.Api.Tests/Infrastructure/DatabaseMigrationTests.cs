using Farm.Infrastructure.Data;
using Farm.Infrastructure.Data.Migrations;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;

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
            "20260725032040_InitialV1",
            "20260725085243_AddCalibrationPrinterContext",
            "20260725144853_AddGcodePromotionLineage",
            "20260725173426_AlignDevelopmentAppSchema",
            "20260725184947_AddOwnerScopedPromotionOperationKey",
            "20260725203646_AddCalibrationPersistenceSync",
            "20260725204532_AddCalibrationGenerationOrchestration",
            "20260726090013_ReconcileEpic705AppSchema");
        second.LegacySchemaBaselined.Should().BeFalse();
        second.AppliedMigrations.Should().BeEquivalentTo(first.AppliedMigrations);
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
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

        Func<Task> migrate = () => ProviderAwareMigrationRunner.MigrateAsync(
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
            "20260725032053_InitialV1",
            "20260725095108_AddCalibrationProfileIdentity",
            "20260725140244_AddSliceJobLeaseAndCalibrationProvenance",
            "20260725144915_AddArtifactPromotionCoordination",
            "20260725173232_AlignDevelopmentSlicerSchema",
            "20260725185010_AddOwnerScopedPromotionOperationKey",
            "20260726084205_AddSliceJobSlicerEngineVersion",
            "20260726170804_AddSliceJobClaimIncarnation",
            "20260728212624_AddWorkerAttestationAndCleanupReservation",
            "20260728230034_AddArtifactCleanupDeletionState");
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("sqlite", "Farm.Slicer.Migrations.Sqlite", false)]
    [InlineData("postgres", "Farm.Slicer.Migrations.PostgreSQL", false)]
    [InlineData("sqlserver", "Farm.Slicer.Migrations.SqlServer", true)]
    public void SliceJobLeaseMigration_ForEveryProvider_AddsFencingAndOwnerScopedUniqueness(
        string provider,
        string migrationAssembly,
        bool expectsFilteredIndexes)
    {
        DbContextOptionsBuilder<SlicerDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                _ = options.UseSqlite(
                    "Data Source=:memory:",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            case "postgres":
                _ = options.UseNpgsql(
                    "Host=localhost;Database=printfarmer;Username=test;******",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            default:
                _ = options.UseSqlServer(
                    "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
        }

        using SlicerDbContext context = new(options.Options);
        IMigrationsAssembly migrationsAssembly = context.GetService<IMigrationsAssembly>();
        Migration migration = CreateMigration(
            migrationsAssembly,
            context,
            "_AddSliceJobLeaseAndCalibrationProvenance");

        string[] addedColumns = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Where(operation => operation.Table == "SliceJobs")
            .Select(operation => operation.Name)
            .ToArray();
        _ = addedColumns.Should().Contain(
            nameof(SliceJob.LeaseToken),
            nameof(SliceJob.LeaseFence),
            nameof(SliceJob.Model3DId),
            nameof(SliceJob.ModelSha256),
            nameof(SliceJob.SlicerEngineName),
            nameof(SliceJob.MachineProfileJson),
            nameof(SliceJob.ProcessProfileJson),
            nameof(SliceJob.FilamentProfileJson),
            nameof(SliceJob.MachineProfileSha256),
            nameof(SliceJob.CalibrationProjectId),
            nameof(SliceJob.IdempotencyScopeId));

        // Every added column must be nullable or defaulted so existing non-calibration jobs survive.
        _ = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Should().OnlyContain(operation => operation.IsNullable || operation.DefaultValue != null);

        CreateIndexOperation[] uniqueIndexes = migration.UpOperations
            .OfType<CreateIndexOperation>()
            .Where(operation => operation.IsUnique)
            .ToArray();
        _ = uniqueIndexes.Select(operation => operation.Name).Should().BeEquivalentTo(
            "IX_SliceJobs_Owner_Project_Correlation",
            "IX_SliceJobs_Owner_Project_Checksum");
        _ = uniqueIndexes.Should().OnlyContain(operation =>
            operation.Columns.Contains(nameof(SliceJob.UserId)) &&
            operation.Columns.Contains(nameof(SliceJob.IdempotencyScopeId)));

        // Only SQL Server treats nulls as duplicates, so only it needs a filter.
        _ = uniqueIndexes.Should().OnlyContain(operation =>
            expectsFilteredIndexes ? operation.Filter != null : operation.Filter == null);
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
    }

    [Theory]
    [InlineData("sqlite", "Farm.Migrations.Sqlite")]
    [InlineData("postgres", "Farm.Migrations.PostgreSQL")]
    [InlineData("sqlserver", "Farm.Migrations.SqlServer")]
    public void CalibrationContextMigration_ForEveryProvider_UsesSafeLegacyDefaults(
        string provider,
        string migrationAssembly)
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                _ = options.UseSqlite(
                    "Data Source=:memory:",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            case "postgres":
                _ = options.UseNpgsql(
                    "Host=localhost;Database=printfarmer;Username=test;******",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            default:
                _ = options.UseSqlServer(
                    "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
        }

        using AppDbContext context = new(options.Options);
        IMigrationsAssembly migrationsAssembly = context.GetService<IMigrationsAssembly>();
        KeyValuePair<string, System.Reflection.TypeInfo> migrationDefinition =
            migrationsAssembly.Migrations.Single(migration =>
                migration.Key.EndsWith(
                    "_AddCalibrationPrinterContext",
                    StringComparison.Ordinal));
        Migration migration = migrationsAssembly.CreateMigration(
            migrationDefinition.Value,
            context.Database.ProviderName!);
        Dictionary<string, AddColumnOperation> printerColumns = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Where(operation => operation.Table == "Printers")
            .ToDictionary(operation => operation.Name, StringComparer.Ordinal);

        _ = printerColumns[nameof(Printer.ConfigurationRevision)].DefaultValue
            .Should().Be(1L);
        _ = printerColumns[nameof(Printer.FirmwareFamily)].DefaultValue
            .Should().Be(0);
        _ = printerColumns[nameof(Printer.GcodeDialect)].DefaultValue
            .Should().Be(0);
        _ = printerColumns[nameof(Printer.FirmwareDetectionSource)].DefaultValue
            .Should().Be(0);
        _ = printerColumns[nameof(Printer.FirmwareIdentityVerified)].DefaultValue
            .Should().Be(false);
    }

    [Theory]
    [InlineData("sqlite", "Farm.Migrations.Sqlite")]
    [InlineData("postgres", "Farm.Migrations.PostgreSQL")]
    [InlineData("sqlserver", "Farm.Migrations.SqlServer")]
    public void CalibrationPersistenceMigration_ForEveryProvider_CreatesCoreTopology(
        string provider,
        string migrationAssembly)
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                _ = options.UseSqlite(
                    "Data Source=:memory:",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            case "postgres":
                _ = options.UseNpgsql(
                    "Host=localhost;Database=printfarmer;Username=test;******",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            default:
                _ = options.UseSqlServer(
                    "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
        }

        using AppDbContext context = new(options.Options);
        IMigrationsAssembly migrationsAssembly = context.GetService<IMigrationsAssembly>();
        KeyValuePair<string, System.Reflection.TypeInfo> migrationDefinition =
            migrationsAssembly.Migrations.Single(migration =>
                migration.Key.EndsWith(
                    "_AddCalibrationPersistenceSync",
                    StringComparison.Ordinal));
        Migration migration = migrationsAssembly.CreateMigration(
            migrationDefinition.Value,
            context.Database.ProviderName!);
        string[] tables = migration.UpOperations
            .OfType<CreateTableOperation>()
            .Select(operation => operation.Name)
            .ToArray();

        _ = tables.Should().Contain(
            "CalibrationProjects",
            "PrinterConfigurationSnapshots",
            "CalibrationDrafts",
            "CalibrationAttempts",
            "CalibrationAttemptEvents",
            "CalibrationObservations",
            "CalibrationPhotos",
            "CalibrationBlobCleanups",
            "GeneratedProfileRevisions",
            "GeneratedProfileRevisionOperations",
            "CalibrationIdempotencyRecords",
            "CalibrationOrchestrations",
            "CalibrationChanges",
            "CalibrationChangeFeedStates",
            "CalibrationSyncCursors");
        _ = migration.UpOperations
            .OfType<AddForeignKeyOperation>()
            .Should()
            .NotContain(operation =>
                operation.PrincipalTable.Contains("Slicer", StringComparison.OrdinalIgnoreCase) ||
                operation.PrincipalTable.Contains("Artifact", StringComparison.OrdinalIgnoreCase));

        foreach (string tableName in new[]
                 {
                     "CalibrationAttemptEvents",
                     "CalibrationObservations",
                     "CalibrationOrchestrations",
                     "CalibrationPhotos",
                 })
        {
            CreateTableOperation table = migration.UpOperations
                .OfType<CreateTableOperation>()
                .Single(operation => operation.Name == tableName);
            _ = table.ForeignKeys.Single(foreignKey =>
                    foreignKey.PrincipalTable == "CalibrationProjects")
                .OnDelete.Should().Be(ReferentialAction.Restrict);
            _ = table.ForeignKeys.Single(foreignKey =>
                    foreignKey.PrincipalTable == "CalibrationAttempts")
                .OnDelete.Should().Be(ReferentialAction.Cascade);
        }

        _ = migration.UpOperations
            .OfType<InsertDataOperation>()
            .Should().ContainSingle(operation =>
                operation.Table == "CalibrationChangeFeedStates");
    }

    [Theory]
    [InlineData("sqlite", "Farm.Slicer.Migrations.Sqlite")]
    [InlineData("postgres", "Farm.Slicer.Migrations.PostgreSQL")]
    [InlineData("sqlserver", "Farm.Slicer.Migrations.SqlServer")]
    public void CalibrationProfileIdentityMigration_ForEveryProvider_LeavesLegacyIdentityUnknown(
        string provider,
        string migrationAssembly)
    {
        DbContextOptionsBuilder<SlicerDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                _ = options.UseSqlite(
                    "Data Source=:memory:",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            case "postgres":
                _ = options.UseNpgsql(
                    "Host=localhost;Database=printfarmer;Username=test;******",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            default:
                _ = options.UseSqlServer(
                    "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
        }

        using SlicerDbContext context = new(options.Options);
        IMigrationsAssembly migrationsAssembly = context.GetService<IMigrationsAssembly>();
        KeyValuePair<string, System.Reflection.TypeInfo> migrationDefinition =
            migrationsAssembly.Migrations.Single(migration =>
                migration.Key.EndsWith(
                    "_AddCalibrationProfileIdentity",
                    StringComparison.Ordinal));
        Migration migration = migrationsAssembly.CreateMigration(
            migrationDefinition.Value,
            context.Database.ProviderName!);
        AddColumnOperation[] identityColumns = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Where(operation =>
                operation.Name is
                    nameof(MachineProfile.SlicerDistribution) or
                    nameof(MachineProfile.ProfileFormat))
            .ToArray();

        _ = identityColumns.Should().HaveCount(6);
        _ = identityColumns.Select(operation => operation.Table).Should().BeEquivalentTo(
            "MachineProfiles",
            "MachineProfiles",
            "ProcessProfiles",
            "ProcessProfiles",
            "FilamentProfiles",
            "FilamentProfiles");
        _ = identityColumns.Should().OnlyContain(operation =>
            operation.IsNullable && operation.DefaultValue == null);
    }

    [Theory]
    [InlineData("sqlite", "Farm.Migrations.Sqlite", false)]
    [InlineData("postgres", "Farm.Migrations.PostgreSQL", false)]
    [InlineData("sqlserver", "Farm.Migrations.SqlServer", true)]
    public void PromotionLineageMigration_ForEveryProvider_AddsOutboxAndSafeLineageColumns(
        string provider,
        string migrationAssembly,
        bool expectsFilteredIndexes)
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                _ = options.UseSqlite(
                    "Data Source=:memory:",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            case "postgres":
                _ = options.UseNpgsql(
                    "Host=localhost;Database=printfarmer;Username=test;******",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            default:
                _ = options.UseSqlServer(
                    "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
        }

        using AppDbContext context = new(options.Options);
        Migration migration = CreateMigration(
            context.GetService<IMigrationsAssembly>(),
            context,
            "_AddGcodePromotionLineage");

        string[] lineageColumns = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Where(operation => operation.Table == "GcodeFiles")
            .Select(operation => operation.Name)
            .ToArray();
        _ = lineageColumns.Should().Contain(
            nameof(GcodeFile.SourceArtifactId),
            nameof(GcodeFile.SourceSliceJobId),
            nameof(GcodeFile.CalibrationAttemptId),
            nameof(GcodeFile.CalibrationOrchestrationId),
            nameof(GcodeFile.PromotionOperationId),
            nameof(GcodeFile.ContentSha256),
            nameof(GcodeFile.SpecificationSha256),
            nameof(GcodeFile.SlicerContainerDigest),
            nameof(GcodeFile.FirmwareFamily),
            nameof(GcodeFile.CalibrationManifestJson),
            nameof(GcodeFile.IsImmutable));

        // Existing library rows have no promotion lineage, so every added column stays optional.
        _ = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Should().OnlyContain(operation => operation.IsNullable || operation.DefaultValue != null);

        _ = migration.UpOperations
            .OfType<CreateTableOperation>()
            .Select(operation => operation.Name)
            .Should().ContainSingle()
            .Which.Should().Be("GcodePromotionCheckpoints");
        _ = migration.UpOperations
            .OfType<AddForeignKeyOperation>()
            .Should().NotContain(operation =>
                operation.PrincipalTable.Contains("Artifact", StringComparison.OrdinalIgnoreCase) ||
                operation.PrincipalTable.Contains("SliceJob", StringComparison.OrdinalIgnoreCase));

        CreateIndexOperation[] uniqueIndexes = migration.UpOperations
            .OfType<CreateIndexOperation>()
            .Where(operation => operation.IsUnique)
            .ToArray();
        _ = uniqueIndexes.Select(operation => operation.Name).Should().Contain(
            "IX_GcodeFiles_SourceArtifactId_ContentSha256",
            "IX_GcodePromotionCheckpoints_OperationScope_OperationId",
            "IX_GcodePromotionCheckpoints_SourceArtifactId_SourceContentSha256");

        // Only SQL Server treats nulls as duplicates, so only it needs the null-tolerant filter that
        // keeps existing non-promoted rows valid.
        CreateIndexOperation[] nullableUniqueIndexes = uniqueIndexes
            .Where(operation => operation.Table == "GcodeFiles")
            .ToArray();
        _ = nullableUniqueIndexes.Should().OnlyContain(operation =>
            expectsFilteredIndexes ? operation.Filter != null : operation.Filter == null);
    }

    [Theory]
    [InlineData("sqlite", "Farm.Migrations.Sqlite", "GcodeFiles")]
    [InlineData("postgres", "Farm.Migrations.PostgreSQL", "GcodeFiles")]
    [InlineData("sqlserver", "Farm.Migrations.SqlServer", "GcodeFiles")]
    public void OwnerScopedPromotionKeyMigration_ForEveryCoreProvider_ReplacesGlobalOperationUniqueness(
        string provider,
        string migrationAssembly,
        string table)
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                _ = options.UseSqlite(
                    "Data Source=:memory:",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            case "postgres":
                _ = options.UseNpgsql(
                    "Host=localhost;Database=printfarmer;Username=test;******",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            default:
                _ = options.UseSqlServer(
                    "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
        }

        using AppDbContext context = new(options.Options);
        Migration migration = CreateMigration(
            context.GetService<IMigrationsAssembly>(),
            context,
            "_AddOwnerScopedPromotionOperationKey");

        AssertOperationKeyReplacesGlobalUniqueness(migration, table, nameof(GcodeFile.PromotionOperationKey));
    }

    [Theory]
    [InlineData("sqlite", "Farm.Slicer.Migrations.Sqlite", "Artifacts")]
    [InlineData("postgres", "Farm.Slicer.Migrations.PostgreSQL", "Artifacts")]
    [InlineData("sqlserver", "Farm.Slicer.Migrations.SqlServer", "Artifacts")]
    public void OwnerScopedPromotionKeyMigration_ForEverySlicerProvider_ReplacesGlobalOperationUniqueness(
        string provider,
        string migrationAssembly,
        string table)
    {
        DbContextOptionsBuilder<SlicerDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                _ = options.UseSqlite(
                    "Data Source=:memory:",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            case "postgres":
                _ = options.UseNpgsql(
                    "Host=localhost;Database=printfarmer;Username=test;******",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            default:
                _ = options.UseSqlServer(
                    "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
        }

        using SlicerDbContext context = new(options.Options);
        Migration migration = CreateMigration(
            context.GetService<IMigrationsAssembly>(),
            context,
            "_AddOwnerScopedPromotionOperationKey");

        AssertOperationKeyReplacesGlobalUniqueness(migration, table, nameof(Artifact.PromotionOperationKey));
    }

    /// <summary>
    /// Asserts that a migration moves promotion uniqueness from the caller-supplied idempotency key
    /// onto the owner-scoped key, so two owners can reuse the same raw key.
    /// </summary>
    /// <param name="migration">The migration under test.</param>
    /// <param name="table">The table that carries the promotion identity.</param>
    /// <param name="keyColumn">The owner-scoped key column name.</param>
    private static void AssertOperationKeyReplacesGlobalUniqueness(
        Migration migration,
        string table,
        string keyColumn)
    {
        _ = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Should().ContainSingle(operation => operation.Table == table && operation.Name == keyColumn)
            .Which.IsNullable.Should().BeTrue();

        CreateIndexOperation[] indexes = migration.UpOperations
            .OfType<CreateIndexOperation>()
            .Where(operation => operation.Table == table)
            .ToArray();
        _ = indexes.Should().ContainSingle(operation =>
            operation.IsUnique && operation.Columns.SequenceEqual(new[] { keyColumn }));
        _ = indexes.Should().ContainSingle(operation =>
            !operation.IsUnique && operation.Columns.SequenceEqual(new[] { "PromotionOperationId" }));
        _ = migration.UpOperations
            .OfType<DropIndexOperation>()
            .Select(operation => operation.Name)
            .Should().Contain($"IX_{table}_PromotionOperationId");
    }

    [Theory]
    [InlineData("sqlite", "Farm.Slicer.Migrations.Sqlite")]
    [InlineData("postgres", "Farm.Slicer.Migrations.PostgreSQL")]
    [InlineData("sqlserver", "Farm.Slicer.Migrations.SqlServer")]
    public void ArtifactPromotionMigration_ForEveryProvider_AddsOptionalCoordinationColumns(
        string provider,
        string migrationAssembly)
    {
        DbContextOptionsBuilder<SlicerDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                _ = options.UseSqlite(
                    "Data Source=:memory:",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            case "postgres":
                _ = options.UseNpgsql(
                    "Host=localhost;Database=printfarmer;Username=test;******",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            default:
                _ = options.UseSqlServer(
                    "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
        }

        using SlicerDbContext context = new(options.Options);
        Migration migration = CreateMigration(
            context.GetService<IMigrationsAssembly>(),
            context,
            "_AddArtifactPromotionCoordination");

        AddColumnOperation[] artifactColumns = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Where(operation => operation.Table == "Artifacts")
            .ToArray();
        _ = artifactColumns.Select(operation => operation.Name).Should().Contain(
            nameof(Artifact.PromotionOperationId),
            nameof(Artifact.PromotionCheckpointId),
            nameof(Artifact.PromotionStartedAtUtc),
            nameof(Artifact.PromotedAtUtc),
            nameof(Artifact.PromotedGcodeFileId));

        // Existing non-calibration artifacts have never been promoted, so nothing may become required.
        _ = artifactColumns.Should().OnlyContain(operation => operation.IsNullable);
        _ = migration.UpOperations.OfType<CreateTableOperation>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("sqlite", "Farm.Slicer.Migrations.Sqlite", "TEXT")]
    [InlineData("postgres", "Farm.Slicer.Migrations.PostgreSQL", "timestamp with time zone")]
    [InlineData("sqlserver", "Farm.Slicer.Migrations.SqlServer", "datetime2")]
    public void ArtifactCleanupDeletionStateMigration_ForEveryProvider_AddsRecoverableTombstone(
        string provider,
        string migrationAssembly,
        string expectedColumnType)
    {
        DbContextOptionsBuilder<SlicerDbContext> options = new();
        switch (provider)
        {
            case "sqlite":
                _ = options.UseSqlite(
                    "Data Source=:memory:",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            case "postgres":
                _ = options.UseNpgsql(
                    "Host=localhost;Database=printfarmer;Username=test;******",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
            default:
                _ = options.UseSqlServer(
                    "Server=localhost;Database=printfarmer;User Id=test;******;TrustServerCertificate=true",
                    builder => builder.MigrationsAssembly(migrationAssembly));
                break;
        }

        using SlicerDbContext context = new(options.Options);
        Migration migration = CreateMigration(
            context.GetService<IMigrationsAssembly>(),
            context,
            "_AddArtifactCleanupDeletionState");

        AddColumnOperation added = migration.UpOperations
            .OfType<AddColumnOperation>()
            .Should().ContainSingle()
            .Which;
        _ = added.Table.Should().Be("Artifacts");
        _ = added.Name.Should().Be(nameof(Artifact.CleanupDeletionStartedAtUtc));
        _ = added.ColumnType.Should().Be(expectedColumnType);
        _ = added.IsNullable.Should().BeTrue();

        DropColumnOperation removed = migration.DownOperations
            .OfType<DropColumnOperation>()
            .Should().ContainSingle()
            .Which;
        _ = removed.Table.Should().Be("Artifacts");
        _ = removed.Name.Should().Be(nameof(Artifact.CleanupDeletionStartedAtUtc));
    }

    private static Migration CreateMigration(
        IMigrationsAssembly migrationsAssembly,
        DbContext context,
        string migrationSuffix)
    {
        KeyValuePair<string, System.Reflection.TypeInfo> definition =
            migrationsAssembly.Migrations.Single(candidate =>
                candidate.Key.EndsWith(migrationSuffix, StringComparison.Ordinal));
        return migrationsAssembly.CreateMigration(
            definition.Value,
            context.Database.ProviderName!);
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

    private static SlicerDbContext CreateSlicerContext(SqliteConnection connection)
    {
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"))
            .Options;
        return new SlicerDbContext(options);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @tableName)";
        _ = command.Parameters.AddWithValue("@tableName", tableName);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToBoolean(result);
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

        Func<Task> migrate = () => ProviderAwareMigrationRunner.MigrateAsync(
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
