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
        result.AppliedMigrations.Should().HaveCount(2);
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
