using System.Text.Json;

namespace Farm.Web.Api.Contracts;

/// <summary>Safe representation of an authoritative calibration project.</summary>
public sealed record CalibrationProjectDto(
    Guid Id,
    string Name,
    string LifecycleStatus,
    string ExperienceMode,
    Guid PrinterId,
    Guid? CurrentPrinterConfigurationSnapshotId,
    Guid? SelectedToolheadId,
    int? SelectedToolheadIndex,
    CalibrationFilamentIdentityDto Filament,
    JsonElement OrderedSteps,
    string? CurrentStep,
    JsonElement CurrentSelections,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? DeletedAtUtc);

/// <summary>Stable catalog identity and immutable spool snapshot retained by a project.</summary>
public sealed record CalibrationFilamentIdentityDto(
    string Provider,
    string ProductId,
    string? Sku,
    string? Vendor,
    string ProductName,
    string Material,
    decimal? Diameter,
    string? Color,
    Guid? FilamentTypeId,
    Guid? SpoolmanFilamentId,
    Guid? LocalSpoolId,
    Guid? SpoolmanSpoolId,
    JsonElement Snapshot);

/// <summary>Request to create a new project from the explicit printer-context contract.</summary>
public sealed class CalibrationProjectCreateRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public Guid PrinterId { get; init; }

    public long PrinterConfigurationRevision { get; init; }

    public Guid? SelectedToolheadId { get; init; }

    public int? SelectedToolheadIndex { get; init; }

    public string FilamentProvider { get; init; } = string.Empty;

    public string FilamentProductId { get; init; } = string.Empty;

    public string? FilamentSku { get; init; }

    public string? FilamentVendor { get; init; }

    public string FilamentProductName { get; init; } = string.Empty;

    public string FilamentMaterial { get; init; } = string.Empty;

    public decimal? FilamentDiameter { get; init; }

    public string? FilamentColor { get; init; }

    public Guid? FilamentTypeId { get; init; }

    public Guid? SpoolmanFilamentId { get; init; }

    public Guid? LocalSpoolId { get; init; }

    public Guid? SpoolmanSpoolId { get; init; }

    public JsonElement FilamentSnapshot { get; init; }

    public JsonElement OrderedSteps { get; init; }

    public string? CurrentStep { get; init; }

    public JsonElement CurrentSelections { get; init; }

    public string ExperienceMode { get; init; } = "Coach";
}

/// <summary>Request to update editable project metadata with an explicit base revision.</summary>
public sealed class CalibrationProjectUpdateRequest
{
    public long? BaseRevision { get; init; }

    public string? Name { get; init; }

    public string? LifecycleStatus { get; init; }

    public string? CurrentStep { get; init; }

    public JsonElement? OrderedSteps { get; init; }

    public JsonElement? CurrentSelections { get; init; }

    public DateTime? CompletedAtUtc { get; init; }
}

/// <summary>Safe conflict representation used instead of any last-write-wins behavior.</summary>
public sealed record CalibrationRevisionConflictDto(
    long CurrentRevision,
    long? SubmittedBaseRevision,
    object? CurrentRepresentation,
    IReadOnlyList<string> ConflictCategories,
    IReadOnlyList<string> ResolutionOptions);

/// <summary>Editable step draft returned only to the owning user or a farm administrator.</summary>
public sealed record CalibrationDraftDto(
    Guid Id,
    Guid ProjectId,
    string StepId,
    string DeviceLineageId,
    string Method,
    JsonElement Values,
    JsonElement Prerequisites,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? DeletedAtUtc);

/// <summary>Request to create or replace an active draft.</summary>
public sealed class CalibrationDraftUpsertRequest
{
    public long? BaseRevision { get; init; }

    public string DeviceLineageId { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public JsonElement Values { get; init; }

    public JsonElement Prerequisites { get; init; }
}

/// <summary>Immutable calibration attempt plan.</summary>
public sealed record CalibrationAttemptDto(
    Guid Id,
    Guid ProjectId,
    long Sequence,
    Guid? ParentAttemptId,
    string CalibrationKind,
    string Method,
    string DefinitionVersion,
    JsonElement Input,
    JsonElement Specification,
    string SpecificationSha256,
    Guid? PrinterConfigurationSnapshotId,
    JsonElement ProfileSnapshotIds,
    JsonElement? ActualSpoolSnapshot,
    string DerivedStatus,
    DateTime CreatedAtUtc);

/// <summary>Request to append an immutable attempt plan.</summary>
public sealed class CalibrationAttemptCreateRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public Guid? ParentAttemptId { get; init; }

    public string CalibrationKind { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string DefinitionVersion { get; init; } = string.Empty;

    public JsonElement Input { get; init; }

    public JsonElement Specification { get; init; }

    public JsonElement ProfileSnapshotIds { get; init; }

    public JsonElement? ActualSpoolSnapshot { get; init; }

    public long PrinterConfigurationRevision { get; init; }
}

/// <summary>Append-only lifecycle event for an attempt.</summary>
public sealed record CalibrationAttemptEventDto(
    Guid Id,
    Guid AttemptId,
    long Sequence,
    string EventType,
    string DerivedStatus,
    Guid? Model3DId,
    Guid? SliceJobId,
    Guid? ArtifactId,
    Guid? GcodeFileId,
    Guid? PrintJobId,
    Guid? CalibrationOrchestrationId,
    string? ErrorCode,
    JsonElement? Error,
    int? RetryNumber,
    string OperationId,
    DateTime OccurredAtUtc);

/// <summary>Request to append an attempt lifecycle event.</summary>
public sealed class CalibrationAttemptEventCreateRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string EventType { get; init; } = string.Empty;

    public Guid? Model3DId { get; init; }

    public Guid? SliceJobId { get; init; }

    public Guid? ArtifactId { get; init; }

    public Guid? GcodeFileId { get; init; }

    public Guid? PrintJobId { get; init; }

    public Guid? CalibrationOrchestrationId { get; init; }

    public string? ErrorCode { get; init; }

    public JsonElement? Error { get; init; }

    public int? RetryNumber { get; init; }

    public DateTime? OccurredAtUtc { get; init; }
}

/// <summary>Append-only measurement or operator conclusion from an attempt.</summary>
public sealed record CalibrationObservationDto(
    Guid Id,
    Guid AttemptId,
    long Sequence,
    string ObservationType,
    JsonElement Measurements,
    JsonElement Result,
    JsonElement Units,
    decimal? Confidence,
    bool RetestRecommended,
    string? Notes,
    Guid? SelectionParentObservationId,
    string? SelectionReason,
    string OperationId,
    DateTime ObservedAtUtc);

/// <summary>Request to append an immutable observation.</summary>
public sealed class CalibrationObservationCreateRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string ObservationType { get; init; } = string.Empty;

    public JsonElement Measurements { get; init; }

    public JsonElement Result { get; init; }

    public JsonElement Units { get; init; }

    public decimal? Confidence { get; init; }

    public bool RetestRecommended { get; init; }

    public string? Notes { get; init; }

    public Guid? SelectionParentObservationId { get; init; }

    public string? SelectionReason { get; init; }

    public DateTime? ObservedAtUtc { get; init; }
}

/// <summary>Safe private-photo metadata. Storage keys and local paths are deliberately absent.</summary>
public sealed record CalibrationPhotoDto(
    Guid Id,
    Guid ProjectId,
    Guid AttemptId,
    string ClientUploadId,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int Width,
    int Height,
    DateTime? CapturedAtUtc,
    string? Caption,
    int SortOrder,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime? DeletedAtUtc,
    DateTime? PurgedAtUtc);

/// <summary>Request to update mutable presentation metadata of a private photo.</summary>
public sealed class CalibrationPhotoUpdateRequest
{
    public long? BaseRevision { get; init; }

    public string? Caption { get; init; }

    public int? SortOrder { get; init; }
}

/// <summary>Immutable upstream OrcaSlicer profile revision retained by the calibration context.</summary>
public sealed record GeneratedProfileRevisionDto(
    Guid Id,
    Guid ProjectId,
    Guid SourceAttemptId,
    Guid? ParentRevisionId,
    long RevisionNumber,
    string ProfileType,
    string SchemaVersion,
    string SlicerEngine,
    string SlicerDistribution,
    string? SlicerVersion,
    string? SlicerContainerDigest,
    string Name,
    JsonElement NormalizedSettings,
    JsonElement ExactProfile,
    string Sha256,
    string SourceProfileFingerprint,
    string GeneratorVersion,
    DateTime CreatedAtUtc);

/// <summary>
/// Records externally generated profile content after validation; it never starts
/// a generator or submits a printer/slicer job.
/// </summary>
public sealed class GeneratedProfileRevisionCreateRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string GenerationRequestId { get; init; } = string.Empty;

    public Guid SourceAttemptId { get; init; }

    public Guid? ParentRevisionId { get; init; }

    public string ProfileType { get; init; } = string.Empty;

    public string SchemaVersion { get; init; } = string.Empty;

    public string SlicerEngine { get; init; } = string.Empty;

    public string SlicerDistribution { get; init; } = string.Empty;

    public string? SlicerVersion { get; init; }

    public string? SlicerContainerDigest { get; init; }

    public string Name { get; init; } = string.Empty;

    public JsonElement NormalizedSettings { get; init; }

    public string ExactProfileJson { get; init; } = string.Empty;

    public string SourceProfileFingerprint { get; init; } = string.Empty;

    public string GeneratorVersion { get; init; } = string.Empty;

    public decimal? FlowRatio { get; init; }

    public decimal? PressureAdvance { get; init; }

    public decimal? PressureAdvanceSmoothTime { get; init; }

    public decimal? RetractionLength { get; init; }

    public decimal? RetractionSpeed { get; init; }

    public decimal? RetractionMinimumTravel { get; init; }

    public decimal? RetractionLiftZ { get; init; }

    public int? NozzleTemperature { get; init; }

    public int? BedTemperature { get; init; }

    public decimal? MaximumVolumetricFlow { get; init; }

    public Guid? SourceMachineProfileId { get; init; }

    public Guid? SourceProcessProfileId { get; init; }

    public Guid? SourceFilamentProfileId { get; init; }
}

/// <summary>Request that appends export or publication history without mutating a profile revision.</summary>
public sealed class GeneratedProfileRevisionOperationRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string? ExportFormat { get; init; }

    public Guid? PublishedProfileId { get; init; }
}

/// <summary>A safe, ordered calibration change visible to the caller's scope.</summary>
public sealed record CalibrationChangeDto(
    long Sequence,
    Guid ProjectId,
    string EntityType,
    Guid EntityId,
    long EntityRevision,
    string ChangeType,
    JsonElement? Tombstone,
    string MutationId,
    DateTime OccurredAtUtc);

/// <summary>Cursor-paginated change feed response.</summary>
public sealed record CalibrationChangesResponse(
    IReadOnlyList<CalibrationChangeDto> Changes,
    string NextCursor,
    bool HasMore,
    DateTime ServerTimeUtc);

/// <summary>One explicit mutation submitted by an offline calibration client.</summary>
public sealed class CalibrationSyncMutationRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string OperationType { get; init; } = string.Empty;

    public Guid? ProjectId { get; init; }

    public long? BaseRevision { get; init; }

    public JsonElement Payload { get; init; }

    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

/// <summary>Per-mutation result preserving conflicts and validation failures in a batch.</summary>
public sealed record CalibrationSyncMutationResultDto(
    string OperationId,
    string Status,
    int StatusCode,
    string? Code,
    JsonElement? Result,
    CalibrationRevisionConflictDto? Conflict);

/// <summary>Request to apply ordered, idempotent client mutations.</summary>
public sealed record CalibrationSyncApplyRequest(IReadOnlyList<CalibrationSyncMutationRequest> Mutations);

/// <summary>Legacy v4 calibration import envelope supporting preview before commit.</summary>
public sealed class LegacyCalibrationImportRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public bool DryRun { get; init; } = true;

    public IReadOnlyList<CalibrationProjectCreateRequest> Projects { get; init; } = [];
}

/// <summary>Result of a validated legacy import preview or commit request.</summary>
public sealed record LegacyCalibrationImportResultDto(
    bool DryRun,
    string SourceSha256,
    IReadOnlyList<string> Mappings,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> RejectedRecords,
    IReadOnlyList<Guid> ProjectIds);
