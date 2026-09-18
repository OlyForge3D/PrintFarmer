using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.SystemStatus;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Dtos;

public sealed class HostUpdateSchedulingStatusDtoTests
{
    [Fact]
    public void Serialization_UsesCamelCaseStringEnumsAndExplicitNulls()
    {
        var dto = new SystemInfoDto
        {
            App = new() { Version = "1", Uptime = "1s", Hostname = "host" },
            Cpu = new() { Cores = 1, UsagePercent = 0 },
            Memory = new() { UsedBytes = 1, TotalBytes = 2 },
            Disk = new() { UsedBytes = 1, TotalBytes = 2, ArchiveBytes = 0, DatabaseBytes = 0 },
            Services = [],
            Database = new() { Engine = "sqlite", Version = "3", PrinterCount = 0, ArchiveCount = 0 },
            UpdateScheduling = new()
            {
                ConfiguredEnabled = false,
                EffectiveEnabled = false,
                SelectedChannel = "stable",
                EffectiveChannel = null,
                PolicyRevision = 0,
                LastAttemptAt = null,
                NextAttemptAt = null,
                Backoff = new() { State = HostUpdateBackoffState.Unknown, ConsecutiveFailures = 0, Until = null, Reasons = [] },
                KillSwitch = new() { Enabled = false, Reason = null },
                Executor = new() { State = HostUpdateExecutorState.Unavailable, Reason = "executor_not_wired" },
                Reasons = ["disabled"]
            }
        };

        string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });

        json.Should().Contain("\"updateScheduling\"").And.Contain("\"state\":\"Unknown\"").And.Contain("\"effectiveChannel\":null");
    }

    [Theory]
    [InlineData(HostUpdateBackoffState.None)]
    [InlineData(HostUpdateBackoffState.Waiting)]
    [InlineData(HostUpdateBackoffState.Due)]
    [InlineData(HostUpdateBackoffState.Unknown)]
    public void Serialization_RoundTripsEveryBackoffState(HostUpdateBackoffState state)
    {
        var dto = new HostUpdateBackoffDto { State = state, ConsecutiveFailures = 2, Until = DateTimeOffset.UnixEpoch, Reasons = ["kill_switch"] };

        string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });
        HostUpdateBackoffDto? roundTripped = JsonSerializer.Deserialize<HostUpdateBackoffDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });

        json.Should().Contain($"\"state\":\"{state}\"");
        roundTripped.Should().NotBeNull();
        roundTripped!.State.Should().Be(state);
        roundTripped.Reasons.Should().Contain("kill_switch");
    }

    [Theory]
    [InlineData(HostUpdateExecutorState.Unavailable)]
    [InlineData(HostUpdateExecutorState.Available)]
    [InlineData(HostUpdateExecutorState.Busy)]
    [InlineData(HostUpdateExecutorState.RecoveryRequired)]
    public void Serialization_RoundTripsEveryExecutorState(HostUpdateExecutorState state)
    {
        var dto = new HostUpdateExecutorDto { State = state, Reason = "some_reason" };

        string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });

        json.Should().Contain($"\"state\":\"{state}\"").And.Contain("\"reason\":\"some_reason\"");
    }

    [Fact]
    public void UnavailableProvider_UsesConcretePolicyAndReportsPhysicalGapsAsIneffectiveReasons()
    {
        var provider = new UnavailableHostUpdateSchedulingStatusProvider(
            settings: null!,
            policyRepository: new FixedPolicyRepository(new HostUpdateAutomationPolicy(Enabled: true, Channel: "stable", Revision: 7)),
            replayAnchor: null,
            replayStore: new UnavailableHostUpdateReplayStore(),
            executor: new UnavailableHostUpdateExecutor(),
            admissionFence: new UnavailableHostUpdateAdmissionFence());

        HostUpdateSchedulingStatusDto status = provider.GetStatus();

        status.ConfiguredEnabled.Should().BeTrue();
        status.EffectiveEnabled.Should().BeFalse();
        status.SelectedChannel.Should().Be("stable");
        status.EffectiveChannel.Should().BeNull();
        status.PolicyRevision.Should().Be(7);
        status.Reasons.Should().Contain(HostUpdateSchedulingAvailability.ProtectedReplayAnchorReason);
        status.Reasons.Should().Contain("host_update_recovery_unavailable");
        status.Reasons.Should().Contain(HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason);
        status.Reasons.Should().NotContain(HostUpdateSchedulingAvailability.PolicyMutationReason);
    }

    [Fact]
    public void UnavailableProvider_DoesNotFabricatePolicyAuthorityWhenPolicyStorageFails()
    {
        var provider = new UnavailableHostUpdateSchedulingStatusProvider(
            settings: null!,
            policyRepository: new UnavailablePolicyRepository("host_update_policy_corrupt"),
            replayAnchor: null,
            replayStore: new UnavailableHostUpdateReplayStore(),
            executor: new UnavailableHostUpdateExecutor(),
            admissionFence: new InactiveHostUpdateAdmissionFence());

        HostUpdateSchedulingStatusDto status = provider.GetStatus();

        status.ConfiguredEnabled.Should().BeFalse();
        status.PolicyRevision.Should().Be(0);
        status.Reasons.Should().Contain("host_update_policy_corrupt");
    }

    [Fact]
    public void UnwiredStatus_IsNull()
    {
        new UnwiredHostUpdateSchedulingStatusProvider().GetStatus().Should().BeNull();
    }

    private sealed class UnavailablePolicyRepository(string error) : IHostUpdateAutomationPolicyRepository
    {
        public HostUpdatePolicyReadResult Read() => new(false, new HostUpdateAutomationPolicy(Enabled: true, Revision: 99), error);

        public Task<HostUpdatePolicyReadResult> ReplaceAsync(HostUpdateAutomationPolicy policy, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new HostUpdatePolicyReadResult(false, policy, error));
    }

    private sealed class FixedPolicyRepository(HostUpdateAutomationPolicy policy) : IHostUpdateAutomationPolicyRepository
    {
        public HostUpdatePolicyReadResult Read() => new(true, policy, null);

        public Task<HostUpdatePolicyReadResult> ReplaceAsync(HostUpdateAutomationPolicy policy, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new HostUpdatePolicyReadResult(true, policy, null));
    }
}
