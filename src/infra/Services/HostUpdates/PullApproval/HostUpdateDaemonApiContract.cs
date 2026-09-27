using System.Text.RegularExpressions;

namespace Farm.Infrastructure.Services.HostUpdates.PullApproval;

/// <summary>
/// Route contract for the enrolled host-update daemon pull API (issue #3115). These routes are
/// defined but intentionally not mapped: no controller serves them until the daemon, enrollment
/// store and #2982 recovery evidence exist and the owner authorizes enablement.
/// </summary>
public static class HostUpdateDaemonApiRoutes
{
    public const string Base = "/api/host-updates/daemon/v1";
    public const string Enrollment = Base + "/enrollment";
    public const string Readiness = Base + "/readiness";
    public const string Approval = Base + "/approval";
    public const string ApprovalConfirmation = Base + "/approvals/{approvalId}/confirm";
    public const string Status = Base + "/status";

    /// <summary>Operator view of the daemon; farm_admin only, ordinary JWT authentication.</summary>
    public const string OperatorStatus = "/api/admin/host-updates/daemon/status";
}

/// <summary>Fixed protocol constants shared by the request and response signing profiles.</summary>
public static class HostUpdateDaemonProtocol
{
    public const int Version = 1;
    public const string SignatureLabel = "pf";
    public const string SignatureTag = "printfarmer-host-update-v1";
    public const string SignatureAlgorithm = "ecdsa-p256-sha256";
    public const string CounterHeader = "PrintFarmer-Request-Counter";
    public const string ResponseSignaturePrefix = "printfarmer-host-update-response-v1\n";
    public const int MaxRequestBodyBytes = 64 * 1024;
    public const int MaxListItems = 16;
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxApprovalLifetime = TimeSpan.FromMinutes(5);
}

/// <summary>Automatic-update runtime gate. Hard-coded off until #2982 passes and the owner authorizes it.</summary>
public static class HostUpdatePullRuntimeGate
{
    public static bool AutomaticUpdatesEnabled => false;
}

/// <summary>Bounded reason codes. Values are stable wire strings.</summary>
public static class HostUpdatePullReasons
{
    public const string UnknownHostIdentity = "unknown_host_identity";
    public const string EnrollmentPending = "enrollment_pending";
    public const string EnrollmentRevoked = "enrollment_revoked";
    public const string EnrollmentQuarantined = "enrollment_quarantined";
    public const string EnrollmentExpired = "enrollment_expired";
    public const string InstallationIdentityUnavailable = "installation_identity_unavailable";
    public const string InstallationMismatch = "installation_mismatch";
    public const string KillSwitchActive = "kill_switch_active";
    public const string NoApproval = "no_approval";
    public const string ApprovalBindingMismatch = "approval_binding_mismatch";
    public const string ApprovalRevoked = "approval_revoked";
    public const string ApprovalConsumed = "approval_consumed";
    public const string ApprovalExpired = "approval_expired";
    public const string ApprovalNotYetValid = "approval_not_yet_valid";
    public const string ApprovalLifetimeExceeded = "approval_lifetime_exceeded";
    public const string ApprovalIdentityInvalid = "approval_identity_invalid";
    public const string PolicyChanged = "policy_changed";
    public const string TrustChanged = "trust_changed";
    public const string HostPolicyChanged = "host_policy_changed";
    public const string TopologyDrift = "topology_drift";
    public const string ConfigurationDrift = "configuration_drift";
    public const string SchemaDrift = "schema_drift";
    public const string ChannelMismatch = "channel_mismatch";
    public const string DowngradeRejected = "downgrade_rejected";
    public const string InstalledReleaseUnknown = "installed_release_unknown";
    public const string AuthorizationMissing = "authorization_missing";
    public const string AutomaticPolicyDisabled = "automatic_policy_disabled";
    public const string AutomaticUpdatesRuntimeDisabled = "automatic_updates_runtime_disabled";
    public const string RecoveryPrerequisitesMissing = "recovery_prerequisites_missing";
    public const string ConfirmationMismatch = "confirmation_mismatch";
}

/// <summary>Format rules for identifiers carried by the contract.</summary>
public static partial class HostUpdatePullIdentifiers
{
    public static bool IsReasonCode(string? value) => value is not null && ReasonCodePattern().IsMatch(value);

    public static bool IsReleaseId(string? value) => value is not null && ReleaseIdPattern().IsMatch(value);

    public static bool IsDigest(string? value) => value is not null && DigestPattern().IsMatch(value);

    public static bool IsOpaqueId(string? value) => value is not null && OpaqueIdPattern().IsMatch(value);

    public static bool IsChannel(string? value) => value is "stable" or "insider";

    /// <summary>
    /// Orders canonical release identities. Returns null when either value is not canonical.
    /// Stable <c>vX.Y.Z</c> sorts after any <c>vX.Y.Z-insider.N</c> of the same core version.
    /// </summary>
    public static int? CompareReleaseIds(string? left, string? right)
    {
        if (!TryParseRelease(left, out (long, long, long, long) a) || !TryParseRelease(right, out (long, long, long, long) b))
        {
            return null;
        }

        return a.CompareTo(b);
    }

    private static bool TryParseRelease(string? value, out (long Major, long Minor, long Patch, long Insider) parsed)
    {
        parsed = default;
        if (value is null)
        {
            return false;
        }

        Match match = ReleaseIdPattern().Match(value);
        if (!match.Success)
        {
            return false;
        }

        // The pattern bounds each component to nine digits, so parsing cannot overflow.
        long insider = match.Groups["insider"].Success ? ParseComponent(match, "insider") : long.MaxValue;
        parsed = (ParseComponent(match, "major"), ParseComponent(match, "minor"), ParseComponent(match, "patch"), insider);
        return true;
    }

    private static long ParseComponent(Match match, string group) =>
        long.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ReasonCodePattern();

    [GeneratedRegex(@"^v(?<major>0|[1-9]\d{0,8})\.(?<minor>0|[1-9]\d{0,8})\.(?<patch>0|[1-9]\d{0,8})(-insider\.(?<insider>[1-9]\d{0,8}))?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ReleaseIdPattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex DigestPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex OpaqueIdPattern();
}
