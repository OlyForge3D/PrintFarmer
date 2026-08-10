using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications;

/// <summary>
/// Verifies <see cref="ServerIdentityService"/>: identity is generated exactly once per
/// backing database, is stable across repeated calls and simulated restarts (a fresh
/// service instance backed by the same database), two distinct databases produce distinct
/// identities, and losing a concurrent first-generation race re-reads the winner's identity
/// rather than returning its own freshly generated GUID. See issue #1407. The genuine
/// unique-index race-safety proof for the underlying repository insert lives in
/// <c>EfAppSettingsRepositoryRaceTests</c> against a real relational (SQLite) provider, since
/// the EF in-memory provider used here does not enforce unique indexes.
/// </summary>
public sealed class ServerIdentityServiceTests
{
    private static IServiceScopeFactory CreateScopeFactory(string databaseName)
    {
        var services = new ServiceCollection();
        _ = services.AddDbContext<AppDbContext>(o => o
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        _ = services.AddScoped<IAppSettingsRepository, EfAppSettingsRepository>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task GetServerIdAsync_FreshDatabase_GeneratesCanonicalUuidOnce()
    {
        IServiceScopeFactory scopeFactory = CreateScopeFactory($"ServerIdentity_{Guid.NewGuid():N}");
        var sut = new ServerIdentityService(scopeFactory, NullLogger<ServerIdentityService>.Instance);

        string serverId = await sut.GetServerIdAsync();

        NativePushRegistrationContract.IsCanonicalOriginServerId(serverId).Should().BeTrue();
    }

    [Fact]
    public async Task GetServerIdAsync_RepeatedCallsSameInstance_ReturnsSameValue()
    {
        IServiceScopeFactory scopeFactory = CreateScopeFactory($"ServerIdentity_{Guid.NewGuid():N}");
        var sut = new ServerIdentityService(scopeFactory, NullLogger<ServerIdentityService>.Instance);

        string first = await sut.GetServerIdAsync();
        string second = await sut.GetServerIdAsync();

        second.Should().Be(first);
    }

    [Fact]
    public async Task GetServerIdAsync_NewInstanceSameDatabase_SimulatesRestart_ReturnsSameValue()
    {
        string databaseName = $"ServerIdentity_{Guid.NewGuid():N}";
        IServiceScopeFactory scopeFactoryBeforeRestart = CreateScopeFactory(databaseName);
        var before = new ServerIdentityService(scopeFactoryBeforeRestart, NullLogger<ServerIdentityService>.Instance);
        string original = await before.GetServerIdAsync();

        // A brand-new ServerIdentityService instance (no shared in-memory cache) against a
        // scope factory targeting the SAME database simulates a process restart.
        IServiceScopeFactory scopeFactoryAfterRestart = CreateScopeFactory(databaseName);
        var after = new ServerIdentityService(scopeFactoryAfterRestart, NullLogger<ServerIdentityService>.Instance);
        string afterRestart = await after.GetServerIdAsync();

        afterRestart.Should().Be(original, "the identity must survive a process restart");
    }

    [Fact]
    public async Task GetServerIdAsync_TwoDistinctDatabases_ProduceDistinctIdentities()
    {
        var serverOne = new ServerIdentityService(
            CreateScopeFactory($"ServerIdentity_{Guid.NewGuid():N}"),
            NullLogger<ServerIdentityService>.Instance);
        var serverTwo = new ServerIdentityService(
            CreateScopeFactory($"ServerIdentity_{Guid.NewGuid():N}"),
            NullLogger<ServerIdentityService>.Instance);

        string idOne = await serverOne.GetServerIdAsync();
        string idTwo = await serverTwo.GetServerIdAsync();

        idTwo.Should().NotBe(idOne, "two logically distinct servers must never share an identity");
    }

    [Fact]
    public async Task GetServerIdAsync_LostRaceOnInsert_ReturnsWinnersIdNotItsOwnGeneratedGuid()
    {
        // This exercises ServerIdentityService's own race-handling logic in isolation: the
        // EF in-memory provider does not enforce unique indexes (see
        // EfAppSettingsRepositoryRaceTests for the real relational-provider proof that
        // TryInsertIfAbsentAsync itself is race-safe), so here a fake repository simulates
        // "another caller already committed a row" by returning false from
        // TryInsertIfAbsentAsync on the first attempt.
        var winnerId = Guid.NewGuid().ToString("D");
        var fakeRepository = new LosingRaceAppSettingsRepository(winnerId);
        var services = new ServiceCollection();
        _ = services.AddSingleton<IAppSettingsRepository>(fakeRepository);
        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var sut = new ServerIdentityService(scopeFactory, NullLogger<ServerIdentityService>.Instance);

        string resolved = await sut.GetServerIdAsync();

        resolved.Should().Be(winnerId, "losing the insert race must re-read and return the winner's committed identity, never the loser's own freshly generated GUID");
    }

    /// <summary>
    /// Fake <see cref="IAppSettingsRepository"/> that simulates a caller losing a concurrent
    /// first-generation race: no row exists on the initial read, the insert attempt reports it
    /// lost the race, and a subsequent read returns another caller's already-committed row.
    /// </summary>
    private sealed class LosingRaceAppSettingsRepository(string winnerServerId) : IAppSettingsRepository
    {
        private readonly string _winnerJson = $"{{\"ServerId\":\"{winnerServerId}\"}}";
        private int _readCount;

        public Task<AppSettingsEntity?> GetAsync(string key, CancellationToken ct = default)
            => GetReadOnlyAsync(key, ct);

        public Task<AppSettingsEntity?> GetReadOnlyAsync(string key, CancellationToken ct = default)
        {
            _readCount++;
            if (_readCount == 1)
            {
                // Initial "does it already exist?" read: nothing yet.
                return Task.FromResult<AppSettingsEntity?>(null);
            }

            // Re-read after losing the race: the winner's row is now visible.
            return Task.FromResult<AppSettingsEntity?>(new AppSettingsEntity
            {
                Key = key,
                SettingsJson = _winnerJson,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => throw new InvalidOperationException("ServerIdentityService must not call the upsert-style SetAsync for first-generation.");

        public Task<bool> TryInsertIfAbsentAsync(string key, string value, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task SaveChangesAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
