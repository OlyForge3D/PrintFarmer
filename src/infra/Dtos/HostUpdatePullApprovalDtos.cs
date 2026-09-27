using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Dtos;

// Wire contracts for the enrolled host-update daemon pull API (issue #3115).
// See docs/HOST_UPDATE_PULL_API.md. These payloads carry only immutable signed release or plan
// identities, bounded reason codes and policy metadata. They never carry images, commands,
// Compose fragments, host paths, URLs or credentials.

/// <summary>Enrollment state of a daemon key record held in the protected host-state store.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateDaemonEnrollmentState
{
    Pending,
    Active,
    Rotating,
    Revoked,
    Quarantined
}

/// <summary>Signature algorithm used for daemon requests and API responses.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateDaemonSignatureAlgorithm
{
    EcdsaP256Sha256
}

/// <summary>Kind of signed payload carried inside a response envelope.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateDaemonResponseType
{
    EnrollmentStatus,
    Readiness,
    Approval,
    ApprovalConfirmation,
    StatusReportAck
}

/// <summary>Immutable target kind an approval may name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateApprovalTargetKind
{
    SignedRelease,
    SignedRecoveryPlan
}

/// <summary>How an approval was authorized in the app.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateApprovalOrigin
{
    ManualOneTime,
    Automatic
}

/// <summary>Result of an approval query. Anything other than <c>Approved</c> admits nothing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdatePullApprovalDecision
{
    None,
    Approved,
    Denied
}

/// <summary>Result of the signed re-confirmation made before the first side effect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateApprovalConfirmationDecision
{
    Confirmed,
    Denied
}

/// <summary>Tri-state readiness condition. <c>Unknown</c> is treated exactly like <c>NotSatisfied</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateReadinessState
{
    Unknown,
    NotSatisfied,
    Satisfied
}

/// <summary>Daemon lifecycle state reported by the daemon itself.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateDaemonState
{
    Idle,
    Waiting,
    Deferred,
    Running,
    NeedsOperator
}

/// <summary>Host-local execution mode. The app can observe it but never set it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateDaemonExecutionMode
{
    None,
    Manual,
    Automatic
}

/// <summary>Outcome of the daemon's last attempted operation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostUpdateDaemonResultOutcome
{
    None,
    Succeeded,
    Rejected,
    Failed,
    RecoveryRequired
}

/// <summary>
/// Signed response wrapper. <see cref="SignedContent"/> is the exact JSON text of a
/// <see cref="HostUpdateDaemonResponseEnvelope{TPayload}"/>; the signature covers its UTF-8 bytes
/// with a fixed domain-separation prefix, so no JSON canonicalization is required.
/// </summary>
public sealed record HostUpdateDaemonSignedResponseDto
{
    public required string SignedContent { get; init; }

    public required string ResponseKeyId { get; init; }

    public required HostUpdateDaemonSignatureAlgorithm Algorithm { get; init; }

    /// <summary>Base64url IEEE P1363 (r||s) signature.</summary>
    public required string Signature { get; init; }
}

/// <summary>Signed response body. Echoes request bindings so the daemon can detect replay and API rollback.</summary>
public sealed record HostUpdateDaemonResponseEnvelope<TPayload>
{
    public required int ProtocolVersion { get; init; }

    public required HostUpdateDaemonResponseType ResponseType { get; init; }

    public required string InstallationId { get; init; }

    public required string KeyId { get; init; }

    public required long EnrollmentEpoch { get; init; }

    /// <summary>Nonce of the request this response answers.</summary>
    public required string RequestNonce { get; init; }

    /// <summary>Counter of the request the API accepted before this one; never decreases.</summary>
    public required long AcknowledgedCounter { get; init; }

    /// <summary>Monotonic enrollment-state revision; never decreases.</summary>
    public required long EnrollmentStateRevision { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required TPayload Payload { get; init; }
}

/// <summary>Payload of <c>GET enrollment</c>, and of every signed rejection for a revoked or quarantined key.</summary>
public sealed record HostUpdateDaemonEnrollmentStatusDto
{
    public required HostUpdateDaemonEnrollmentState State { get; init; }

    public DateTimeOffset? KeyExpiresAt { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>
/// Payload of <c>GET readiness</c>. Readiness is advisory: <see cref="GrantsExecution"/> is always
/// <c>false</c>, and the daemon re-checks its own host policy regardless.
/// </summary>
public sealed record HostUpdateReadinessDto
{
    public required bool Eligible { get; init; }

    public bool GrantsExecution => false;

    public required HostUpdateMaintenanceWindowReadinessDto MaintenanceWindow { get; init; }

    public required HostUpdateReadinessConditionDto PrinterActivity { get; init; }

    public required HostUpdatePolicyReadinessDto Policy { get; init; }

    public required HostUpdateReadinessConditionDto RecoveryEvidence { get; init; }

    public required HostUpdateReadinessConditionDto HostEligibility { get; init; }

    public required bool KillSwitchActive { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>Generic readiness condition with bounded reason codes.</summary>
public sealed record HostUpdateReadinessConditionDto
{
    public required HostUpdateReadinessState State { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>App-side view of the maintenance window. The host window is enforced independently.</summary>
public sealed record HostUpdateMaintenanceWindowReadinessDto
{
    public required HostUpdateReadinessState State { get; init; }

    public DateTimeOffset? WindowStart { get; init; }

    public DateTimeOffset? WindowEnd { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>App-side policy summary. None of these values can widen host policy.</summary>
public sealed record HostUpdatePolicyReadinessDto
{
    public required HostUpdateReadinessState State { get; init; }

    public required string Channel { get; init; }

    public required long PolicyRevision { get; init; }

    public required long TrustRevision { get; init; }

    public required bool AutomaticPolicyEnabled { get; init; }

    /// <summary>Always <c>false</c> until #2982 recovery evidence passes and the owner authorizes enablement.</summary>
    public required bool AutomaticUpdatesRuntimeEnabled { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>Payload of <c>GET approval</c>. <see cref="Approval"/> is present only when <see cref="Decision"/> is <c>Approved</c>.</summary>
public sealed record HostUpdatePullApprovalResponseDto
{
    public required HostUpdatePullApprovalDecision Decision { get; init; }

    public HostUpdatePullApprovalDto? Approval { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>
/// A bounded approval: an immutable signed target identity plus the bindings it is valid for.
/// There are deliberately no image, command, Compose, path or URL members.
/// </summary>
public sealed record HostUpdatePullApprovalDto
{
    public required string ApprovalId { get; init; }

    public required HostUpdateApprovalTargetKind TargetKind { get; init; }

    public required HostUpdateApprovalOrigin Origin { get; init; }

    /// <summary>Canonical release identity, <c>vX.Y.Z</c> or <c>vX.Y.Z-insider.N</c>.</summary>
    public required string ReleaseId { get; init; }

    /// <summary>Digest of the signed release manifest, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    public required string ManifestDigest { get; init; }

    /// <summary>Digest of the signed recovery plan; required for <c>SignedRecoveryPlan</c>, otherwise null.</summary>
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

    public required HostUpdateApprovalAuthorizationDto Authorization { get; init; }
}

/// <summary>Redacted authorization evidence for an approval. No actor identifiers or tokens.</summary>
public sealed record HostUpdateApprovalAuthorizationDto
{
    public required long AuthorizationRevision { get; init; }

    public required DateTimeOffset AuthorizedAt { get; init; }

    public required bool Reauthenticated { get; init; }
}

/// <summary>Body of <c>POST approvals/{approvalId}/confirm</c>, sent before the first side effect.</summary>
public sealed record HostUpdateApprovalConfirmationRequestDto
{
    public required string ApprovalId { get; init; }

    public required string ManifestDigest { get; init; }

    public string? PlanDigest { get; init; }

    public required long HostPolicyRevision { get; init; }

    /// <summary>Bounded checkpoint code the daemon is about to leave.</summary>
    public required string Checkpoint { get; init; }
}

/// <summary>Payload of the confirmation response.</summary>
public sealed record HostUpdateApprovalConfirmationDto
{
    public required HostUpdateApprovalConfirmationDecision Decision { get; init; }

    public required string ApprovalId { get; init; }

    public DateTimeOffset? ValidUntil { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>
/// Body of <c>POST status</c>. Every free-form member is a bounded reason code, so credentials,
/// enrollment material and host paths cannot be represented.
/// </summary>
public sealed record HostUpdateDaemonStatusReportDto
{
    public required HostUpdateDaemonState DaemonState { get; init; }

    public required HostUpdateDaemonExecutionMode ExecutionMode { get; init; }

    public required long HostPolicyRevision { get; init; }

    public string? CurrentCheckpoint { get; init; }

    public required HostUpdateDaemonLastResultDto LastResult { get; init; }

    public required IReadOnlyList<string> DeferReasons { get; init; }

    public required IReadOnlyList<string> RecoveryHints { get; init; }

    public required DateTimeOffset ReportedAt { get; init; }
}

/// <summary>Redacted last-result summary.</summary>
public sealed record HostUpdateDaemonLastResultDto
{
    public required HostUpdateDaemonResultOutcome Outcome { get; init; }

    public string? ApprovalId { get; init; }

    public string? ReleaseId { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? ReasonCode { get; init; }
}

/// <summary>Payload acknowledging a status report.</summary>
public sealed record HostUpdateDaemonStatusReportAckDto
{
    public required DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>
/// Operator-facing daemon status (farm_admin only). Combines the enrollment record with the
/// last validated report; contains key fingerprints and bounded codes only.
/// </summary>
public sealed record HostUpdateDaemonOperatorStatusDto
{
    public required bool Enrolled { get; init; }

    public HostUpdateDaemonEnrollmentState? EnrollmentState { get; init; }

    public long? EnrollmentEpoch { get; init; }

    public string? KeyFingerprint { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public HostUpdateDaemonStatusReportDto? LastReport { get; init; }

    public required bool AutomaticUpdatesRuntimeEnabled { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}
