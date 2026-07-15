using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.OperatorFeatures;

/// <summary>
/// Unit tests for <see cref="OperatorFeatureGate"/> covering defaults, persisted DB values,
/// the environment hard-disable override, and the DB-independent degradation path required
/// by issue #725. These tests use a mock <see cref="IAppSettingsRepository"/>; a companion
/// integration-style suite exercises the real EF Core repository against an in-memory
/// AppSettings store.
/// </summary>
public class OperatorFeatureGateTests
{
    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().AddInMemoryCollection([]).Build();

    private static IConfiguration ConfigWith(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static Mock<IAppSettingsRepository> RepoWith(OperatorFeatureSettings? persisted)
    {
        Mock<IAppSettingsRepository> repo = new();
        if (persisted is null)
        {
            repo.Setup(r => r.GetReadOnlyAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()))
                .ReturnsAsync((AppSettingsEntity?)null);
        }
        else
        {
            repo.Setup(r => r.GetReadOnlyAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AppSettingsEntity
                {
                    Key = OperatorFeatureSettings.SectionName,
                    SettingsJson = JsonSerializer.Serialize(persisted),
                    UpdatedAt = DateTime.UtcNow,
                });
        }

        return repo;
    }

    private static IOperatorFeatureGate CreateGate(
        OperatorFeatureSettings? persisted = null,
        IConfiguration? configuration = null)
        => new OperatorFeatureGate(
            RepoWith(persisted).Object,
            configuration ?? EmptyConfig(),
            NullLogger<OperatorFeatureGate>.Instance);

    [Fact]
    public void Defaults_MatchIssueSpecification()
    {
        IOperatorFeatureGate gate = CreateGate();

        OperatorFeatureFlagsDto flags = gate.GetEffectiveFlags();

        flags.AttentionEnabled.Should().BeTrue();
        flags.NativePushEnabled.Should().BeFalse("issue #725 requires native push disabled until a provider is configured");
        flags.FilamentCoverageEnabled.Should().BeTrue();
        flags.GuidedSwapEnabled.Should().BeTrue();
        flags.MultiSlotFallbackEnabled.Should().BeTrue();
        flags.ShiftPlanEnabled.Should().BeTrue();
        flags.PrintedPartsInventoryEnabled.Should().BeTrue();
        flags.OfflineWriteReplayEnabled.Should().BeTrue();
    }

    [Fact]
    public void GetFlagName_ReturnsCamelCaseFlagNames()
    {
        IOperatorFeatureGate gate = CreateGate();

        gate.GetFlagName(OperatorFeature.Attention).Should().Be("attentionEnabled");
        gate.GetFlagName(OperatorFeature.NativePush).Should().Be("nativePushEnabled");
        gate.GetFlagName(OperatorFeature.FilamentCoverage).Should().Be("filamentCoverageEnabled");
        gate.GetFlagName(OperatorFeature.GuidedSwap).Should().Be("guidedSwapEnabled");
        gate.GetFlagName(OperatorFeature.MultiSlotFallback).Should().Be("multiSlotFallbackEnabled");
        gate.GetFlagName(OperatorFeature.ShiftPlan).Should().Be("shiftPlanEnabled");
        gate.GetFlagName(OperatorFeature.PrintedPartsInventory).Should().Be("printedPartsInventoryEnabled");
        gate.GetFlagName(OperatorFeature.OfflineWriteReplay).Should().Be("offlineWriteReplayEnabled");
    }

    [Fact]
    public void AllFeatures_CoversEveryEnumValue()
    {
        IOperatorFeatureGate gate = CreateGate();

        gate.AllFeatures.Select(f => f.Feature)
            .Should().BeEquivalentTo(Enum.GetValues<OperatorFeature>());
    }

    [Fact]
    public void DatabaseValue_TakesEffectWhenNoEnvironmentOverride()
    {
        OperatorFeatureSettings persisted = new()
        {
            AttentionEnabled = false,
            NativePushEnabled = true,
            PrintedPartsInventoryEnabled = false,
        };
        IOperatorFeatureGate gate = CreateGate(persisted);

        gate.IsEnabled(OperatorFeature.Attention).Should().BeFalse();
        gate.IsEnabled(OperatorFeature.NativePush).Should().BeTrue();
        gate.IsEnabled(OperatorFeature.PrintedPartsInventory).Should().BeFalse();
        gate.IsEnabled(OperatorFeature.FilamentCoverage).Should().BeTrue("untouched flags stay at their default");
    }

    [Fact]
    public void EnvironmentFalse_HardDisables_EvenWhenDatabaseIsTrue()
    {
        OperatorFeatureSettings persisted = new()
        {
            AttentionEnabled = true,
            PrintedPartsInventoryEnabled = true,
        };
        IConfiguration config = ConfigWith(
            ("OperatorFeatures:attentionEnabled", "false"),
            ("OperatorFeatures:printedPartsInventoryEnabled", "False"));

        IOperatorFeatureGate gate = CreateGate(persisted, config);

        gate.IsEnabled(OperatorFeature.Attention).Should().BeFalse();
        gate.IsEnabled(OperatorFeature.PrintedPartsInventory).Should().BeFalse();
        gate.IsHardDisabledByEnvironment(OperatorFeature.Attention).Should().BeTrue();
        gate.IsHardDisabledByEnvironment(OperatorFeature.PrintedPartsInventory).Should().BeTrue();
        gate.IsHardDisabledByEnvironment(OperatorFeature.FilamentCoverage).Should().BeFalse();
    }

    [Fact]
    public void EnvironmentTrue_DoesNotForceEnable_WhenDatabaseIsFalse()
    {
        OperatorFeatureSettings persisted = new() { NativePushEnabled = false };
        IConfiguration config = ConfigWith(("OperatorFeatures:nativePushEnabled", "true"));

        IOperatorFeatureGate gate = CreateGate(persisted, config);

        gate.IsEnabled(OperatorFeature.NativePush).Should().BeFalse("only explicit false overrides");
        gate.IsHardDisabledByEnvironment(OperatorFeature.NativePush).Should().BeFalse();
    }

    [Fact]
    public void EnvironmentJunk_FallsThroughToDatabase()
    {
        OperatorFeatureSettings persisted = new() { AttentionEnabled = true };
        IConfiguration config = ConfigWith(("OperatorFeatures:attentionEnabled", "not-a-bool"));

        IOperatorFeatureGate gate = CreateGate(persisted, config);

        gate.IsEnabled(OperatorFeature.Attention).Should().BeTrue();
        gate.IsHardDisabledByEnvironment(OperatorFeature.Attention).Should().BeFalse();
    }

    [Fact]
    public void GetEffectiveFlags_AppliesEnvironmentOverrideBeforeReturning()
    {
        OperatorFeatureSettings persisted = new()
        {
            AttentionEnabled = true,
            OfflineWriteReplayEnabled = true,
        };
        IConfiguration config = ConfigWith(("OperatorFeatures:offlineWriteReplayEnabled", "false"));

        IOperatorFeatureGate gate = CreateGate(persisted, config);
        OperatorFeatureFlagsDto flags = gate.GetEffectiveFlags();

        flags.AttentionEnabled.Should().BeTrue();
        flags.OfflineWriteReplayEnabled.Should().BeFalse();
    }

    [Fact]
    public void GetEffectiveFlags_UsesDefaults_WhenRepositoryThrows()
    {
        // Blocker 2 from the #725 convergence: capability lookup MUST NOT 500 when the
        // persisted-settings acquisition throws. Any repository failure (DB down, provider
        // startup race, malformed row) must degrade to the documented defaults.
        Mock<IAppSettingsRepository> repo = new();
        repo.Setup(r => r.GetReadOnlyAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cold start / DB unavailable"));

        OperatorFeatureGate gate = new(repo.Object, EmptyConfig(), NullLogger<OperatorFeatureGate>.Instance);

        OperatorFeatureFlagsDto flags = gate.GetEffectiveFlags();
        flags.AttentionEnabled.Should().BeTrue();
        flags.NativePushEnabled.Should().BeFalse();
        flags.FilamentCoverageEnabled.Should().BeTrue();
        flags.OfflineWriteReplayEnabled.Should().BeTrue();
    }

    [Fact]
    public void GetEffectiveFlags_UsesDefaults_WhenPersistedRowIsMalformedJson()
    {
        Mock<IAppSettingsRepository> repo = new();
        repo.Setup(r => r.GetReadOnlyAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettingsEntity
            {
                Key = OperatorFeatureSettings.SectionName,
                SettingsJson = "{ not-json",
                UpdatedAt = DateTime.UtcNow,
            });

        OperatorFeatureGate gate = new(repo.Object, EmptyConfig(), NullLogger<OperatorFeatureGate>.Instance);

        OperatorFeatureFlagsDto flags = gate.GetEffectiveFlags();
        flags.AttentionEnabled.Should().BeTrue("malformed rows fall back to defaults");
        flags.NativePushEnabled.Should().BeFalse();
    }

    [Fact]
    public void EnvironmentFalse_StillApplies_WhenRepositoryThrows()
    {
        // Env override must remain effective even in the DB-degraded path so on-call rollback
        // works when the DB itself is the incident.
        Mock<IAppSettingsRepository> repo = new();
        repo.Setup(r => r.GetReadOnlyAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        IConfiguration config = ConfigWith(("OperatorFeatures:attentionEnabled", "false"));
        OperatorFeatureGate gate = new(repo.Object, config, NullLogger<OperatorFeatureGate>.Instance);

        OperatorFeatureFlagsDto flags = gate.GetEffectiveFlags();
        flags.AttentionEnabled.Should().BeFalse();
        flags.FilamentCoverageEnabled.Should().BeTrue("only the overridden flag flips");
    }

    [Fact]
    public void FlagsDto_SerializesInCamelCase()
    {
        OperatorFeatureFlagsDto flags = new() { AttentionEnabled = false };

        string json = JsonSerializer.Serialize(flags, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        });

        json.Should().Contain("\"attentionEnabled\":false");
        json.Should().Contain("\"nativePushEnabled\":false");
        json.Should().Contain("\"filamentCoverageEnabled\":true");
        json.Should().Contain("\"offlineWriteReplayEnabled\":true");
    }

    [Fact]
    public void RuntimeDatabaseUpdate_ObservedOnNextGateCall()
    {
        // Runtime DB updates take effect on the very next request (issue #725 acceptance).
        // In production the gate is scoped and re-reads the repository on every property
        // access; here we simulate the next request by returning a different row on the
        // second call.
        Mock<IAppSettingsRepository> repo = new();
        repo.SetupSequence(r => r.GetReadOnlyAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettingsEntity
            {
                Key = OperatorFeatureSettings.SectionName,
                SettingsJson = JsonSerializer.Serialize(new OperatorFeatureSettings { AttentionEnabled = true }),
                UpdatedAt = DateTime.UtcNow,
            })
            .ReturnsAsync(new AppSettingsEntity
            {
                Key = OperatorFeatureSettings.SectionName,
                SettingsJson = JsonSerializer.Serialize(new OperatorFeatureSettings { AttentionEnabled = false }),
                UpdatedAt = DateTime.UtcNow,
            });

        OperatorFeatureGate gate = new(repo.Object, EmptyConfig(), NullLogger<OperatorFeatureGate>.Instance);

        gate.IsEnabled(OperatorFeature.Attention).Should().BeTrue();
        gate.IsEnabled(OperatorFeature.Attention).Should().BeFalse();
    }

    [Fact]
    public void GateInstance_DoesNotCacheSettingsBetweenCalls()
    {
        // Guards against a regression where the gate might snapshot settings in its ctor
        // instead of consulting the repository on every call. Each IsEnabled() call must
        // trigger a fresh GetReadOnlyAsync(OperatorFeatures).
        Mock<IAppSettingsRepository> repo = new();
        repo.Setup(r => r.GetReadOnlyAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AppSettingsEntity?)null);

        OperatorFeatureGate gate = new(repo.Object, EmptyConfig(), NullLogger<OperatorFeatureGate>.Instance);

        gate.IsEnabled(OperatorFeature.Attention);
        gate.IsEnabled(OperatorFeature.FilamentCoverage);
        gate.GetEffectiveFlags();

        repo.Verify(r => r.GetReadOnlyAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
