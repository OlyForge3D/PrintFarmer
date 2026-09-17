using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>Legacy DTO retained for deserialization compatibility only. Standing authorization is mutated exclusively through IHostUpdateAutomationPolicyRepository/CAS.</summary>
public sealed class HostUpdateAutomationSettings : IValidatableSetting
{
    public const string SectionName = "UpdateAutomation";

    /// <summary>Enables scheduler policy evaluation. Defaults to disabled.</summary>
    [JsonPropertyName("configuredEnabled")]
    public bool ConfiguredEnabled { get; set; }

    /// <summary>Stops automatic execution without changing the selected policy.</summary>
    [JsonPropertyName("killSwitchEnabled")]
    public bool KillSwitchEnabled { get; set; }

    /// <summary>Monotonically increasing persisted policy revision.</summary>
    [JsonPropertyName("policyRevision")]
    public long PolicyRevision { get; set; }

    /// <summary>Minimum poll cadence, bounded to prevent hot loops.</summary>
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 3600;

    /// <summary>Maintenance window start in UTC hour.</summary>
    [JsonPropertyName("maintenanceWindowStartHour")]
    public int MaintenanceWindowStartHour { get; set; }

    /// <summary>Maintenance window end in UTC hour.</summary>
    [JsonPropertyName("maintenanceWindowEndHour")]
    public int MaintenanceWindowEndHour { get; set; } = 24;

    /// <summary>Optional cadence override that only applies once the insider channel is selected
    /// and explicitly acknowledged; the stable <see cref="PollIntervalSeconds"/> governs otherwise.</summary>
    [JsonPropertyName("insiderPollIntervalSeconds")]
    public int? InsiderPollIntervalSeconds { get; set; }

    /// <inheritdoc />
    public void Validate()
    {
        if (PolicyRevision < 0)
        {
            throw new ValidationException("PolicyRevision cannot be negative.");
        }

        if (PollIntervalSeconds is < 60 or > 86400)
        {
            throw new ValidationException("PollIntervalSeconds must be between 60 and 86400.");
        }

        if (MaintenanceWindowStartHour is < 0 or > 23)
        {
            throw new ValidationException("MaintenanceWindowStartHour must be between 0 and 23.");
        }

        if (MaintenanceWindowEndHour is < 1 or > 24)
        {
            throw new ValidationException("MaintenanceWindowEndHour must be between 1 and 24.");
        }

        if (InsiderPollIntervalSeconds is int insiderSeconds && insiderSeconds is < 60 or > 86400)
        {
            throw new ValidationException("InsiderPollIntervalSeconds must be between 60 and 86400.");
        }
    }
}
