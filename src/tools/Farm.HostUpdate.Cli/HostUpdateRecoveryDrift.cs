using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Farm.HostUpdate.Cli;

/// <summary>One observed difference between the recorded authorization and the host as it is now.</summary>
internal sealed record HostUpdateDriftItem(string Code, string Recorded, string Observed);

/// <summary>
/// Drift between the journaled authorization and the current host, plus the reapproval token an
/// operator must retype to proceed. The token binds the recorded request, every drift item, the
/// current configuration fingerprint and the current installed state, so any further change
/// between preview and confirm invalidates it.
/// </summary>
internal sealed record HostUpdateDriftReport(
    IReadOnlyList<HostUpdateDriftItem> Items,
    string ConfigurationFingerprint,
    string? ReapprovalToken)
{
    public bool HasDrift => Items.Count > 0;
}

/// <summary>The current standing policy as seen by this process, read without any write.</summary>
internal sealed record HostUpdatePolicyObservation(bool Available, long Revision, string Fingerprint, string Channel, string? Error)
{
    public static HostUpdatePolicyObservation Unavailable(string error) => new(false, -1, string.Empty, string.Empty, error);
}

/// <summary>
/// Detects drift since the recorded authorization (issue #2998): host platform, standing policy
/// revision/fingerprint/channel, and the prior installed state. It never resolves mutable tags,
/// never trusts unsigned material, and never writes: the host-state root is opened with
/// <see cref="HostStatePath.OpenReadOnly"/>, which skips the write probe.
/// </summary>
internal static class HostUpdateRecoveryDrift
{
    public const string HostPlatformDrift = "host_platform_drift";
    public const string PolicyUnverifiable = "policy_unverifiable";
    public const string PolicyRevisionDrift = "policy_revision_drift";
    public const string PolicyFingerprintDrift = "policy_fingerprint_drift";
    public const string ChannelDrift = "channel_drift";
    public const string PriorStateChanged = "prior_state_changed_since_authorization";
    public const string PriorStateMatchesTarget = "prior_state_matches_target";

    private const string TokenPrefix = "drift-";

    public static HostUpdatePolicyObservation ReadPolicy(IConfiguration configuration)
    {
        HostStateOptions? options;
        try
        {
            options = configuration.GetSection(HostStateOptions.SectionName).Get<HostStateOptions>();
        }
        catch (InvalidOperationException)
        {
            return HostUpdatePolicyObservation.Unavailable("host_state_configuration_invalid");
        }

        if (options is null || !options.Enabled)
        {
            return HostUpdatePolicyObservation.Unavailable("host_state_not_enabled");
        }

        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            return HostUpdatePolicyObservation.Unavailable("host_state_not_configured");
        }

        try
        {
            using var repository = new FileHostUpdateAutomationPolicyRepository(HostStatePath.OpenReadOnly(options));
            HostUpdatePolicyReadResult result = repository.Read();
            if (!result.Available)
            {
                return HostUpdatePolicyObservation.Unavailable(result.Error ?? "host_update_policy_unavailable");
            }

            HostUpdateSchedulerSettings settings = HostStateHostUpdateSchedulerSettings.ToSchedulerSettings(result.Policy);
            return new(true, settings.PolicyRevision, settings.Fingerprint, settings.Channel, null);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or SecurityException or InvalidOperationException)
        {
            // Messages may carry host paths; only the type is reported.
            return HostUpdatePolicyObservation.Unavailable("host_state_unverifiable:" + exception.GetType().Name);
        }
    }

    public static string? CurrentPlatform()
    {
        try
        {
            return HostUpdateHostPlatform.Current();
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    public static HostUpdateDriftReport Detect(
        HostUpdateExecutionRequest recorded,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        InstalledHostState? installed,
        HostUpdateRecoveryOutcomeRecord? existingOutcome,
        HostUpdatePolicyObservation policy,
        string? currentPlatform,
        string configurationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(policy);

        var items = new List<HostUpdateDriftItem>();
        if (!string.Equals(recorded.HostPlatform, currentPlatform, StringComparison.Ordinal))
        {
            items.Add(new(HostPlatformDrift, recorded.HostPlatform, currentPlatform ?? "unsupported"));
        }

        if (!policy.Available)
        {
            items.Add(new(PolicyUnverifiable, $"revision={recorded.PolicyRevision}", policy.Error ?? "host_update_policy_unavailable"));
        }
        else
        {
            if (policy.Revision != recorded.PolicyRevision)
            {
                items.Add(new(PolicyRevisionDrift, recorded.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), policy.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            if (!string.Equals(policy.Fingerprint, recorded.PolicyFingerprint, StringComparison.Ordinal))
            {
                items.Add(new(PolicyFingerprintDrift, recorded.PolicyFingerprint, policy.Fingerprint));
            }

            string recordedChannel = ChannelName(recorded.Channel);
            if (!string.Equals(policy.Channel, recordedChannel, StringComparison.Ordinal))
            {
                items.Add(new(ChannelDrift, recordedChannel, policy.Channel));
            }
        }

        // Once a recovery attempt has durably rolled back, the installed state is the one it wrote
        // itself; the prior-identity comparison only applies before any successful rollback.
        bool priorComparable = existingOutcome is null || existingOutcome.Outcome == HostUpdateRecoveryOutcome.NeedsOperator;
        if (priorComparable && installed is not null && activities.Count > 0)
        {
            DateTimeOffset authorizedAt = activities[0].RecordedAt;
            if (installed.RecordedAt > authorizedAt)
            {
                items.Add(new(PriorStateChanged, "before:" + Timestamp(authorizedAt), $"{installed.ReleaseId}@{Timestamp(installed.RecordedAt)}"));
            }

            if (string.Equals(installed.ReleaseId, recorded.ReleaseId, StringComparison.Ordinal) ||
                string.Equals(installed.ManifestDigest, recorded.ManifestDigest, StringComparison.Ordinal))
            {
                items.Add(new(PriorStateMatchesTarget, $"{recorded.ReleaseId}@{recorded.ManifestDigest}", $"{installed.ReleaseId}@{installed.ManifestDigest}"));
            }
        }

        HostUpdateDriftItem[] ordered = [.. items.OrderBy(item => item.Code, StringComparer.Ordinal)];
        string? token = ordered.Length == 0
            ? null
            : TokenPrefix + Hash(new
            {
                Binding = HostUpdateRequestBinding.Compute(recorded),
                Items = ordered,
                Configuration = configurationFingerprint,
                Installed = InstalledStateHash(installed),
            })[..32];
        return new(ordered, configurationFingerprint, token);
    }

    /// <summary>Content hash of the evaluated installed state (<c>none</c> when absent).</summary>
    public static string InstalledStateHash(InstalledHostState? installed) => installed is null ? "none" : Hash(installed);

    /// <summary>
    /// Fingerprint of the configuration the recovery engine will act on. Credentials are never
    /// included: only the provider and, for SQLite, the data-source path contribute.
    /// </summary>
    public static string ConfigurationFingerprint(HostUpdateExecutionOptions options, DatabaseProviderConfiguration database)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(database);
        return "sha256:" + Hash(new
        {
            options.RootDirectory,
            options.ComposeProjectName,
            ComposeFiles = options.ComposeFiles.Select(file => new { File = file, Sha256 = FileHash(file) }).ToArray(),
            ServiceMappings = options.ServiceMappings
                .OrderBy(m => m.ServiceId, StringComparer.Ordinal)
                .Select(m => new { m.ServiceId, m.ComposeServiceName, m.ImageEnvironmentVariable, m.ImageRepository })
                .ToArray(),
            OwnedDirectories = options.OwnedDirectories.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key, p.Value }).ToArray(),
            OptionalOwnedDirectories = options.OptionalOwnedDirectories.Order(StringComparer.Ordinal).ToArray(),
            HostExecutablePaths = options.HostExecutablePaths.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key, p.Value }).ToArray(),
            ActiveServiceIds = options.ActiveServiceIds.Order(StringComparer.Ordinal).ToArray(),
            Provider = database.Provider.ToLowerInvariant(),
            SqliteDataSource = database.IsSqlite ? SqliteDataSource(database.ConnectionString) : null,
        });
    }

    internal static string ChannelName(HostUpdateExecutionChannel channel) => channel switch
    {
        HostUpdateExecutionChannel.Stable => Farm.Infrastructure.Settings.UpdateChannelSettings.StableChannel,
        HostUpdateExecutionChannel.Insider => Farm.Infrastructure.Settings.UpdateChannelSettings.InsiderChannel,
        _ => channel.ToString(),
    };

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string FileHash(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return File.Exists(full) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))) : "missing";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return "unreadable";
        }
    }

    private static string? SqliteDataSource(string connectionString)
    {
        try
        {
            return new SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Hash(object value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
