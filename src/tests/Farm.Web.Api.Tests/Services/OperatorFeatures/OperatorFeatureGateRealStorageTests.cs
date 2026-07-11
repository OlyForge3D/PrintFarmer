using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.OperatorFeatures;

/// <summary>
/// Integration-style tests that exercise <see cref="OperatorFeatureGate"/> against the real
/// <see cref="EfAppSettingsRepository"/> backed by an in-memory <see cref="AppDbContext"/>.
/// These are the tests explicitly required by the #725 convergence:
///
/// <list type="bullet">
///   <item>With an EMPTY AppSettings table, a config value like
///     <c>OperatorFeatures__nativePushEnabled=true</c> must NOT force-enable the feature.
///     The wider <see cref="SettingsService"/>'s config-section fallback bind of
///     <c>OperatorFeatures</c> must not bleed into the gate's base value.</item>
///   <item>With an EMPTY AppSettings table, a non-boolean config value must not throw or
///     crash the DI activation path; the gate falls back to defaults.</item>
///   <item>With an EMPTY AppSettings table, an explicit <c>false</c> still hard-disables.</item>
///   <item>A row written via <see cref="SettingsService.Save{T}"/> is observed by the gate
///     on the next request without any explicit cache invalidation.</item>
///   <item>The gate must not throw at construction time when the DB is unavailable, and its
///     read path must degrade to the documented defaults.</item>
/// </list>
/// </summary>
public class OperatorFeatureGateRealStorageTests
{
    private static (AppDbContext Db, IAppSettingsRepository Repo, IDbContextFactory<AppDbContext> Factory)
        CreateEmptyStorage()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"OperatorFeaturesGate_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        AppDbContext db = new(options);
        Mock<IDbContextFactory<AppDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContext()).Returns(() => new AppDbContext(options));
        return (db, new EfAppSettingsRepository(db), factoryMock.Object);
    }

    private static IConfiguration ConfigWith(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void EmptyDb_ConfigTrue_DoesNotForceEnableNativePush()
    {
        (AppDbContext db, IAppSettingsRepository repo, _) = CreateEmptyStorage();
        using (db)
        {
            // The env var name IS OperatorFeatures__nativePushEnabled — this is the exact
            // form documented for on-call use. The wider SettingsService would bind this
            // into a fresh OperatorFeatureSettings and end up with NativePushEnabled=true;
            // the gate deliberately reads persisted state directly to avoid that bleed.
            IConfiguration config = ConfigWith(("OperatorFeatures:nativePushEnabled", "true"));
            OperatorFeatureGate gate = new(repo, config, NullLogger<OperatorFeatureGate>.Instance);

            OperatorFeatureFlagsDto flags = gate.GetEffectiveFlags();
            flags.NativePushEnabled.Should().BeFalse(
                "config `true` must never force-enable a flag — only DB or class defaults set the base value");
            flags.AttentionEnabled.Should().BeTrue("other flags stay at their documented defaults");
        }
    }

    [Fact]
    public void EmptyDb_ConfigJunk_FallsThroughToDefault_WithoutThrowing()
    {
        (AppDbContext db, IAppSettingsRepository repo, _) = CreateEmptyStorage();
        using (db)
        {
            IConfiguration config = ConfigWith(
                ("OperatorFeatures:attentionEnabled", "not-a-bool"),
                ("OperatorFeatures:nativePushEnabled", "maybe"));

            // Constructing the gate must not throw even though the wider SettingsService
            // would try to bind these strings as bool and fail during ctor.
            OperatorFeatureGate gate = new(repo, config, NullLogger<OperatorFeatureGate>.Instance);
            OperatorFeatureFlagsDto flags = gate.GetEffectiveFlags();

            flags.AttentionEnabled.Should().BeTrue("junk env values fall through to the default");
            flags.NativePushEnabled.Should().BeFalse("junk env values fall through to the default (false)");
        }
    }

    [Fact]
    public void EmptyDb_ConfigFalse_HardDisables()
    {
        (AppDbContext db, IAppSettingsRepository repo, _) = CreateEmptyStorage();
        using (db)
        {
            IConfiguration config = ConfigWith(("OperatorFeatures:attentionEnabled", "false"));
            OperatorFeatureGate gate = new(repo, config, NullLogger<OperatorFeatureGate>.Instance);

            gate.IsEnabled(OperatorFeature.Attention).Should().BeFalse();
            gate.IsHardDisabledByEnvironment(OperatorFeature.Attention).Should().BeTrue();
            gate.IsEnabled(OperatorFeature.FilamentCoverage).Should().BeTrue("unaffected flags stay at defaults");
        }
    }

    [Fact]
    public async Task WriteViaSettingsService_IsObservedByGate_OnNextCall()
    {
        // Real end-to-end DB round-trip: admin flow saves via SettingsService (which writes
        // to the same AppSettings row via IAppSettingsRepository), the gate reads that row
        // back on the very next call. No cache-invalidation ceremony required because the
        // gate re-queries the repository on every property access.
        (AppDbContext db, IAppSettingsRepository repo, IDbContextFactory<AppDbContext> factory) = CreateEmptyStorage();
        using (db)
        {
            IConfiguration config = ConfigWith();
            OperatorFeatureGate gate = new(repo, config, NullLogger<OperatorFeatureGate>.Instance);

            gate.IsEnabled(OperatorFeature.Attention).Should().BeTrue("baseline default");

            SettingsService settingsService = new(
                config,
                factory,
                NullLogger<SettingsService>.Instance,
                new EfAppSettingsRepository(factory.CreateDbContext()));

            OperatorFeatureSettings toPersist = new() { AttentionEnabled = false };
            settingsService.Save(toPersist);

            // Fresh scope-equivalent: same repo instance re-queries the same in-memory DB.
            OperatorFeatureFlagsDto after = gate.GetEffectiveFlags();
            after.AttentionEnabled.Should().BeFalse("gate observes the persisted row on the next call");

            AppSettingsEntity? row = await repo.GetAsync(OperatorFeatureSettings.SectionName);
            row.Should().NotBeNull("SettingsService.Save writes the row via IAppSettingsRepository");
        }
    }

    [Fact]
    public void Construction_DoesNotTouchDatabase()
    {
        // Blocker 2 from the #725 convergence: capability lookup must not fail because
        // gate construction performs DB I/O. Verify by using a repo whose GetAsync throws
        // but whose ctor is cheap — the gate must construct successfully and only fail-soft
        // to defaults on the actual read.
        Mock<IAppSettingsRepository> repo = new();
        repo.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        OperatorFeatureGate gate = new(
            repo.Object,
            ConfigWith(),
            NullLogger<OperatorFeatureGate>.Instance);

        // Construction must not have called GetAsync.
        repo.Verify(
            r => r.GetAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Never);

        // First read triggers the throw but degrades to defaults.
        OperatorFeatureFlagsDto flags = gate.GetEffectiveFlags();
        flags.AttentionEnabled.Should().BeTrue();
        flags.NativePushEnabled.Should().BeFalse();
        repo.Verify(
            r => r.GetAsync(OperatorFeatureSettings.SectionName, It.IsAny<System.Threading.CancellationToken>()),
            Times.AtLeastOnce);
    }
}
