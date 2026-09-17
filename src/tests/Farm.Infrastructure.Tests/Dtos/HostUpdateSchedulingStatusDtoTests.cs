using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Dtos;
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

    [Fact]
    public void UnwiredStatus_IsNull()
    {
        new UnwiredHostUpdateSchedulingStatusProvider().GetStatus().Should().BeNull();
    }
}
