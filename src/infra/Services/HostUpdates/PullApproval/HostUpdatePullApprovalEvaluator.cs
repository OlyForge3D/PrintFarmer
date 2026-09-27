using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.HostUpdates.PullApproval;

/// <summary>Redacted authorization evidence recorded when an approval was issued in the app.</summary>
public sealed record HostUpdateApprovalAuthorizationEvidence
{
    public required long AuthorizationRevision { get; init; }

    public required DateTimeOffset AuthorizedAt { get; init; }

    /// <summary>The approving administrator reauthenticated interactively for this approval.</summary>
    public required bool Reauthenticated { get; init; }

    /// <summary>The approving principal held the <c>farm_admin</c> role (never a service identity).</summary>
    public required bool FarmAdmin { get; init; }

    /// <summary>The approving principal held <c>updates:execute</c>.</summary>
    public required bool ExecutePermission { get; init; }
}

/// <summary>An approval record held by the API, before it is evaluated against current state.</summary>
public sealed record HostUpdateStoredApproval
{
    public required string ApprovalId { get; init; }

    public required HostUpdateApprovalTargetKind TargetKind { get; init; }

    public required HostUpdateApprovalOrigin Origin { get; init; }

    public required string ReleaseId { get; init; }

    public required string ManifestDigest { get; init; }

    public string? PlanDigest { get; init; }

    public required string Channel { get; init; }

    public required string InstallationId { get; init; }

    public required long EnrollmentEpoch { get; init; }

    public required string KeyId { get; init; }

    public required long PolicyRevision { get; init; }

    public required long TrustRevision { get; init; }

    public required long HostPolicyRevision { get; init; }

    public required string TopologyFingerprint { get; init; }

    public required string ConfigurationFingerprint { get; init; }

    public required string SchemaFingerprint { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required bool Revoked { get; init; }

    public required bool Consumed { get; init; }

    public required HostUpdateApprovalAuthorizationEvidence Authorization { get; init; }
}

/// <summary>Current API-side state an approval is evaluated against.</summary>
public sealed record HostUpdatePullApprovalContext
{
    /// <summary>The verified key record of the requesting daemon; null when the host is unknown.</summary>
    public HostUpdateDaemonKeyRecord? Enrollment { get; init; }

    /// <summary>Installation ID read strictly from protected host state; null when unavailable.</summary>
    public string? StrictInstallationId { get; init; }

    public required bool KillSwitchActive { get; init; }

    public required string Channel { get; init; }

    public required long PolicyRevision { get; init; }

    public required long TrustRevision { get; init; }

    public required string TopologyFingerprint { get; init; }

    public required string ConfigurationFingerprint { get; init; }

    public required string SchemaFingerprint { get; init; }

    /// <summary>Host policy revision the daemon reported in the signed request.</summary>
    public required long ReportedHostPolicyRevision { get; init; }

    /// <summary>Currently installed canonical release; null when unknown.</summary>
    public string? InstalledReleaseId { get; init; }

    public required bool AutomaticPolicyEnabled { get; init; }

    public required bool RecoveryPrerequisitesMet { get; init; }

    public HostUpdateStoredApproval? Candidate { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// Evaluates the approval a daemon may pull. Every check fails closed and all failing reasons are
/// reported. An approval is returned only when nothing fails; automatic approvals are always denied
/// while <see cref="HostUpdatePullRuntimeGate.AutomaticUpdatesEnabled"/> is off.
/// </summary>
public static class HostUpdatePullApprovalEvaluator
{
    public static HostUpdatePullApprovalResponseDto Evaluate(HostUpdatePullApprovalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reasons = new List<string>();
        AddIdentityReasons(context.Enrollment, context.StrictInstallationId, context.Now, reasons);
        if (reasons.Count > 0)
        {
            return Denied(reasons);
        }

        if (context.KillSwitchActive)
        {
            reasons.Add(HostUpdatePullReasons.KillSwitchActive);
        }

        HostUpdateStoredApproval? candidate = context.Candidate;
        if (candidate is null)
        {
            return reasons.Count > 0
                ? Denied(reasons)
                : new HostUpdatePullApprovalResponseDto { Decision = HostUpdatePullApprovalDecision.None, Reasons = [HostUpdatePullReasons.NoApproval] };
        }

        AddCandidateReasons(context, context.Enrollment!, candidate, reasons);
        if (reasons.Count > 0)
        {
            return Denied(reasons);
        }

        return new HostUpdatePullApprovalResponseDto
        {
            Decision = HostUpdatePullApprovalDecision.Approved,
            Approval = ToDto(candidate),
            Reasons = []
        };
    }

    /// <summary>
    /// Signed re-confirmation before the first side effect. Re-runs every approval check against
    /// current state and requires the daemon's request to match the approval exactly.
    /// </summary>
    public static HostUpdateApprovalConfirmationDto Confirm(HostUpdatePullApprovalContext context, HostUpdateApprovalConfirmationRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        HostUpdatePullApprovalResponseDto evaluation = Evaluate(context);
        var reasons = new List<string>(evaluation.Reasons.Where(reason => reason != HostUpdatePullReasons.NoApproval));
        HostUpdatePullApprovalDto? approval = evaluation.Approval;
        if (evaluation.Decision != HostUpdatePullApprovalDecision.Approved || approval is null)
        {
            if (reasons.Count == 0)
            {
                reasons.Add(HostUpdatePullReasons.NoApproval);
            }
        }
        else if (!string.Equals(request.ApprovalId, approval.ApprovalId, StringComparison.Ordinal)
            || !string.Equals(request.ManifestDigest, approval.ManifestDigest, StringComparison.Ordinal)
            || !string.Equals(request.PlanDigest, approval.PlanDigest, StringComparison.Ordinal)
            || request.HostPolicyRevision != approval.HostPolicyRevision
            || !HostUpdatePullIdentifiers.IsReasonCode(request.Checkpoint))
        {
            reasons.Add(HostUpdatePullReasons.ConfirmationMismatch);
        }

        return reasons.Count == 0
            ? new HostUpdateApprovalConfirmationDto
            {
                Decision = HostUpdateApprovalConfirmationDecision.Confirmed,
                ApprovalId = approval!.ApprovalId,
                ValidUntil = approval.ExpiresAt,
                Reasons = []
            }
            : new HostUpdateApprovalConfirmationDto
            {
                Decision = HostUpdateApprovalConfirmationDecision.Denied,
                ApprovalId = request.ApprovalId,
                ValidUntil = null,
                Reasons = reasons
            };
    }

    internal static void AddIdentityReasons(HostUpdateDaemonKeyRecord? enrollment, string? strictInstallationId, DateTimeOffset now, List<string> reasons)
    {
        if (enrollment is null)
        {
            reasons.Add(HostUpdatePullReasons.UnknownHostIdentity);
            return;
        }

        switch (enrollment.State)
        {
            case HostUpdateDaemonEnrollmentState.Active:
            case HostUpdateDaemonEnrollmentState.Rotating:
                break;
            case HostUpdateDaemonEnrollmentState.Pending:
                reasons.Add(HostUpdatePullReasons.EnrollmentPending);
                break;
            case HostUpdateDaemonEnrollmentState.Revoked:
                reasons.Add(HostUpdatePullReasons.EnrollmentRevoked);
                break;
            case HostUpdateDaemonEnrollmentState.Quarantined:
                reasons.Add(HostUpdatePullReasons.EnrollmentQuarantined);
                break;
            default:
                reasons.Add(HostUpdatePullReasons.UnknownHostIdentity);
                break;
        }

        if (now >= enrollment.KeyExpiresAt)
        {
            reasons.Add(HostUpdatePullReasons.EnrollmentExpired);
        }

        if (string.IsNullOrWhiteSpace(strictInstallationId))
        {
            reasons.Add(HostUpdatePullReasons.InstallationIdentityUnavailable);
        }
        else if (!string.Equals(strictInstallationId, enrollment.InstallationId, StringComparison.Ordinal))
        {
            reasons.Add(HostUpdatePullReasons.InstallationMismatch);
        }
    }

    private static void AddCandidateReasons(
        HostUpdatePullApprovalContext context,
        HostUpdateDaemonKeyRecord enrollment,
        HostUpdateStoredApproval candidate,
        List<string> reasons)
    {
        if (!HasValidIdentity(candidate))
        {
            reasons.Add(HostUpdatePullReasons.ApprovalIdentityInvalid);
        }

        if (!string.Equals(candidate.InstallationId, enrollment.InstallationId, StringComparison.Ordinal)
            || candidate.EnrollmentEpoch != enrollment.EnrollmentEpoch
            || !string.Equals(candidate.KeyId, enrollment.KeyId, StringComparison.Ordinal))
        {
            reasons.Add(HostUpdatePullReasons.ApprovalBindingMismatch);
        }

        if (candidate.Revoked)
        {
            reasons.Add(HostUpdatePullReasons.ApprovalRevoked);
        }

        if (candidate.Consumed)
        {
            reasons.Add(HostUpdatePullReasons.ApprovalConsumed);
        }

        AddFreshnessReasons(candidate, context.Now, reasons);
        AddDriftReasons(context, candidate, reasons);
        AddDowngradeReasons(context, candidate, reasons);
        AddAuthorizationReasons(context, candidate, reasons);

        if (!context.RecoveryPrerequisitesMet)
        {
            reasons.Add(HostUpdatePullReasons.RecoveryPrerequisitesMissing);
        }
    }

    private static bool HasValidIdentity(HostUpdateStoredApproval candidate) =>
        HostUpdatePullIdentifiers.IsOpaqueId(candidate.ApprovalId)
        && HostUpdatePullIdentifiers.IsReleaseId(candidate.ReleaseId)
        && HostUpdatePullIdentifiers.IsDigest(candidate.ManifestDigest)
        && HostUpdatePullIdentifiers.IsChannel(candidate.Channel)
        && HostUpdatePullIdentifiers.IsDigest(candidate.TopologyFingerprint)
        && HostUpdatePullIdentifiers.IsDigest(candidate.ConfigurationFingerprint)
        && HostUpdatePullIdentifiers.IsDigest(candidate.SchemaFingerprint)
        && IsValidTarget(candidate)
        && candidate.Origin is HostUpdateApprovalOrigin.ManualOneTime or HostUpdateApprovalOrigin.Automatic;

    private static bool IsValidTarget(HostUpdateStoredApproval candidate) => candidate.TargetKind switch
    {
        HostUpdateApprovalTargetKind.SignedRelease => candidate.PlanDigest is null,
        HostUpdateApprovalTargetKind.SignedRecoveryPlan => HostUpdatePullIdentifiers.IsDigest(candidate.PlanDigest),
        _ => false
    };

    private static void AddFreshnessReasons(HostUpdateStoredApproval candidate, DateTimeOffset now, List<string> reasons)
    {
        if (candidate.ExpiresAt <= candidate.IssuedAt
            || candidate.ExpiresAt - candidate.IssuedAt > HostUpdateDaemonProtocol.MaxApprovalLifetime)
        {
            reasons.Add(HostUpdatePullReasons.ApprovalLifetimeExceeded);
        }

        if (candidate.IssuedAt > now + HostUpdateDaemonProtocol.ClockSkew)
        {
            reasons.Add(HostUpdatePullReasons.ApprovalNotYetValid);
        }

        if (now >= candidate.ExpiresAt)
        {
            reasons.Add(HostUpdatePullReasons.ApprovalExpired);
        }
    }

    private static void AddDriftReasons(HostUpdatePullApprovalContext context, HostUpdateStoredApproval candidate, List<string> reasons)
    {
        if (candidate.PolicyRevision != context.PolicyRevision)
        {
            reasons.Add(HostUpdatePullReasons.PolicyChanged);
        }

        if (candidate.TrustRevision != context.TrustRevision)
        {
            reasons.Add(HostUpdatePullReasons.TrustChanged);
        }

        if (candidate.HostPolicyRevision != context.ReportedHostPolicyRevision)
        {
            reasons.Add(HostUpdatePullReasons.HostPolicyChanged);
        }

        if (!string.Equals(candidate.TopologyFingerprint, context.TopologyFingerprint, StringComparison.Ordinal))
        {
            reasons.Add(HostUpdatePullReasons.TopologyDrift);
        }

        if (!string.Equals(candidate.ConfigurationFingerprint, context.ConfigurationFingerprint, StringComparison.Ordinal))
        {
            reasons.Add(HostUpdatePullReasons.ConfigurationDrift);
        }

        if (!string.Equals(candidate.SchemaFingerprint, context.SchemaFingerprint, StringComparison.Ordinal))
        {
            reasons.Add(HostUpdatePullReasons.SchemaDrift);
        }

        if (!string.Equals(candidate.Channel, context.Channel, StringComparison.Ordinal))
        {
            reasons.Add(HostUpdatePullReasons.ChannelMismatch);
        }
    }

    private static void AddDowngradeReasons(HostUpdatePullApprovalContext context, HostUpdateStoredApproval candidate, List<string> reasons)
    {
        if (candidate.TargetKind != HostUpdateApprovalTargetKind.SignedRelease)
        {
            // Recovery is a separate verified plan; its downgrade rules are enforced by the host verifier (#3116).
            return;
        }

        int? comparison = HostUpdatePullIdentifiers.CompareReleaseIds(candidate.ReleaseId, context.InstalledReleaseId);
        if (comparison is null)
        {
            reasons.Add(HostUpdatePullReasons.InstalledReleaseUnknown);
        }
        else if (comparison <= 0)
        {
            reasons.Add(HostUpdatePullReasons.DowngradeRejected);
        }
    }

    private static void AddAuthorizationReasons(HostUpdatePullApprovalContext context, HostUpdateStoredApproval candidate, List<string> reasons)
    {
        HostUpdateApprovalAuthorizationEvidence evidence = candidate.Authorization;
        bool humanAuthorized = evidence.FarmAdmin && evidence.Reauthenticated
            && evidence.AuthorizedAt <= candidate.IssuedAt;
        if (candidate.Origin == HostUpdateApprovalOrigin.ManualOneTime && (!humanAuthorized || !evidence.ExecutePermission))
        {
            reasons.Add(HostUpdatePullReasons.AuthorizationMissing);
        }

        if (candidate.Origin == HostUpdateApprovalOrigin.Automatic)
        {
            if (!humanAuthorized)
            {
                reasons.Add(HostUpdatePullReasons.AuthorizationMissing);
            }

            if (!context.AutomaticPolicyEnabled)
            {
                reasons.Add(HostUpdatePullReasons.AutomaticPolicyDisabled);
            }

            if (!HostUpdatePullRuntimeGate.AutomaticUpdatesEnabled)
            {
                reasons.Add(HostUpdatePullReasons.AutomaticUpdatesRuntimeDisabled);
            }
        }
    }

    private static HostUpdatePullApprovalResponseDto Denied(List<string> reasons) =>
        new() { Decision = HostUpdatePullApprovalDecision.Denied, Approval = null, Reasons = reasons.Distinct(StringComparer.Ordinal).ToArray() };

    private static HostUpdatePullApprovalDto ToDto(HostUpdateStoredApproval candidate) => new()
    {
        ApprovalId = candidate.ApprovalId,
        TargetKind = candidate.TargetKind,
        Origin = candidate.Origin,
        ReleaseId = candidate.ReleaseId,
        ManifestDigest = candidate.ManifestDigest,
        PlanDigest = candidate.PlanDigest,
        Channel = candidate.Channel,
        InstallationId = candidate.InstallationId,
        EnrollmentEpoch = candidate.EnrollmentEpoch,
        KeyId = candidate.KeyId,
        PolicyRevision = candidate.PolicyRevision,
        TrustRevision = candidate.TrustRevision,
        HostPolicyRevision = candidate.HostPolicyRevision,
        TopologyFingerprint = candidate.TopologyFingerprint,
        ConfigurationFingerprint = candidate.ConfigurationFingerprint,
        SchemaFingerprint = candidate.SchemaFingerprint,
        IssuedAt = candidate.IssuedAt,
        ExpiresAt = candidate.ExpiresAt,
        Authorization = new HostUpdateApprovalAuthorizationDto
        {
            AuthorizationRevision = candidate.Authorization.AuthorizationRevision,
            AuthorizedAt = candidate.Authorization.AuthorizedAt,
            Reauthenticated = candidate.Authorization.Reauthenticated
        }
    };
}
