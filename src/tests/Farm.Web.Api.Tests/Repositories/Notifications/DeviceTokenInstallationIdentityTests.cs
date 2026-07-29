using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Repositories.Notifications;

/// <summary>
/// Provider-aware contract tests for opaque native-push installation identities.
/// </summary>
public sealed class DeviceTokenInstallationIdentityTests
{
    private const string BinaryCaseSensitiveCollation = "Latin1_General_100_BIN2";

    [Fact]
    public void InstallationId_SqlServerModel_UsesBytewiseCollation()
    {
        IModel model = BuildModel(builder => builder.UseSqlServer(
            "Server=(localdb)\\ModelOnly;Database=model_only;Trusted_Connection=True;TrustServerCertificate=True"));

        string? collation = GetInstallationId(model).GetCollation();

        collation.Should().Be(BinaryCaseSensitiveCollation);
    }

    [Fact]
    public void InstallationId_PostgreSqlAndSqliteModels_DoNotUseSqlServerCollation()
    {
        IModel postgreSql = BuildModel(builder => builder.UseNpgsql(
            "Host=localhost;Database=model_only;Username=model_only;Password=model_only"));
        IModel sqlite = BuildModel(builder => builder.UseSqlite("DataSource=:memory:"));

        GetInstallationId(postgreSql).GetCollation().Should().BeNull();
        GetInstallationId(sqlite).GetCollation().Should().BeNull();
    }

    [Fact]
    public void InstallationId_AllProviderModels_EnforceOneGlobalOwner()
    {
        IModel[] models =
        [
            BuildModel(builder => builder.UseSqlServer(
                "Server=(localdb)\\ModelOnly;Database=model_only;Trusted_Connection=True;TrustServerCertificate=True")),
            BuildModel(builder => builder.UseNpgsql(
                "Host=localhost;Database=model_only;Username=model_only;Password=model_only")),
            BuildModel(builder => builder.UseSqlite("DataSource=:memory:")),
        ];

        foreach (IModel model in models)
        {
            IEntityType entity = model.FindEntityType(typeof(DeviceToken))!;
            IIndex ownerIndex = entity.GetIndexes()
                .Single(index => index.GetDatabaseName() == "IX_DeviceTokens_InstallationId");

            ownerIndex.IsUnique.Should().BeTrue();
            ownerIndex.Properties.Select(property => property.Name)
                .Should().Equal(nameof(DeviceToken.InstallationId));
            entity.GetIndexes().Should().NotContain(index =>
                index.GetDatabaseName() == "IX_DeviceTokens_UserId_InstallationId");
        }
    }

    [Fact]
    public void InstallationOwnerMigration_Sqlite_DeduplicatesBeforeGlobalIndex()
    {
        AssertOwnerMigration(
            new Farm.Migrations.Sqlite.Migrations.EnforceGlobalDeviceTokenInstallationOwner(),
            "\"DeviceTokens\"",
            "\"InstallationId\"",
            "\"RegistrationVersion\" DESC");
    }

    [Fact]
    public void InstallationOwnerMigration_PostgreSql_DeduplicatesBeforeGlobalIndex()
    {
        AssertOwnerMigration(
            new Farm.Migrations.PostgreSQL.Migrations.EnforceGlobalDeviceTokenInstallationOwner(),
            "\"DeviceTokens\"",
            "\"InstallationId\"",
            "\"RegistrationVersion\" DESC");
    }

    [Fact]
    public void InstallationOwnerMigration_SqlServer_DeduplicatesBeforeGlobalIndex()
    {
        AssertOwnerMigration(
            new Farm.Migrations.SqlServer.Migrations.EnforceGlobalDeviceTokenInstallationOwner(),
            "[DeviceTokens]",
            "[InstallationId]",
            "[RegistrationVersion] DESC");
    }

    [Fact]
    public async Task UpsertAsync_CaseVariantInstallationIds_RemainDistinct()
    {
        NativePushRegistrationContract.IsCanonicalInstallationId("Install-A").Should().BeTrue();
        NativePushRegistrationContract.IsCanonicalInstallationId("install-a").Should().BeTrue();
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        _ = await db.Database.EnsureCreatedAsync();
        Guid userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Username = $"installation-identity-{userId:N}",
            Email = $"installation-identity-{userId:N}@test.local",
            PasswordHash = "x",
        });
        await db.SaveChangesAsync();
        var repository = new EfDeviceTokenRepository(db);

        DeviceToken upper = await repository.UpsertAsync(
            userId,
            "Install-A",
            new string('a', 64),
            "ios",
            "production",
            null);
        DeviceToken lower = await repository.UpsertAsync(
            userId,
            "install-a",
            new string('b', 64),
            "ios",
            "production",
            null);

        upper.Id.Should().NotBe(lower.Id);
        (await db.DeviceTokens.AsNoTracking().ToListAsync())
            .Select(token => token.InstallationId)
            .Should().BeEquivalentTo(["Install-A", "install-a"]);

        (await repository.DeleteByInstallationAsync(userId, "Install-A")).Should().BeTrue();
        (await db.DeviceTokens.AsNoTracking().SingleAsync()).InstallationId.Should().Be("install-a");
    }

    private static IModel BuildModel(Action<DbContextOptionsBuilder<AppDbContext>> configureProvider)
    {
        DbContextOptionsBuilder<AppDbContext> builder = new();
        configureProvider(builder);
        using var context = new AppDbContext(builder.Options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static IProperty GetInstallationId(IModel model)
    {
        IProperty? property = model.FindEntityType(typeof(DeviceToken))
            ?.FindProperty(nameof(DeviceToken.InstallationId));
        property.Should().NotBeNull();
        return property!;
    }

    private static void AssertOwnerMigration(
        Migration migration,
        string tableIdentifier,
        string installationIdentifier,
        string versionOrdering)
    {
        IReadOnlyList<MigrationOperation> up = migration.UpOperations;
        up.Should().HaveCount(4);
        SqlOperation deduplication = up[0].Should().BeOfType<SqlOperation>().Subject;
        deduplication.Sql.Should().Contain("ROW_NUMBER()");
        deduplication.Sql.Should().Contain(tableIdentifier);
        deduplication.Sql.Should().Contain(installationIdentifier);
        deduplication.Sql.Should().Contain(versionOrdering);

        DropIndexOperation droppedOwnerIndex = up[1].Should().BeOfType<DropIndexOperation>().Subject;
        droppedOwnerIndex.Name.Should().Be("IX_DeviceTokens_UserId_InstallationId");

        CreateIndexOperation globalOwnerIndex = up[2].Should().BeOfType<CreateIndexOperation>().Subject;
        globalOwnerIndex.Name.Should().Be("IX_DeviceTokens_InstallationId");
        globalOwnerIndex.Columns.Should().Equal(nameof(DeviceToken.InstallationId));
        globalOwnerIndex.IsUnique.Should().BeTrue();

        CreateIndexOperation userIndex = up[3].Should().BeOfType<CreateIndexOperation>().Subject;
        userIndex.Name.Should().Be("IX_DeviceTokens_UserId");
        userIndex.IsUnique.Should().BeFalse();

        migration.DownOperations.OfType<SqlOperation>().Should().BeEmpty();
        migration.DownOperations.OfType<CreateIndexOperation>()
            .Should().ContainSingle(index =>
                index.Name == "IX_DeviceTokens_UserId_InstallationId"
                && index.IsUnique);
    }
}
