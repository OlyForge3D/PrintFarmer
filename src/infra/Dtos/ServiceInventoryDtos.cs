using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Dtos;

/// <summary>Read-only inventory ObservationState values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryObservationState
{
    Observed,
    Stale,
    Unavailable,
    Unknown,
    NotInstalled,
}

/// <summary>Read-only inventory CompatibilityState values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryCompatibilityState
{
    Compatible,
    Incompatible,
    Unknown,
    MixedRelease,
    MixedChannel,
}

/// <summary>Read-only inventory ChannelState values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryChannelState
{
    Observed,
    Stale,
    Unknown,
    Mismatch,
    Mixed,
}

/// <summary>Read-only inventory Eligibility values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryEligibility
{
    Blocked,
    Eligible,
    Unknown,
    NotManaged,
}

/// <summary>Consumer vocabulary owned by #2668. Values are copied, never allocated or derived by inventory.</summary>
public sealed record CanonicalReleaseIdentityDto
{
    /// <summary>Immutable canonical version, not a configured image alias.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? CanonicalVersion { get; init; }

    /// <summary>Authored release base from the shared record.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BaseVersion { get; init; }

    /// <summary>Canonical channel; not installation enrollment.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Channel { get; init; }

    /// <summary>Immutable release identifier.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReleaseId { get; init; }

    /// <summary>Historical authorized source tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SourceTag { get; init; }

    /// <summary>Branch at authorization, not current ancestry.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SourceBranch { get; init; }

    /// <summary>Full authorized source commit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SourceCommit { get; init; }

    /// <summary>Exact branch head at authorization.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AuthorizedBranchHead { get; init; }

    /// <summary>Build identity from the release authority.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BuildId { get; init; }

    /// <summary>Build attempt from the release authority.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BuildAttempt { get; init; }

    /// <summary>Authoritative workflow identity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? WorkflowIdentity { get; init; }

    /// <summary>Durable allocation evidence identifier.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AllocationIdentity { get; init; }

    /// <summary>Verified promotion lineage when supplied by the authority.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public PromotionOriginDto? PromotionOrigin { get; init; }
}

/// <summary>Historical promotion origin; inventory never infers promotion from matching versions.</summary>
public sealed record PromotionOriginDto
{
    /// <summary>Original insider release.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReleaseId { get; init; }

    /// <summary>Original canonical version.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? CanonicalVersion { get; init; }

    /// <summary>Full original source commit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SourceCommit { get; init; }

    /// <summary>Original signed manifest digest.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ManifestDigest { get; init; }

    /// <summary>Opaque qualification evidence identifier, never a host path.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Evidence { get; init; }
}

/// <summary>One replica or an explicitly unobserved topology slot. Self-report is not running digest attestation.</summary>
public sealed record ServiceReplicaObservationDto
{
    /// <summary>Logical service identity.</summary>
    public string ServiceId { get; init; } = string.Empty;

    /// <summary>Opaque replica identity; null means no replica was observed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? InstanceId { get; init; }

    /// <summary>Application component, not hostname or endpoint.</summary>
    public string Component { get; init; } = string.Empty;

    /// <summary>Whether this topology slot is expected.</summary>
    public bool Required { get; init; }

    /// <summary>Observed application build, separate from engine and canonical installed version.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ApplicationVersion { get; init; }

    /// <summary>Full build commit when reported.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SourceCommit { get; init; }

    /// <summary>Slicer engine version, not worker application version.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? EngineVersion { get; init; }

    /// <summary>Normalized provider for the database used by this component, when independently observed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DatabaseProvider { get; init; }

    /// <summary>Applied migration head for the component's database context, never a target schema claim.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? MigrationHead { get; init; }

    /// <summary>Freshness or absence of the observation.</summary>
    public InventoryObservationState ObservationState { get; init; } = InventoryObservationState.Unknown;

    /// <summary>Original observation time; never refreshed when reading an import.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>Last successful source observation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>Observation source and trust boundary.</summary>
    public string Source { get; init; } = "None";

    /// <summary>Safe machine-readable explanation; no raw errors.</summary>
    public string ReasonCode { get; init; } = "NoObservation";

    /// <summary>Shared canonical record when independently verified by an inventory source.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CanonicalReleaseIdentityDto? Identity { get; init; }

    /// <summary>Trusted local verifier identity, not a self-reported verified flag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? VerificationSource { get; init; }

    /// <summary>Original verification timestamp.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>Observed operating system and architecture.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Platform { get; init; }

    /// <summary>Actual running platform digest; never substituted with index digest.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PlatformDigest { get; init; }

    /// <summary>Multi-platform image index digest, if known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? IndexDigest { get; init; }

    /// <summary>Release manifest digest, distinct from image digests.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ManifestDigest { get; init; }

    /// <summary>Secondary configured reference only; never installed identity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ConfiguredImage { get; init; }

    /// <summary>Evidence-based nullable channel, independent of selected policy.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ObservedChannel { get; init; }

    /// <summary>Per-replica channel evidence state.</summary>
    public InventoryChannelState ChannelState { get; init; } = InventoryChannelState.Unknown;

    /// <summary>Compatibility independent of freshness.</summary>
    public InventoryCompatibilityState CompatibilityState { get; init; } = InventoryCompatibilityState.Unknown;

    /// <summary>Safe explanations for the compatibility decision.</summary>
    public IReadOnlyList<string> CompatibilityReasons { get; init; } = [];
}

/// <summary>Admin-only read-only inventory. This contract does not authorize or perform updates.</summary>
public sealed record ServiceInventoryDto
{
    /// <summary>Local configured selection; defaults to stable, never inferred from builds.</summary>
    public string SelectedChannel { get; init; } = "stable";

    /// <summary>Default or explicit host configuration.</summary>
    public string SelectionSource { get; init; } = "Default";

    /// <summary>Read-model collection time, not observation or verification time.</summary>
    public DateTimeOffset CollectedAt { get; init; }

    /// <summary>Single evidenced channel, or null for unknown/mixed sets.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ObservedChannel { get; init; }

    /// <summary>No target discovery in this increment.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? TargetChannel { get; init; }

    /// <summary>Set-wide evidence, mismatch and freshness state.</summary>
    public InventoryChannelState ChannelState { get; init; } = InventoryChannelState.Unknown;

    /// <summary>Set compatibility independently of freshness and eligibility.</summary>
    public InventoryCompatibilityState CompatibilityState { get; init; } = InventoryCompatibilityState.Unknown;

    /// <summary>Safe compatibility reason codes.</summary>
    public IReadOnlyList<string> CompatibilityReasons { get; init; } = [];

    /// <summary>Read-only inventory does not establish managed eligibility unless an evaluator supplies it.</summary>
    public InventoryEligibility Eligibility { get; init; } = InventoryEligibility.Blocked;

    /// <summary>No implicit check, enrollment, execution, or eligibility authorization.</summary>
    public IReadOnlyList<string> EligibilityReasons { get; init; } = ["EligibilityNotEvaluated", "ReadOnlyInventory"];

    /// <summary>Read-only installation lifecycle derived from supplied signed release evidence.</summary>
    public ReleaseReadinessDto? Readiness { get; init; }

    /// <summary>Whether the snapshot was collected locally or imported for offline inspection.</summary>
    public InventorySnapshotOrigin SnapshotOrigin { get; init; } = InventorySnapshotOrigin.Live;

    /// <summary>Original source of an imported snapshot; null for local observations.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SnapshotSource { get; init; }

    /// <summary>Original export time for an imported snapshot; never replaced by import time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? SnapshotExportedAt { get; init; }

    /// <summary>All reported replicas, including absent optional topology slots.</summary>
    public IReadOnlyList<ServiceReplicaObservationDto> Services { get; init; } = [];
}

/// <summary>Distinguishes live host observations from offline imported evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventorySnapshotOrigin
{
    Live,
    Imported,
}

/// <summary>Portable inventory envelope used to export and classify offline snapshots.</summary>
public sealed record InstallationInventorySnapshotDto
{
    /// <summary>Envelope format version.</summary>
    public int FormatVersion { get; init; } = 1;

    /// <summary>Snapshots are always imported evidence when read from this envelope.</summary>
    public InventorySnapshotOrigin SnapshotOrigin { get; init; } = InventorySnapshotOrigin.Imported;

    /// <summary>Original export source.</summary>
    public string SnapshotSource { get; init; } = string.Empty;

    /// <summary>Original export timestamp.</summary>
    public DateTimeOffset SnapshotExportedAt { get; init; }

    /// <summary>Inventory evidence captured at export time.</summary>
    public required ServiceInventoryDto Inventory { get; init; }

    /// <summary>Creates the portable envelope from a locally collected inventory snapshot.</summary>
    public static InstallationInventorySnapshotDto FromLiveInventory(
        ServiceInventoryDto inventory,
        string snapshotSource,
        DateTimeOffset snapshotExportedAt) =>
        new()
        {
            SnapshotSource = snapshotSource,
            SnapshotExportedAt = snapshotExportedAt,
            Inventory = inventory,
        };

    /// <summary>Classifies deserialized evidence as imported without altering its observation provenance.</summary>
    public ServiceInventoryDto ToImportedInventory()
    {
        if (FormatVersion != 1 || SnapshotOrigin != InventorySnapshotOrigin.Imported)
        {
            throw new InvalidOperationException("The installation inventory snapshot format is not supported.");
        }

        return Inventory with
        {
            SnapshotOrigin = InventorySnapshotOrigin.Imported,
            SnapshotSource = SnapshotSource,
            SnapshotExportedAt = SnapshotExportedAt,
        };
    }
}

/// <summary>Complete independently verified release evidence consumed by the readiness evaluator.</summary>
public sealed record VerifiedReleaseEvidenceDto
{
    /// <summary>Whether the complete coordinated release set has a valid signature.</summary>
    public bool SignatureVerified { get; init; }

    /// <summary>Whether all required services have immutable target entries.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Canonical target identity authenticated by the signed release manifest.</summary>
    public CanonicalReleaseIdentityDto? Identity { get; init; }

    /// <summary>Immutable release manifest digest.</summary>
    public string? ManifestDigest { get; init; }

    /// <summary>Per-service target requirements from the signed release manifest.</summary>
    public IReadOnlyList<ReleaseServiceRequirementDto> Services { get; init; } = [];
}

/// <summary>One immutable target requirement from a signed coordinated release.</summary>
public sealed record ReleaseServiceRequirementDto
{
    /// <summary>Required logical service identifier.</summary>
    public string ServiceId { get; init; } = string.Empty;

    /// <summary>Target operating system and architecture.</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>Target immutable platform digest.</summary>
    public string PlatformDigest { get; init; } = string.Empty;

    /// <summary>Required source migration head for this service's context.</summary>
    public string? RequiredMigrationHead { get; init; }

    /// <summary>Required worker engine version when this is a slicer worker.</summary>
    public string? RequiredEngineVersion { get; init; }
}

/// <summary>Evidence-based installation lifecycle. This read model never performs installation.</summary>
public sealed record ReleaseReadinessDto
{
    /// <summary>Nullable only when no release-readiness assessment was requested.</summary>
    public InventoryEligibility? State { get; init; }

    /// <summary>Safe, machine-readable reasons for the result.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Immutable ordered verification stages completed before the terminal readiness state.</summary>
    public IReadOnlyList<string> Hops { get; init; } = [];
}
