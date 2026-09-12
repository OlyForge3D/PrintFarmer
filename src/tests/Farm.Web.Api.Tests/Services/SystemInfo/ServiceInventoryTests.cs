using System.Text.Json.Serialization;
using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.SystemStatus;
using Xunit;

namespace Farm.Web.Api.Tests.Services.SystemInfo;

public sealed class ServiceInventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Commit = new('a', 40);
    private static readonly string Digest = "sha256:" + new string('b', 64);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("daily")]
    [InlineData("stable")]
    public void Evaluate_NewOrLegacySelection_DefaultsStableWithoutProvenance(string? selection)
    {
        ServiceInventoryDto result = Evaluate([new() { ServiceId = "api", Required = true }], selection);
        Assert.Equal("stable", result.SelectedChannel);
        Assert.Null(result.ObservedChannel);
        Assert.Null(result.TargetChannel);
        Assert.Equal(InventoryEligibility.NotManaged, result.Eligibility);
        Assert.Equal(InventoryCompatibilityState.Unknown, result.CompatibilityState);
    }

    [Fact]
    public void Evaluate_ExplicitInsider_DoesNotRelabelLegacyBuild()
    {
        ServiceInventoryDto result = Evaluate([new() { ApplicationVersion = "1.2.3-daily.7" }], "insider");
        Assert.Equal("insider", result.SelectedChannel);
        Assert.Equal("HostConfiguration", result.SelectionSource);
        Assert.Null(result.Services[0].ObservedChannel);
        Assert.Equal("1.2.3-daily.7", result.Services[0].ApplicationVersion);
    }

    [Fact]
    public void Evaluate_MixedChannels_PreservesReplicasAndBlocks()
    {
        ServiceInventoryDto result = Evaluate([Verified("a"), Verified("b", "insider")]);
        Assert.Equal(2, result.Services.Count);
        Assert.Equal(InventoryCompatibilityState.MixedChannel, result.CompatibilityState);
        Assert.Equal(InventoryChannelState.Mixed, result.ChannelState);
        Assert.Equal(InventoryEligibility.Blocked, result.Eligibility);
        Assert.Null(result.ObservedChannel);
        Assert.Equal("1.2.3-insider.10", result.Services[1].Identity!.CanonicalVersion);
    }

    [Fact]
    public void Evaluate_InsiderObservedStableSelected_MismatchDoesNotEnroll()
    {
        ServiceInventoryDto result = Evaluate([Verified("a", "insider")]);
        Assert.Equal("stable", result.SelectedChannel);
        Assert.Equal("insider", result.ObservedChannel);
        Assert.Equal(InventoryChannelState.Mismatch, result.Services[0].ChannelState);
        Assert.Equal(InventoryEligibility.Blocked, result.Eligibility);
    }

    [Fact]
    public void Evaluate_OldImport_RetainsTimesAndCompatibilityIndependentOfFreshness()
    {
        ServiceReplicaObservationDto imported = Verified("a") with { ObservedAt = Now.AddDays(-2), LastSuccessAt = Now.AddDays(-2), VerifiedAt = Now.AddDays(-3) };
        ServiceInventoryDto result = Evaluate([imported]);
        Assert.Equal(InventoryObservationState.Stale, result.Services[0].ObservationState);
        Assert.Equal(imported.ObservedAt, result.Services[0].ObservedAt);
        Assert.Equal(imported.VerifiedAt, result.Services[0].VerifiedAt);
        Assert.Equal(InventoryChannelState.Stale, result.ChannelState);
        Assert.Equal(InventoryCompatibilityState.Compatible, result.CompatibilityState);
        Assert.Equal(InventoryEligibility.NotManaged, result.Eligibility);
    }

    [Fact]
    public void Evaluate_OptionalNotInstalled_DoesNotCreateMixedSet()
    {
        ServiceInventoryDto result = Evaluate([Verified("a"), Verified("optional", "insider") with { Required = false, ObservationState = InventoryObservationState.NotInstalled }]);
        Assert.Equal(InventoryCompatibilityState.Compatible, result.CompatibilityState);
        Assert.Equal("stable", result.ObservedChannel);
        Assert.Null(result.Services[1].Identity);
    }

    [Fact]
    public void Evaluate_SameVersionDifferentReplicaDigests_IsIncompatible()
    {
        ServiceInventoryDto result = Evaluate([Verified("a"), Verified("b") with { PlatformDigest = "sha256:" + new string('c', 64) }]);
        Assert.Equal(InventoryCompatibilityState.Incompatible, result.CompatibilityState);
        Assert.Equal(InventoryEligibility.Blocked, result.Eligibility);
    }

    [Fact]
    public void Evaluate_DifferentComponentsHaveDifferentDigests_IsNotConflict()
    {
        ServiceInventoryDto result = Evaluate([Verified("a"), Verified("b") with { ServiceId = "frontend", PlatformDigest = "sha256:" + new string('c', 64) }]);
        Assert.Equal(InventoryCompatibilityState.Compatible, result.CompatibilityState);
    }

    [Fact]
    public void Evaluate_MutableAliasMoved_DoesNotChangeRunningIdentity()
    {
        ServiceReplicaObservationDto original = Verified("a") with { ConfiguredImage = "printfarmer:latest" };
        ServiceInventoryDto first = Evaluate([original]);
        ServiceInventoryDto second = Evaluate([original with { ConfiguredImage = "printfarmer:stable" }]);
        Assert.Equal(first.Services[0].Identity, second.Services[0].Identity);
        Assert.Equal(first.Services[0].PlatformDigest, second.Services[0].PlatformDigest);
        Assert.Equal(first.CompatibilityState, second.CompatibilityState);
    }

    [Fact]
    public void Evaluate_MissingPlatformDigest_DoesNotSubstituteIndexDigest()
    {
        ServiceInventoryDto result = Evaluate([Verified("a") with { PlatformDigest = null, IndexDigest = Digest }]);
        Assert.Null(result.Services[0].PlatformDigest);
        Assert.Equal(InventoryCompatibilityState.Unknown, result.CompatibilityState);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("tag")]
    [InlineData("channel")]
    [InlineData("peeled-sha")]
    [InlineData("authorization")]
    [InlineData("self-report")]
    [InlineData("zero-n")]
    [InlineData("leading-zero")]
    public void Evaluate_InvalidAuthorizationBinding_RemainsUnknown(string invalid)
    {
        ServiceReplicaObservationDto row = Verified("a", "insider");
        row = invalid switch
        {
            "branch" => row with { Identity = row.Identity! with { SourceBranch = "release/v1.2.3" } },
            "tag" => row with { Identity = row.Identity! with { SourceTag = "latest" } },
            "channel" => row with { Identity = row.Identity! with { Channel = "stable" } },
            "peeled-sha" => row with { Identity = row.Identity! with { AuthorizedBranchHead = new string('c', 40) } },
            "authorization" => row with { VerificationSource = null },
            "self-report" => row with { Source = "SelfReport" },
            "zero-n" => row with { Identity = row.Identity! with { CanonicalVersion = "1.2.3-insider.0", SourceTag = "v1.2.3-insider.0", ReleaseId = "insider:1.2.3-insider.0" } },
            _ => row with { Identity = row.Identity! with { CanonicalVersion = "1.2.3-insider.01", SourceTag = "v1.2.3-insider.01", ReleaseId = "insider:1.2.3-insider.01" } },
        };
        ServiceInventoryDto result = Evaluate([row]);
        Assert.Null(result.Services[0].Identity);
        Assert.Null(result.ObservedChannel);
        Assert.Equal(InventoryCompatibilityState.Unknown, result.CompatibilityState);
    }

    [Fact]
    public void Evaluate_HistoricalAuthorization_DoesNotQueryDeletedBranchesOrMovingHeads()
    {
        ServiceInventoryDto result = Evaluate([Verified("a")]);
        Assert.Equal(Commit, result.Services[0].Identity!.AuthorizedBranchHead);
        Assert.Equal("main", result.Services[0].Identity!.SourceBranch);
        Assert.Equal("LocalVerifiedImport", result.Services[0].VerificationSource);
    }

    [Fact]
    public void Evaluate_MixedApplicationBuilds_DoesNotCollapseWorkerEngineVersions()
    {
        ServiceInventoryDto result = Evaluate([
            new() { ServiceId = "worker", InstanceId = "a", ApplicationVersion = "1.2.3", EngineVersion = "2.4.2" },
            new() { ServiceId = "worker", InstanceId = "b", ApplicationVersion = "1.2.4", EngineVersion = "2.4.2" },
        ]);
        Assert.Equal(InventoryCompatibilityState.MixedRelease, result.CompatibilityState);
        Assert.Equal(2, result.Services.Count);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("0.0.0", null)]
    [InlineData("latest", null)]
    [InlineData("C:/private/build", null)]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.7", "1.2.3-beta.7")]
    public void Parse_MissingOrLegacyBuild_NeverFabricatesVersion(string? input, string? expected)
    {
        Assert.Equal(expected, ApplicationBuildObservation.Parse(input).Version);
        Assert.Null(ApplicationBuildObservation.Parse(input).Commit);
    }

    [Fact]
    public void Parse_CanonicalBuildMarker_RetainsFullSha()
    {
        Assert.Equal(("1.2.3-insider.10", Commit), ApplicationBuildObservation.Parse($"1.2.3-insider.10+sha.{Commit}"));
    }

    [Fact]
    public void Serialize_RestAndSignalRPolicies_RetainNullsCamelCaseAndStringEnums()
    {
        foreach (JsonIgnoreCondition ignore in new[] { JsonIgnoreCondition.WhenWritingNull, JsonIgnoreCondition.Never })
        {
            JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = ignore };
            using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(Evaluate([new()]), options));
            JsonElement row = json.RootElement.GetProperty("services")[0];
            Assert.Equal("Unknown", row.GetProperty("observationState").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("platformDigest").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("applicationVersion").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("identity").ValueKind);
            Assert.False(row.TryGetProperty("SourceCommit", out _));
        }
    }

    private static ServiceInventoryDto Evaluate(ServiceReplicaObservationDto[] rows, string? selection = null) =>
        ServiceInventoryEvaluator.Evaluate(rows, selection, Now);

    private static ServiceReplicaObservationDto Verified(string instance, string channel = "stable")
    {
        string version = channel == "stable" ? "1.2.3" : "1.2.3-insider.10";
        return new()
        {
            ServiceId = "api", InstanceId = instance, Component = "api", Required = true,
            ApplicationVersion = version, SourceCommit = Commit, Source = "VerifiedImport",
            ObservationState = InventoryObservationState.Observed, ObservedAt = Now, LastSuccessAt = Now,
            VerificationSource = "LocalVerifiedImport", VerifiedAt = Now, Platform = "linux/amd64",
            PlatformDigest = Digest, ManifestDigest = Digest,
            Identity = new()
            {
                CanonicalVersion = version, BaseVersion = "1.2.3", ReleaseId = $"{channel}:{version}", Channel = channel,
                SourceCommit = Commit, AuthorizedBranchHead = Commit, SourceBranch = channel == "stable" ? "main" : "development",
                SourceTag = $"v{version}", BuildId = "100", BuildAttempt = "1", WorkflowIdentity = "authoritative-workflow",
                AllocationIdentity = "reservation-10",
            },
        };
    }
}
