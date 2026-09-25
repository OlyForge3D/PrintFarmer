using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
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
/// Detects drift since the recorded authorization (issues #2998, #3047): host platform, standing
/// policy revision/fingerprint/channel, and -- against the baseline journaled on the
/// <c>accepted</c> activity -- the prior installed state, configuration, release trust root and
/// database manifest binding (issue #3050).
/// It never resolves mutable tags,
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
    public const string AuthorizationBaselineUnrecorded = "authorization_baseline_unrecorded";
    public const string ConfigurationDrift = "configuration_drift";
    public const string TrustRootDrift = "trust_root_drift";
    public const string ManifestBindingDrift = "manifest_binding_drift";

    /// <summary>Recorded value reported for a schema-1 baseline, which predates the manifest binding.</summary>
    public const string ManifestBindingUnrecorded = "unrecorded";

    /// <summary>Observed value reported when the binding could not be read; always drift, never "no drift".</summary>
    public const string ManifestBindingUnreadablePrefix = "unreadable:";

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
        string configurationFingerprint,
        string? observedManifestBinding,
        string? currentTrustRootFingerprint = null)
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
        HostUpdateAuthorizationBaseline? baseline = AuthorizationBaseline(activities);
        string trustRootFingerprint = currentTrustRootFingerprint ?? HostUpdateTrustRoot.Fingerprint;
        if (!HostUpdateTrustRoot.IsPinned(recorded.TrustRoot))
        {
            items.Add(new(TrustRootDrift, recorded.TrustRoot, "unpinned"));
        }

        if (baseline is null)
        {
            items.Add(new(AuthorizationBaselineUnrecorded, "none", $"schema={HostUpdateAuthorizationBaseline.CurrentSchemaVersion}"));
        }
        else
        {
            if (!string.Equals(baseline.ConfigurationFingerprint, configurationFingerprint, StringComparison.Ordinal))
            {
                items.Add(new(ConfigurationDrift, baseline.ConfigurationFingerprint, configurationFingerprint));
            }

            if (!string.Equals(baseline.TrustRootFingerprint, trustRootFingerprint, StringComparison.Ordinal))
            {
                items.Add(new(TrustRootDrift, baseline.TrustRootFingerprint, trustRootFingerprint));
            }

            if (ManifestBindingItem(baseline, observedManifestBinding) is { } bindingItem)
            {
                items.Add(bindingItem);
            }
        }

        if (priorComparable && baseline is not null)
        {
            // Content comparison (issue #3047, H03): any change to the installed state since
            // authorization -- including a backdated RecordedAt or a deleted record -- is drift.
            string installedHash = InstalledStateHash(installed);
            if (!string.Equals(baseline.InstalledStateHash, installedHash, StringComparison.Ordinal))
            {
                items.Add(new(PriorStateChanged, baseline.InstalledStateHash, installed is null ? installedHash : $"{installed.ReleaseId}@{installedHash}"));
            }
        }
        else if (priorComparable && installed is not null && activities.Count > 0)
        {
            // Legacy fallback for journals without a baseline; always accompanied by
            // authorization_baseline_unrecorded, so it never stands alone as proof of no drift.
            DateTimeOffset authorizedAt = activities[0].RecordedAt;
            if (installed.RecordedAt > authorizedAt)
            {
                items.Add(new(PriorStateChanged, "before:" + Timestamp(authorizedAt), $"{installed.ReleaseId}@{Timestamp(installed.RecordedAt)}"));
            }
        }

        if (priorComparable && installed is not null && activities.Count > 0 &&
            (string.Equals(installed.ReleaseId, recorded.ReleaseId, StringComparison.Ordinal) ||
             string.Equals(installed.ManifestDigest, recorded.ManifestDigest, StringComparison.Ordinal)))
        {
            items.Add(new(PriorStateMatchesTarget, $"{recorded.ReleaseId}@{recorded.ManifestDigest}", $"{installed.ReleaseId}@{installed.ManifestDigest}"));
        }

        return Report(recorded, items, configurationFingerprint, installed, observedManifestBinding);
    }

    /// <summary>
    /// Drift for a terminal <c>RolledBack</c> outcome: the installed state is the one the rollback
    /// wrote, so only the database-side manifest binding is compared (issue #3050). An unreadable
    /// binding is still reported, never hidden behind the durable no-op.
    /// </summary>
    public static HostUpdateDriftReport DetectAfterRollback(
        HostUpdateExecutionRequest recorded,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        InstalledHostState? installed,
        string configurationFingerprint,
        string? observedManifestBinding)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(activities);

        var items = new List<HostUpdateDriftItem>();
        HostUpdateAuthorizationBaseline? baseline = AuthorizationBaseline(activities);
        if (baseline is not null && ManifestBindingItem(baseline, observedManifestBinding) is { } bindingItem)
        {
            items.Add(bindingItem);
        }
        else if (baseline is null && ObservedUnreadable(observedManifestBinding) is { } unreadable)
        {
            items.Add(new(ManifestBindingDrift, ManifestBindingUnrecorded, unreadable));
        }

        return Report(recorded, items, configurationFingerprint, installed, observedManifestBinding);
    }

    // Issue #3050: a missing baseline value, an unobserved or unreadable binding, or any
    // difference is drift. An unreadable binding never counts as "no drift".
    private static HostUpdateDriftItem? ManifestBindingItem(HostUpdateAuthorizationBaseline baseline, string? observedManifestBinding)
    {
        string recordedBinding = baseline.ManifestBinding ?? ManifestBindingUnrecorded;
        string observedBinding = ObservedUnreadable(observedManifestBinding) ?? observedManifestBinding!;
        return baseline.ManifestBinding is null
            || observedBinding.StartsWith(ManifestBindingUnreadablePrefix, StringComparison.Ordinal)
            || !string.Equals(recordedBinding, observedBinding, StringComparison.Ordinal)
                ? new(ManifestBindingDrift, recordedBinding, observedBinding)
                : null;
    }

    private static string? ObservedUnreadable(string? observedManifestBinding) =>
        observedManifestBinding is null
            ? ManifestBindingUnreadablePrefix + "unobserved"
            : observedManifestBinding.StartsWith(ManifestBindingUnreadablePrefix, StringComparison.Ordinal) ? observedManifestBinding : null;

    private static HostUpdateDriftReport Report(
        HostUpdateExecutionRequest recorded,
        List<HostUpdateDriftItem> items,
        string configurationFingerprint,
        InstalledHostState? installed,
        string? observedManifestBinding)
    {
        HostUpdateDriftItem[] ordered = [.. items.OrderBy(item => item.Code, StringComparer.Ordinal)];
        string? token = ordered.Length == 0
            ? null
            : TokenPrefix + Hash(new
            {
                Binding = HostUpdateRequestBinding.Compute(recorded),
                Items = ordered,
                Configuration = configurationFingerprint,
                Installed = InstalledStateHash(installed),
                ManifestBinding = observedManifestBinding,
            })[..32];
        return new(ordered, configurationFingerprint, token);
    }

    /// <summary>Content hash of the evaluated installed state (<c>none</c> when absent); shared with the executor's baseline.</summary>
    public static string InstalledStateHash(InstalledHostState? installed) => HostUpdateBaselineHashes.InstalledState(installed);

    /// <summary>Fingerprint of the configuration the recovery engine will act on; shared with the executor's baseline.</summary>
    public static string ConfigurationFingerprint(HostUpdateExecutionOptions options, DatabaseProviderConfiguration database) =>
        HostUpdateBaselineHashes.Configuration(options, database);

    /// <summary>
    /// The baseline journaled when the update was authorized, carried only by the first journal
    /// activity: a later <c>accepted</c> re-append (resume after a crash) must never supply one, or a
    /// legacy journal could be rebased onto post-authorization state. Null when none was recorded or
    /// the schema is not understood; schema 1 predates the manifest binding and reports it as drift.
    /// </summary>
    internal static HostUpdateAuthorizationBaseline? AuthorizationBaseline(IReadOnlyList<HostUpdateExecutionActivity> activities) =>
        activities.Count > 0
        && activities[0] is { State: HostUpdateExecutionState.Accepted, Phase: "accepted", AuthorizationBaseline: { } baseline }
        && baseline.SchemaVersion is >= HostUpdateAuthorizationBaseline.MinimumSupportedSchemaVersion and <= HostUpdateAuthorizationBaseline.CurrentSchemaVersion
            ? baseline
            : null;

    internal static string ChannelName(HostUpdateExecutionChannel channel) => channel switch
    {
        HostUpdateExecutionChannel.Stable => Farm.Infrastructure.Settings.UpdateChannelSettings.StableChannel,
        HostUpdateExecutionChannel.Insider => Farm.Infrastructure.Settings.UpdateChannelSettings.InsiderChannel,
        _ => channel.ToString(),
    };

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string Hash(object value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
