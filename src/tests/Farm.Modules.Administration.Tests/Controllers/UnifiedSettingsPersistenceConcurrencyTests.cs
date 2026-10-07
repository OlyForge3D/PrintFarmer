using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Administration.Tests.Controllers;

public sealed class UnifiedSettingsPersistenceConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public UnifiedSettingsPersistenceConcurrencyTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using AppDbContext db = new(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Save_LegacyThenCheckedInSameScope_UsesCommittedRevision()
    {
        using AppDbContext repositoryContext = new(_options);
        Mock<IDbContextFactory<AppDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContext()).Returns(() => new AppDbContext(_options));
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));
        SettingsService service = new(new ConfigurationBuilder().Build(), factory.Object,
            NullLogger<SettingsService>.Instance, new EfAppSettingsRepository(repositoryContext));

        service.Save(new CatalogUpdateSettings());
        SettingsSectionSnapshot first = service.GetSectionSnapshot("CatalogUpdates");
        first.RowVersion.Should().NotBe(SettingsSectionSnapshot.AbsentRowVersion);
        SettingsSectionSnapshot second = await service.SaveWithConcurrencyCheckAsync(
            new CatalogUpdateSettings { Enabled = false }, first.RowVersion);
        second.RowVersion.Should().NotBe(first.RowVersion);
    }

    [Fact]
    public async Task SaveWithConcurrencyCheck_TwoLoadedSnapshots_RejectsStaleWithoutChangingCache()
    {
        SettingsService first = CreateService();
        SettingsSectionSnapshot seed = await first.SaveWithConcurrencyCheckAsync(
            new CatalogUpdateSettings(), SettingsSectionSnapshot.AbsentRowVersion);
        SettingsService second = CreateService();
        SettingsSectionSnapshot old = second.GetSectionSnapshot("CatalogUpdates");
        SettingsSectionSnapshot saved = await first.SaveWithConcurrencyCheckAsync(
            new CatalogUpdateSettings { Enabled = false }, seed.RowVersion);

        Func<Task> staleSave = () => second.SaveWithConcurrencyCheckAsync(
            new CatalogUpdateSettings { Enabled = true }, old.RowVersion);
        await staleSave.Should().ThrowAsync<DbUpdateConcurrencyException>();
        second.GetSectionSnapshot("CatalogUpdates").Should().BeEquivalentTo(old);

        // A long-lived reader must not attach a freshly queried token to its old cached values.
        old.RowVersion.Should().NotBe(saved.RowVersion);
        ((CatalogUpdateSettings)old.Value).Enabled.Should().BeTrue();
        CreateService().GetSectionSnapshot("CatalogUpdates").Should().BeEquivalentTo(saved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveWithConcurrencyCheck_WriterBetweenReadAndCommit_RejectsLoser(bool creating)
    {
        SettingsService seedService = CreateService();
        string token = SettingsSectionSnapshot.AbsentRowVersion;
        if (!creating)
        {
            token = (await seedService.SaveWithConcurrencyCheckAsync(new CatalogUpdateSettings(), token)).RowVersion;
        }

        BeforeSaveInterceptor interceptor = new(async () =>
        {
            SettingsService winner = CreateService();
            await winner.SaveWithConcurrencyCheckAsync(
                new CatalogUpdateSettings { Enabled = false }, token);
        });
        SettingsService loser = CreateService(interceptor);
        SettingsSectionSnapshot before = loser.GetSectionSnapshot("CatalogUpdates");
        Func<Task> losingSave = () => loser.SaveWithConcurrencyCheckAsync(new CatalogUpdateSettings(), token);

        await losingSave.Should().ThrowAsync<DbUpdateConcurrencyException>();
        loser.GetSectionSnapshot("CatalogUpdates").Should().BeEquivalentTo(before);
        using AppDbContext db = new(_options);
        AppSettingsEntity row = await db.AppSettingsEntities.SingleAsync(e => e.Key == "CatalogUpdates");
        JsonSerializer.Deserialize<CatalogUpdateSettings>(row.SettingsJson)!.Enabled.Should().BeFalse();
        row.Revision.Should().Be(creating ? 1 : 2);
    }

    [Fact]
    public async Task SaveWithConcurrencyCheck_AbsentTokenAfterCreation_RejectsWithoutOverwriting()
    {
        SettingsService service = CreateService();
        SettingsSectionSnapshot saved = await service.SaveWithConcurrencyCheckAsync(
            new CatalogUpdateSettings(), SettingsSectionSnapshot.AbsentRowVersion);
        Func<Task> retry = () => service.SaveWithConcurrencyCheckAsync(
            new CatalogUpdateSettings { Enabled = false },
            SettingsSectionSnapshot.AbsentRowVersion);
        await retry.Should().ThrowAsync<DbUpdateConcurrencyException>();
        service.GetSectionSnapshot("CatalogUpdates").Should().BeEquivalentTo(saved);
    }

    [Fact]
    public async Task SaveWithConcurrencyCheck_PersistenceFailure_DoesNotPublishUnsavedValues()
    {
        BeforeSaveInterceptor interceptor = new(() => throw new InvalidOperationException("simulated storage failure"));
        SettingsService service = CreateService(interceptor);
        SettingsSectionSnapshot before = service.GetSectionSnapshot("CatalogUpdates");
        Func<Task> save = () => service.SaveWithConcurrencyCheckAsync(
            new CatalogUpdateSettings { Enabled = false }, before.RowVersion);
        await save.Should().ThrowAsync<InvalidOperationException>();
        service.GetSectionSnapshot("CatalogUpdates").Should().BeEquivalentTo(before);
        using AppDbContext db = new(_options);
        (await db.AppSettingsEntities.AnyAsync(e => e.Key == "CatalogUpdates")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveWithConcurrencyCheck_SequentialAndUnrelatedSaves_AdvanceOnlySectionRevision()
    {
        SettingsService service = CreateService();
        SettingsSectionSnapshot first = await service.SaveWithConcurrencyCheckAsync(
            new CatalogUpdateSettings(), SettingsSectionSnapshot.AbsentRowVersion);
        await service.SaveWithConcurrencyCheckAsync(
            new SpoolCoverageSettings(), SettingsSectionSnapshot.AbsentRowVersion);
        service.GetSectionSnapshot("CatalogUpdates").RowVersion.Should().Be(first.RowVersion);
        SettingsSectionSnapshot second = await service.SaveWithConcurrencyCheckAsync(
            new CatalogUpdateSettings(), first.RowVersion);
        second.RowVersion.Should().NotBe(first.RowVersion);
        using AppDbContext db = new(_options);
        (await db.AppSettingsEntities.SingleAsync(e => e.Key == "CatalogUpdates")).Revision.Should().Be(2);
    }

    private SettingsService CreateService(SaveChangesInterceptor? interceptor = null)
    {
        DbContextOptions<AppDbContext> options = interceptor is null
            ? _options
            : new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(interceptor).Options;
        Mock<IDbContextFactory<AppDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContext()).Returns(() => new AppDbContext(options));
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(options));

        // The checked save must never call the old unchecked repository save path.
        Mock<IAppSettingsRepository> repository = new(MockBehavior.Strict);
        return new SettingsService(new ConfigurationBuilder().Build(), factory.Object,
            NullLogger<SettingsService>.Instance, repository.Object);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class BeforeSaveInterceptor(Func<Task> beforeSave) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await beforeSave();
            return result;
        }
    }
}
