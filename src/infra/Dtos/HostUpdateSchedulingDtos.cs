using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Dtos;

/// <summary>Scheduler availability reported by the system status endpoint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateExecutorState
{
    Unknown,
    Unavailable,
    Available,
    Busy,
    RecoveryRequired
}

/// <summary>Backoff state reported by the scheduler.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateBackoffState
{
    Unknown,
    None,
    Waiting
}

/// <summary>Explicit automatic update status. Null means the scheduler is not wired.</summary>
public sealed record HostUpdateSchedulingStatusDto
{
    public required bool ConfiguredEnabled { get; init; }

    public required bool EffectiveEnabled { get; init; }

    public required string SelectedChannel { get; init; }

    public string? EffectiveChannel { get; init; }

    public required long PolicyRevision { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public required HostUpdateBackoffDto Backoff { get; init; }

    public required HostUpdateKillSwitchDto KillSwitch { get; init; }

    public required HostUpdateExecutorDto Executor { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>Automatic update retry state.</summary>
public sealed record HostUpdateBackoffDto
{
    public required HostUpdateBackoffState State { get; init; }

    public required int ConsecutiveFailures { get; init; }

    public DateTimeOffset? Until { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>Automatic update kill-switch state.</summary>
public sealed record HostUpdateKillSwitchDto
{
    public required bool Enabled { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Executor capability state.</summary>
public sealed record HostUpdateExecutorDto
{
    public required HostUpdateExecutorState State { get; init; }

    public string? Reason { get; init; }
}
