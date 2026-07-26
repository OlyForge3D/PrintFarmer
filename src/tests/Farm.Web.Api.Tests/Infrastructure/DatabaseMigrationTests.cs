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
        first.AppliedMigrations.Should().NotBeEmpty();
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
            "20260725173232_AlignDevelopmentSlicerSchema");
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
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
}
