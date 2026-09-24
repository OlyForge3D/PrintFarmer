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
    public async Task SaveWithConcurrencyCheck_TwoLoadedSnapshots_RejectsStaleWithoutChangingCache()
    {
        SettingsService first = CreateService();
        SettingsSectionSnapshot seed = await first.SaveWithConcurrencyCheckAsync(
            new UpdateChannelSettings(), SettingsSectionSnapshot.AbsentRowVersion);
        SettingsService second = CreateService();
        SettingsSectionSnapshot old = second.GetSectionSnapshot("UpdateChannel");
        SettingsSectionSnapshot saved = await first.SaveWithConcurrencyCheckAsync(
            new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = true }, seed.RowVersion);

        Func<Task> staleSave = () => second.SaveWithConcurrencyCheckAsync(
            new UpdateChannelSettings { Channel = "stable" }, old.RowVersion);
        await staleSave.Should().ThrowAsync<DbUpdateConcurrencyException>();
        second.GetSectionSnapshot("UpdateChannel").Should().BeEquivalentTo(old);
        // A long-lived reader must not attach a freshly queried token to its old cached values.
        old.RowVersion.Should().NotBe(saved.RowVersion);
        ((UpdateChannelSettings)old.Value).Channel.Should().Be("stable");
        CreateService().GetSectionSnapshot("UpdateChannel").Should().BeEquivalentTo(saved);
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
            token = (await seedService.SaveWithConcurrencyCheckAsync(new UpdateChannelSettings(), token)).RowVersion;
        }

        BeforeSaveInterceptor interceptor = new(async () =>
        {
            SettingsService winner = CreateService();
            await winner.SaveWithConcurrencyCheckAsync(
                new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = true }, token);
        });
        SettingsService loser = CreateService(interceptor);
        SettingsSectionSnapshot before = loser.GetSectionSnapshot("UpdateChannel");
        Func<Task> losingSave = () => loser.SaveWithConcurrencyCheckAsync(new UpdateChannelSettings(), token);

        await losingSave.Should().ThrowAsync<DbUpdateConcurrencyException>();
        loser.GetSectionSnapshot("UpdateChannel").Should().BeEquivalentTo(before);
        using AppDbContext db = new(_options);
        AppSettingsEntity row = await db.AppSettingsEntities.SingleAsync(e => e.Key == "UpdateChannel");
        JsonSerializer.Deserialize<UpdateChannelSettings>(row.SettingsJson)!.Channel.Should().Be("insider");
        row.Revision.Should().Be(creating ? 1 : 2);
    }

    [Fact]
    public async Task SaveWithConcurrencyCheck_AbsentTokenAfterCreation_RejectsWithoutOverwriting()
    {
        SettingsService service = CreateService();
        SettingsSectionSnapshot saved = await service.SaveWithConcurrencyCheckAsync(
            new UpdateChannelSettings(), SettingsSectionSnapshot.AbsentRowVersion);
        Func<Task> retry = () => service.SaveWithConcurrencyCheckAsync(
            new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = true },
            SettingsSectionSnapshot.AbsentRowVersion);
        await retry.Should().ThrowAsync<DbUpdateConcurrencyException>();
        service.GetSectionSnapshot("UpdateChannel").Should().BeEquivalentTo(saved);
    }

    [Fact]
    public async Task SaveWithConcurrencyCheck_PersistenceFailure_DoesNotPublishUnsavedValues()
    {
        BeforeSaveInterceptor interceptor = new(() => throw new InvalidOperationException("simulated storage failure"));
        SettingsService service = CreateService(interceptor);
        SettingsSectionSnapshot before = service.GetSectionSnapshot("UpdateChannel");
        Func<Task> save = () => service.SaveWithConcurrencyCheckAsync(
            new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = true }, before.RowVersion);
        await save.Should().ThrowAsync<InvalidOperationException>();
        service.GetSectionSnapshot("UpdateChannel").Should().BeEquivalentTo(before);
        using AppDbContext db = new(_options);
        (await db.AppSettingsEntities.AnyAsync(e => e.Key == "UpdateChannel")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveWithConcurrencyCheck_SequentialAndUnrelatedSaves_AdvanceOnlySectionRevision()
    {
        SettingsService service = CreateService();
        SettingsSectionSnapshot first = await service.SaveWithConcurrencyCheckAsync(
            new UpdateChannelSettings(), SettingsSectionSnapshot.AbsentRowVersion);
        await service.SaveWithConcurrencyCheckAsync(
            new SpoolCoverageSettings(), SettingsSectionSnapshot.AbsentRowVersion);
        service.GetSectionSnapshot("UpdateChannel").RowVersion.Should().Be(first.RowVersion);
        SettingsSectionSnapshot second = await service.SaveWithConcurrencyCheckAsync(
            new UpdateChannelSettings(), first.RowVersion);
        second.RowVersion.Should().NotBe(first.RowVersion);
        using AppDbContext db = new(_options);
        (await db.AppSettingsEntities.SingleAsync(e => e.Key == "UpdateChannel")).Revision.Should().Be(2);
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
