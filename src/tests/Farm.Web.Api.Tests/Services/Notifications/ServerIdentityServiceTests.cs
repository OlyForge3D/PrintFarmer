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
/// identities, and concurrent first-time generation converges on a single winner. See
/// issue #1407.
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
    public async Task GetServerIdAsync_ConcurrentFirstGeneration_ConvergesOnSingleWinner()
    {
        string databaseName = $"ServerIdentity_{Guid.NewGuid():N}";

        // Each concurrent caller uses its own ServerIdentityService instance (no shared
        // in-memory cache) but the SAME backing database, so the unique index on
        // AppSettingsEntity.Key is the only thing that can prevent a double-generation race.
        Task<string>[] tasks = Enumerable.Range(0, 8)
            .Select(_ =>
            {
                var instance = new ServerIdentityService(
                    CreateScopeFactory(databaseName),
                    NullLogger<ServerIdentityService>.Instance);
                return instance.GetServerIdAsync();
            })
            .ToArray();

        string[] results = await Task.WhenAll(tasks);

        results.Distinct().Should().ContainSingle("all concurrent first-time generations must converge on exactly one winning identity");
    }
}
