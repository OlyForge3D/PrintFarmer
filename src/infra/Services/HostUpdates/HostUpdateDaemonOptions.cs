using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Non-secret configuration for the enrolled host-update daemon service core (issue #3114).
/// There is deliberately no setting that enables execution, polling of approvals or automatic
/// updates: runtime updates stay off until the #2982 recovery evidence passes and the owner
/// separately authorizes enablement (docs/HOST_UPDATE_DAEMON_SECURITY.md). Any key in this
/// section that is not listed in <see cref="AllowedKeys"/> is rejected, so an attempted
/// <c>HostUpdateDaemon__Enabled=true</c> fails loudly instead of being silently ignored.
/// </summary>
public sealed class HostUpdateDaemonOptions
{
    public const string SectionName = "HostUpdateDaemon";

    /// <summary>The #2665 polling bound: never poll more often than once a minute.</summary>
    public const int MinimumPollIntervalSeconds = 60;

    /// <summary>The #2665 backoff bound: back off to at most 15 minutes.</summary>
    public const int MaximumBackoffCeilingSeconds = 900;

    public static IReadOnlySet<string> AllowedKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PollIntervalSeconds),
        nameof(MaxBackoffSeconds),
        nameof(IdentityDirectory),
    };

    /// <summary>Interval between reconciliation cycles while healthy.</summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>Upper bound for the exponential backoff applied after failed cycles.</summary>
    public int MaxBackoffSeconds { get; set; } = MaximumBackoffCeilingSeconds;

    /// <summary>
    /// Absolute path reference to the host-held identity directory (for example
    /// <c>/etc/printfarmer-host/daemon</c>). Only the reference is configured; the key is never a
    /// configuration value. Empty means the daemon runs unenrolled.
    /// </summary>
    public string IdentityDirectory { get; set; } = string.Empty;

    /// <summary>Returns fixed failure codes for unknown keys and out-of-range values; never echoes values.</summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration, HostUpdateDaemonOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = configuration.GetSection(SectionName).GetChildren()
            .Where(child => !AllowedKeys.Contains(child.Key))
            .Select(child => "daemon_setting_unknown:" + child.Key)
            .ToList();

        if (options.PollIntervalSeconds is < MinimumPollIntervalSeconds or > MaximumBackoffCeilingSeconds)
        {
            failures.Add("daemon_poll_interval_out_of_range");
        }

        if (options.MaxBackoffSeconds < options.PollIntervalSeconds || options.MaxBackoffSeconds > MaximumBackoffCeilingSeconds)
        {
            failures.Add("daemon_max_backoff_out_of_range");
        }

        if (!string.IsNullOrWhiteSpace(options.IdentityDirectory) && !Path.IsPathFullyQualified(options.IdentityDirectory))
        {
            failures.Add("daemon_identity_directory_not_absolute");
        }
        else if (!string.IsNullOrWhiteSpace(options.IdentityDirectory) && HostUpdateDaemonIdentityStorage.HasTraversalSegment(options.IdentityDirectory))
        {
            failures.Add("daemon_identity_directory_traversal_rejected");
        }

        return failures;
    }
}
