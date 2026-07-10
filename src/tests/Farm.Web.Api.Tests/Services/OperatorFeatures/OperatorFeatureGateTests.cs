using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.OperatorFeatures;

/// <summary>
/// Unit tests for <see cref="OperatorFeatureGate"/> covering defaults, database values, and
/// the environment hard-disable override precedence required by issue #725.
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

    private static IOperatorFeatureGate CreateGate(
        OperatorFeatureSettings? settings = null,
        IConfiguration? configuration = null)
    {
        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.Get<OperatorFeatureSettings>())
            .Returns(settings ?? new OperatorFeatureSettings());

        return new OperatorFeatureGate(
            settingsService.Object,
            configuration ?? EmptyConfig(),
            NullLogger<OperatorFeatureGate>.Instance);
    }

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
            .Should().BeEquivalentTo(System.Enum.GetValues<OperatorFeature>());
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
    public void GetEffectiveFlags_UsesDefaults_WhenSettingsServiceThrows()
    {
        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.Get<OperatorFeatureSettings>())
            .Throws(new System.InvalidOperationException("cold start"));

        OperatorFeatureGate gate = new(
            settingsService.Object,
            EmptyConfig(),
            NullLogger<OperatorFeatureGate>.Instance);

        OperatorFeatureFlagsDto flags = gate.GetEffectiveFlags();
        flags.AttentionEnabled.Should().BeTrue();
        flags.NativePushEnabled.Should().BeFalse();
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
        // Runtime DB updates take effect "on the next request" (issue #725 acceptance criterion).
        // In production this is achieved because IOperatorFeatureGate is scoped and re-reads the
        // scoped ISettingsService on every property access; here we simulate the next request by
        // returning a different settings snapshot from ISettingsService.
        OperatorFeatureSettings before = new() { AttentionEnabled = true };
        OperatorFeatureSettings after = new() { AttentionEnabled = false };

        Mock<ISettingsService> settingsService = new();
        settingsService.SetupSequence(s => s.Get<OperatorFeatureSettings>())
            .Returns(before)
            .Returns(after);

        OperatorFeatureGate gate = new(
            settingsService.Object,
            EmptyConfig(),
            NullLogger<OperatorFeatureGate>.Instance);

        gate.IsEnabled(OperatorFeature.Attention).Should().BeTrue();
        gate.IsEnabled(OperatorFeature.Attention).Should().BeFalse();
    }

    [Fact]
    public void GateInstance_DoesNotCacheSettingsBetweenCalls()
    {
        // Guards against a regression where the gate might snapshot settings in its ctor
        // instead of consulting ISettingsService on every call. Each IsEnabled() call must
        // trigger a fresh Get<OperatorFeatureSettings>().
        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.Get<OperatorFeatureSettings>())
            .Returns(new OperatorFeatureSettings());

        OperatorFeatureGate gate = new(
            settingsService.Object,
            EmptyConfig(),
            NullLogger<OperatorFeatureGate>.Instance);

        gate.IsEnabled(OperatorFeature.Attention);
        gate.IsEnabled(OperatorFeature.FilamentCoverage);
        gate.GetEffectiveFlags();

        settingsService.Verify(s => s.Get<OperatorFeatureSettings>(), Times.Exactly(3));
    }
}
