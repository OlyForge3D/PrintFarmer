using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Infrastructure.OperatorFeatures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.OperatorFeatures;

/// <summary>
/// Validates the reusable ProblemDetails helper for disabled operator features and the
/// invariant that every <see cref="OperatorFeatureSettings"/> property carries the
/// <see cref="JsonPropertyNameAttribute"/> required by the metadata pipeline.
/// </summary>
public class OperatorFeatureProblemDetailsTests
{
    [Fact]
    public void Create_ByFlagName_ReturnsStandardShape()
    {
        ProblemDetails problem = OperatorFeatureProblemDetails.Create("attentionEnabled");

        problem.Status.Should().Be(404);
        problem.Title.Should().Be("Feature disabled");
        problem.Type.Should().Be(OperatorFeatureProblemDetails.TypeUri);
        problem.Extensions["code"].Should().Be(OperatorFeatureProblemDetails.CodeExtension);
        problem.Extensions["code"].Should().Be("featureDisabled");
        problem.Extensions["feature"].Should().Be("attentionEnabled");
        problem.Detail.Should().Contain("attentionEnabled");
    }

    [Fact]
    public void Create_ByGateAndFeature_UsesCanonicalFlagName()
    {
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.GetFlagName(OperatorFeature.PrintedPartsInventory))
            .Returns("printedPartsInventoryEnabled");

        ProblemDetails problem = OperatorFeatureProblemDetails.Create(gate.Object, OperatorFeature.PrintedPartsInventory);

        problem.Extensions["feature"].Should().Be("printedPartsInventoryEnabled");
        problem.Extensions["code"].Should().Be("featureDisabled");
        problem.Status.Should().Be(404);
    }

    [Fact]
    public void NotFound_ReturnsNotFoundObjectResultWith404Status()
    {
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.GetFlagName(OperatorFeature.Attention)).Returns("attentionEnabled");

        NotFoundObjectResult result = OperatorFeatureProblemDetails.NotFound(gate.Object, OperatorFeature.Attention);

        result.StatusCode.Should().Be(404);
        ProblemDetails body = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        body.Extensions["code"].Should().Be("featureDisabled");
        body.Extensions["feature"].Should().Be("attentionEnabled");
    }

    [Fact]
    public void Create_WithCustomDetail_UsesProvidedText()
    {
        ProblemDetails problem = OperatorFeatureProblemDetails.Create("shiftPlanEnabled", "Shifts feature is off for this tenant.");

        problem.Detail.Should().Be("Shifts feature is off for this tenant.");
    }

    [Fact]
    public void NotFound_HelperIsPure_NoWritesOrBroadcasts()
    {
        // Guards the helper's internal contract: the ProblemDetails builder only asks the
        // gate for the canonical flag name and touches nothing else. This does NOT prove
        // that a real gated endpoint performs no writes/broadcasts before returning — the
        // acceptance-criterion integration test for a specific endpoint lives with the first
        // feature PR (#707) that consumes this helper.
        Mock<IOperatorFeatureGate> gate = new(MockBehavior.Strict);
        gate.Setup(g => g.GetFlagName(OperatorFeature.OfflineWriteReplay))
            .Returns("offlineWriteReplayEnabled");

        NotFoundObjectResult result = OperatorFeatureProblemDetails.NotFound(
            gate.Object,
            OperatorFeature.OfflineWriteReplay,
            "Write queue is disabled; using direct writes.");

        result.StatusCode.Should().Be(404);
        ProblemDetails body = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        body.Extensions["code"].Should().Be("featureDisabled");
        body.Extensions["feature"].Should().Be("offlineWriteReplayEnabled");
        body.Detail.Should().Be("Write queue is disabled; using direct writes.");

        // Strict mock rejects any call other than GetFlagName; this is the assertion that the
        // helper itself never invokes IsEnabled, GetEffectiveFlags, or any settings API.
        gate.Verify(g => g.GetFlagName(OperatorFeature.OfflineWriteReplay), Times.Once);
        gate.VerifyNoOtherCalls();
    }

    [Fact]
    public void OperatorFeatureSettings_EveryProperty_HasJsonPropertyName()
    {
        // Invariant from the SettingsService metadata pipeline: every [AppSetting] property
        // exposed via metadata MUST carry [JsonPropertyName], otherwise ALL metadata and
        // per-key settings endpoints break. Guard the invariant explicitly.
        PropertyInfo[] props = typeof(OperatorFeatureSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        props.Should().HaveCount(8);
        foreach (PropertyInfo prop in props)
        {
            prop.GetCustomAttribute<JsonPropertyNameAttribute>()
                .Should().NotBeNull($"{prop.Name} must have [JsonPropertyName] to appear in settings metadata");
        }
    }

    [Fact]
    public void OperatorFeatureSettings_JsonPropertyNames_MatchGateFlagNames()
    {
        Mock<IAppSettingsRepository> repo = new();
        repo.Setup(r => r.GetAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AppSettingsEntity?)null);
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        IOperatorFeatureGate gate = new OperatorFeatureGate(
            repo.Object,
            config,
            NullLogger<OperatorFeatureGate>.Instance);

        System.Collections.Generic.HashSet<string> jsonNames = typeof(OperatorFeatureSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)
            .ToHashSet();

        System.Collections.Generic.HashSet<string> gateNames = gate.AllFeatures.Select(f => f.FlagName).ToHashSet();

        jsonNames.Should().BeEquivalentTo(gateNames);
    }
}
