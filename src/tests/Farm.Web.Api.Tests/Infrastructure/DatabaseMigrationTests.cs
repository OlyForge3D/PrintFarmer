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
            "20260806232640_CanonicalizePrintJobPriority");
        second.LegacySchemaBaselined.Should().BeFalse();
        second.AppliedMigrations.Should().BeEquivalentTo(first.AppliedMigrations);
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
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
    public async Task CoreMigration_SeedsOutboxSequenceFenceWithApplicationManagedRowVersion()
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
        seeded.RowVersion.Should().BeNull(
            "SQLite/PostgreSQL row versions are application-managed, so a migration-seeded row is "
            + "not stamped until the application first saves it");

        seeded.NextSequence = 1L;
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        OutboxSequenceState stamped = await context.OutboxSequenceStates.SingleAsync(s => s.Id == 1);
        stamped.RowVersion.Should().NotBeNullOrEmpty(
            "StampRowVersions() must write a concurrency token once the application saves the row");
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

        Func<Task> migrate = () => ProviderAwareMigrationRunner.MigrateAsync(
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

        Func<Task> initialize = () => ProgramHelpers.InitializeDatabaseAsync(app);

        DatabaseMigrationContractException exception =
            (await initialize.Should().ThrowAsync<DatabaseMigrationContractException>()).Which;
        exception.Code.Should().Be("migration_assembly_missing");
        exception.Message.Should().Contain("No SQLite migrations were found");
        (await TableExistsAsync(connection, "Printers")).Should().BeTrue();
        (await TableExistsAsync(connection, "__EFMigrationsHistory")).Should().BeFalse();
    }

    [Fact]
    public async Task ProgramHelpersInitialization_SeedingFailure_RemainsNonFatal()
    {
        await using SqliteConnection connection = await OpenConnectionAsync();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        _ = builder.Services.AddLogging();
        _ = builder.Services.AddDbContext<AppDbContext>(
            options => options.UseSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")));
        var initializer = new Mock<IDatabaseInitializer>();
        _ = initializer
            .Setup(service => service.InitializeAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("Synthetic seeding failure"));
        _ = builder.Services.AddScoped(_ => initializer.Object);
        _ = builder.Services.AddSingleton<IStartupStatus, StartupStatus>();
        await using WebApplication app = builder.Build();

        Func<Task> initialize = () => ProgramHelpers.InitializeDatabaseAsync(app);

        _ = await initialize.Should().NotThrowAsync();
        initializer.Verify(
            service => service.InitializeAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Once);
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
        result.AppliedMigrations.Should().Equal("20260730231419_SlicerInitialV2");
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

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @tableName)";
        _ = command.Parameters.AddWithValue("@tableName", tableName);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToBoolean(result);
    }

    private static async Task<IReadOnlyList<string>> ReadAppliedMigrationIdsAsync(
        SqliteConnection connection)
    {
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

internal sealed class ArbitraryBaselineDbContext(
    DbContextOptions<ArbitraryBaselineDbContext> options) : DbContext(options)
{
    public DbSet<ArbitraryBaselineEntity> BaselineEntities => Set<ArbitraryBaselineEntity>();
}

internal sealed class ArbitraryBaselineEntity
{
    public int Id { get; set; }
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
