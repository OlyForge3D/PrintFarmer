using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Farm.Infrastructure.Tests.Repositories.Notifications;

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
    public void InstallationId_AllProviderModels_EnforceOneActiveGlobalOwner()
    {
        IModel[] models =
        [
            BuildModel(builder => builder.UseSqlServer(
                "Server=(localdb)\\ModelOnly;Database=model_only;Trusted_Connection=True;TrustServerCertificate=True")),
            BuildModel(builder => builder.UseNpgsql(
                "Host=localhost;Database=model_only;Username=model_only;Password=model_only")),
            BuildModel(builder => builder.UseSqlite("DataSource=:memory:")),
        ];

        string[] filters = ["[IsActive] = 1", "\"IsActive\"", "\"IsActive\" = 1"];

        for (int index = 0; index < models.Length; index++)
        {
            IModel model = models[index];
            IEntityType entity = model.FindEntityType(typeof(DeviceToken))!;
            IIndex ownerIndex = entity.GetIndexes()
                .Single(index => index.GetDatabaseName() == "IX_DeviceTokens_InstallationId");

            ownerIndex.IsUnique.Should().BeTrue();
            ownerIndex.Properties.Select(property => property.Name)
                .Should().Equal(nameof(DeviceToken.InstallationId));
            ownerIndex.GetFilter().Should().Be(filters[index]);
            entity.GetIndexes().Should().NotContain(index =>
                index.GetDatabaseName() == "IX_DeviceTokens_UserId_InstallationId");
        }
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
}
