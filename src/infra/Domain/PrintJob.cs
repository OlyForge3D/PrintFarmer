using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Domain;

// Job Queue System
public class PrintJob
{
    public Guid Id { get; set; }

    /// <summary>
    /// Optimistic concurrency token for EF Core.
    /// Critical for job queue operations where multiple processes may claim jobs.
    /// </summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>Provider-independent logical revision incremented on every mutation.</summary>
    public long Revision { get; set; } = 1;

    public string Name { get; set; } = string.Empty; // Display name for the job

    /// <summary>
    /// The G-code file for this job. Nullable for history-seeded jobs where the
    /// original file may not exist in PrintFarmer's library.
    /// </summary>
    public Guid? GcodeFileId { get; set; }

    public GcodeFile? GcodeFile { get; set; }

    public Guid? AssignedPrinterId { get; set; }

    public Printer? AssignedPrinter { get; set; }

    public PrintJobStatus Status { get; set; }

    public int Priority { get; set; } = (int)PrintJobPriority.Normal; // Higher = more important

    public int QueuePosition { get; set; }

    public decimal? RequiredNozzleDiameter { get; set; }

    public string? RequiredMaterialType { get; set; }

    /// <summary>
    /// JSON-serialized array of per-tool material requirements extracted from slicer / G-code
    /// metadata at queue time. Each element is a <see cref="PrintJobToolMaterialRequirement"/>
    /// with <c>tool</c>, nullable <c>materialType</c>, optional <c>colorHint</c>, and optional
    /// <c>estimatedGrams</c>. Entry presence means the slicer reported that tool as used; a null
    /// material preserves the distinction between used-but-unresolved and unused. The column is
    /// null when the source G-code lacks authoritative per-extruder usage metadata; in that case
    /// validation falls back to <see cref="RequiredMaterialType"/>.
    /// </summary>
    public string? RequiredMaterialsPerToolJson { get; set; }

    /// <summary>
    /// Typed accessor for <see cref="RequiredMaterialsPerToolJson"/>. Setting a non-null value
    /// serializes the list; setting null clears the column. Not mapped by EF Core.
    /// </summary>
    [NotMapped]
    public IReadOnlyList<PrintJobToolMaterialRequirement>? RequiredMaterialsPerTool
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RequiredMaterialsPerToolJson))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<List<PrintJobToolMaterialRequirement>>(
                    RequiredMaterialsPerToolJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        set
        {
            RequiredMaterialsPerToolJson = value is null
                ? null
                : JsonSerializer.Serialize(value);
        }
    }

    public string[]? RequiredCapabilities { get; set; } // JSON array of required capabilities

    public TimeSpan? EstimatedPrintTime { get; set; }

    public double? EstimatedFilamentUsage { get; set; }

    public DateTime? ActualStartTime { get; set; }

    public DateTime? ActualEndTime { get; set; }

    public TimeSpan? ActualPrintTime { get; set; }

    public double? ActualFilamentUsage { get; set; }

    /// <summary>
    /// Estimated cost of the print job in the user's currency, calculated from
    /// spool price and estimated filament usage. Populated at queue time if
    /// Spoolman spool data is available.
    /// </summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>
    /// Actual cost of the print job in the user's currency, calculated from
    /// spool price and actual filament usage. Populated on job completion.
    /// </summary>
    public decimal? ActualCost { get; set; }

    /// <summary>
    /// Material cost in USD (filament usage × price per gram). Calculated on job completion.
    /// </summary>
    public decimal? MaterialCostUsd { get; set; }

    /// <summary>
    /// Kilowatt-hours consumed during this print job, as measured by a power monitor.
    /// When set, energy cost is computed directly as KwhUsed × ElectricityRatePerKwh.
    /// When null, energy cost falls back to an estimate: (ActualPrintTime × Wattage / 1000) × rate.
    /// </summary>
    public decimal? KwhUsed { get; set; }

    /// <summary>
    /// Energy cost in USD (KwhUsed × electricity rate, or estimated from wattage × duration). Calculated on job completion.
    /// </summary>
    public decimal? EnergyCostUsd { get; set; }

    /// <summary>
    /// Machine time cost in USD (print duration × machine hourly rate). Calculated on job completion.
    /// </summary>
    public decimal? MachineTimeCostUsd { get; set; }

    /// <summary>
    /// Labor cost in USD (subtotal × labor markup percent). Calculated on job completion.
    /// </summary>
    public decimal? LaborCostUsd { get; set; }

    /// <summary>
    /// Total cost in USD (material + energy + machine time + labor). Calculated on job completion.
    /// </summary>
    public decimal? TotalCostUsd { get; set; }

    /// <summary>
    /// UTC timestamp when cost was calculated for this job.
    /// </summary>
    public DateTime? CostCalculatedAt { get; set; }

    public string? FailureReason { get; set; }

    public Guid[]? PreferredPrinterIds { get; set; } // JSON array of preferred printer IDs

    public Guid[]? ExcludedPrinterIds { get; set; } // JSON array of excluded printer IDs

    public string? Notes { get; set; } // Job notes/comments (max 500 characters)

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime QueuedAt { get; set; }

    /// <summary>
    /// Optional UTC deadline for this queued job.
    /// </summary>
    public DateTime? DeadlineAtUtc { get; set; }

    // History Seeding: Track external job source for deduplication

    /// <summary>
    /// External job ID from the printer backend (e.g., Moonraker's JobId).
    /// Used for deduplication when seeding history from printers.
    /// </summary>
    public string? ExternalJobId { get; set; }

    /// <summary>
    /// The printer that originally reported this job during history seeding.
    /// Combined with ExternalJobId forms a unique composite key for deduplication.
    /// </summary>
    public Guid? SourcePrinterId { get; set; }

    /// <summary>
    /// Flag indicating this job was seeded from printer history rather than
    /// created through PrintFarmer's job queue.
    /// </summary>
    public bool WasSeededFromHistory { get; set; }

    /// <summary>
    /// Flag indicating this job was created automatically when the polling service
    /// detected a print started externally (e.g., via OrcaSlicer "Upload and Print"
    /// directly to the printer). External jobs are passive tracking records — they
    /// do not trigger auto-dispatch or queue logic.
    /// </summary>
    public bool IsExternalPrint { get; set; }

    // Multi-copy support: track how many copies of this model to print

    /// <summary>
    /// Total number of copies to print for this job. Defaults to 1 for single-copy jobs.
    /// When queued from a project, this equals the file's remaining prints.
    /// Can be overridden from the job queue UI.
    /// </summary>
    public int Copies { get; set; } = 1;

    /// <summary>
    /// Number of copies that have been successfully printed so far.
    /// Incremented when a print completes; not decremented on cancel/abort.
    /// </summary>
    public int CompletedCopies { get; set; }

    /// <summary>
    /// Number of copies still remaining to be printed.
    /// </summary>
    [NotMapped]
    public int RemainingCopies => Math.Max(0, Copies - CompletedCopies);

    /// <summary>
    /// Whether this job requires printing multiple copies.
    /// </summary>
    [NotMapped]
    public bool IsMultiCopy => Copies > 1;

    /// <summary>
    /// Link back to the specific project file this job was created from.
    /// Used to auto-increment the project file's PrintedCount on copy completion.
    /// </summary>
    public Guid? ProjectFileId { get; set; }

    // Project tracking: link job to its source project and filament assignment

    /// <summary>
    /// ID of the project this job was queued from (if any).
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// Denormalized project name for display without a join.
    /// </summary>
    [MaxLength(255)]
    public string? ProjectName { get; set; }

    /// <summary>
    /// Spoolman filament ID assigned via the project file (if any).
    /// </summary>
    public int? SpoolmanFilamentId { get; set; }

    /// <summary>
    /// Spoolman spool ID (physical spool instance) used for this job.
    /// Set on dispatch from the printer's active spool.
    /// </summary>
    public int? SpoolmanSpoolId { get; set; }

    /// <summary>
    /// Denormalized filament display name (e.g., "PolyTerra PLA Charcoal Black").
    /// </summary>
    [MaxLength(255)]
    public string? FilamentName { get; set; }

    /// <summary>
    /// Denormalized filament vendor (e.g., "Polymaker").
    /// </summary>
    [MaxLength(128)]
    public string? FilamentVendor { get; set; }

    /// <summary>
    /// Denormalized filament color hex (e.g., "#1A1A1A").
    /// </summary>
    [MaxLength(32)]
    public string? FilamentColor { get; set; }

    /// <summary>
    /// Optional plate index from a multi-plate 3MF model.
    /// </summary>
    public int? PlateIndex { get; set; }

    /// <summary>
    /// Optional plate name from a multi-plate 3MF model.
    /// </summary>
    [MaxLength(255)]
    public string? PlateName { get; set; }

    // Phase 3C: Timeline tracking
    public ICollection<JobStateHistory> StateHistory { get; } = new List<JobStateHistory>();

    // Phase 4.1: Job Scheduling (one-to-one relationship)
    public JobSchedule? Schedule { get; set; }

    // Phase 4.2: Completion Statistics (one-to-one relationship)
    public PrintJobStatistics? Statistics { get; set; }

    // Dispatch tracking

    /// <summary>
    /// UTC timestamp when this job was dispatched to a printer via the scoring engine.
    /// </summary>
    public DateTime? DispatchedAt { get; set; }

    /// <summary>
    /// Weighted score the assigned printer received from the dispatch scorer.
    /// </summary>
    public double? DispatchScore { get; set; }

    /// <summary>
    /// How the printer was selected: Manual, Suggested (scored), or Auto (future).
    /// Stored as string via JsonStringEnumConverter.
    /// </summary>
    public int? DispatchMode { get; set; }

    // Phase 4.4: Job Retry History

    /// <summary>
    /// Per-toolhead filament usage records for multi-tool/MMU jobs.
    /// Each entry tracks which spool was loaded on a specific toolhead and how much was consumed.
    /// Empty for single-extruder jobs that don't use per-toolhead tracking.
    /// </summary>
    public ICollection<PrintJobToolheadUsage> ToolheadUsages { get; set; } = new List<PrintJobToolheadUsage>();

    /// <summary>
    /// Retry history where THIS job is the original failed job
    /// </summary>
    public ICollection<JobRetry> RetriesAsOriginal { get; } = new List<JobRetry>();

    /// <summary>
    /// Retry history where THIS job is a retry attempt (reference to original in JobRetry.OriginalJobId)
    /// </summary>
    public ICollection<JobRetry> RetriesAsAttempt { get; } = new List<JobRetry>();

    /// <summary>
    /// Tags associated with this print job. Includes both auto-generated tags
    /// (material, color, nozzle) and user-applied manual tags.
    /// </summary>
    public ICollection<Tag> Tags { get; set; } = new List<Tag>();

    // Printed-part harvest metadata (see #714).
    // Harvested jobs remain PrintJobStatus.Completed — harvest is orthogonal to lifecycle.

    /// <summary>
    /// UTC timestamp when this job was harvested into printed-part stock.
    /// A non-null value marks the job as already harvested; subsequent harvest
    /// requests are treated as idempotent replays.
    /// </summary>
    public DateTime? HarvestedAt { get; set; }

    /// <summary>
    /// Unique key used to serialize concurrent/duplicate harvest requests for
    /// this job. Persisted so a retried harvest returns the original response
    /// without creating additional ledger entries.
    /// </summary>
    [MaxLength(128)]
    public string? HarvestOperationKey { get; set; }

    /// <summary>User who initiated the successful harvest, if authenticated.</summary>
    [MaxLength(450)]
    public string? HarvestedByUserId { get; set; }

    /// <summary>
    /// Bin the harvested parts were placed into. Denormalized for read paths;
    /// authoritative per-adjustment bins live on <see cref="PartInventoryAdjustment"/>.
    /// </summary>
    public Guid? HarvestedIntoBinId { get; set; }

    // =========================================================================
    // Calibration dispatch fields (issue #900)
    // All fields below are additive and nullable so existing Standard jobs are
    // fully backward-compatible. Calibration fields are immutable after creation.
    // =========================================================================

    /// <summary>
    /// Classifies the job as Standard or FilamentCalibration.
    /// Null for rows created before issue #900 (backfill as Standard).
    /// </summary>
    public JobKind? JobKind { get; set; }

    // --- Calibration origin links (soft references — no FK constraint) ---

    /// <summary>Calibration project that owns this job.</summary>
    public Guid? CalibrationProjectId { get; set; }

    /// <summary>Calibration attempt that produced the G-code for this job.</summary>
    public Guid? CalibrationAttemptId { get; set; }

    /// <summary>Immutable printer-configuration snapshot used when the job was created.</summary>
    public Guid? CalibrationConfigSnapshotId { get; set; }

    /// <summary>Calibration orchestration that requested this job.</summary>
    public Guid? CalibrationOrchestrationId { get; set; }

    // --- Provenance ---

    /// <summary>Artifact (slicer output) whose bytes were promoted into <see cref="GcodeFile"/>.</summary>
    public Guid? SourceArtifactId { get; set; }

    /// <summary>
    /// Slice job that produced the promoted artifact for this print job.
    /// Part of the immutable provenance chain and a canonical idempotency-hash input:
    /// re-slicing produces a new slice job, therefore a new canonical hash.
    /// </summary>
    public Guid? SliceJobId { get; set; }

    /// <summary>SHA-256 (hex) of the promoted G-code content as verified at promotion time.</summary>
    [MaxLength(64)]
    public string? GcodeContentSha256 { get; set; }

    /// <summary>Exact promoted G-code byte count pinned when the job was created.</summary>
    public long? PinnedGcodeFileSizeBytes { get; set; }

    /// <summary>Subject (user or system) that created this job.</summary>
    [MaxLength(256)]
    public string? CreatorSubject { get; set; }

    // --- Idempotency ---

    /// <summary>
    /// Caller-supplied scope that qualifies <see cref="IdempotencyKey"/> uniqueness
    /// (typically the calibration project ID or user subject for cross-project isolation).
    /// </summary>
    [MaxLength(256)]
    public string? IdempotencyScope { get; set; }

    /// <summary>
    /// Caller-supplied stable key. Combined with <see cref="IdempotencyScope"/>, a filtered
    /// unique index prevents duplicate active jobs on concurrent identical requests.
    /// </summary>
    [MaxLength(512)]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the canonical serialized request payload. A second call with the
    /// same key but a different hash is a 409 idempotency_payload_mismatch.
    /// </summary>
    [MaxLength(64)]
    public string? IdempotencyRequestSha256 { get; set; }

    // --- Explicit firmware/dialect/slicer compatibility tuple (immutable) ---

    /// <summary>Required firmware family (e.g., <c>Klipper</c>). Null for Standard jobs.</summary>
    public PrinterFirmwareFamily? RequiredFirmwareFamily { get; set; }

    /// <summary>Required G-code dialect (e.g., <c>Klipper</c>). Null for Standard jobs.</summary>
    public PrinterGcodeDialect? RequiredGcodeDialect { get; set; }

    /// <summary>Required slicer engine name (e.g., <c>OrcaSlicer</c>). Null for Standard jobs.</summary>
    [MaxLength(128)]
    public string? RequiredSlicerEngine { get; set; }

    /// <summary>Required slicer distribution (e.g., <c>upstream</c>). Null for Standard jobs.</summary>
    [MaxLength(128)]
    public string? RequiredSlicerDistribution { get; set; }

    /// <summary>Pinned slicer version required by this job.</summary>
    [MaxLength(64)]
    public string? RequiredSlicerVersion { get; set; }

    /// <summary>Pinned slicer container OCI digest required by this job.</summary>
    [MaxLength(128)]
    public string? RequiredSlicerContainerDigest { get; set; }

    // --- Content hashes (immutable) ---

    /// <summary>SHA-256 of the canonical calibration specification.</summary>
    [MaxLength(64)]
    public string? SpecificationSha256 { get; set; }

    /// <summary>SHA-256 of the effective machine (printer) slicer profile.</summary>
    [MaxLength(64)]
    public string? MachineProfileSha256 { get; set; }

    /// <summary>SHA-256 of the effective process (print-settings) slicer profile.</summary>
    [MaxLength(64)]
    public string? ProcessProfileSha256 { get; set; }

    /// <summary>SHA-256 of the effective filament slicer profile.</summary>
    [MaxLength(64)]
    public string? FilamentProfileSha256 { get; set; }

    /// <summary>SHA-256 of the full printer-configuration snapshot used at job creation.</summary>
    [MaxLength(64)]
    public string? PrinterConfigSnapshotSha256 { get; set; }

    /// <summary>
    /// Printer configuration revision current at job-creation time.
    /// Dispatch rejects the job if this value no longer matches the printer's revision.
    /// </summary>
    public long? PinnedPrinterConfigRevision { get; set; }

    /// <summary>Exact printer model pinned from the assigned printer.</summary>
    public Guid? PinnedPrinterModelId { get; set; }

    /// <summary>Exact physical toolhead selected by the calibration project.</summary>
    public Guid? PinnedToolheadId { get; set; }

    /// <summary>Zero-based physical toolhead index pinned with <see cref="PinnedToolheadId"/>.</summary>
    public int? PinnedToolheadIndex { get; set; }

    /// <summary>Exact local physical spool pinned by the calibration project.</summary>
    public Guid? PinnedSpoolId { get; set; }

    /// <summary>Filament SKU pinned from the persisted calibration project.</summary>
    [MaxLength(256)]
    public string? PinnedFilamentSku { get; set; }

    /// <summary>Physical production lot pinned from the selected spool at creation.</summary>
    [MaxLength(256)]
    public string? PinnedFilamentLotNumber { get; set; }

    /// <summary>
    /// Unique nullable key used only while an externally observed print is active.
    /// Setting this to the printer ID provides provider-independent unique protection
    /// against concurrent observers creating duplicate active external jobs.
    /// </summary>
    public Guid? ActiveExternalPrinterId { get; set; }

    /// <summary>SHA-256 of the persisted filament snapshot used for this job.</summary>
    [MaxLength(64)]
    public string? FilamentSnapshotSha256 { get; set; }

    /// <summary>SHA-256 of the immutable source model consumed by the slicer.</summary>
    [MaxLength(64)]
    public string? SourceModelSha256 { get; set; }

    /// <summary>SHA-256 of the promoted calibration manifest.</summary>
    [MaxLength(64)]
    public string? CalibrationManifestSha256 { get; set; }

    /// <summary>Object X extent pinned from G-code metadata at queue creation.</summary>
    public double? PinnedObjectDimensionX { get; set; }

    /// <summary>Object Y extent pinned from G-code metadata at queue creation.</summary>
    public double? PinnedObjectDimensionY { get; set; }

    /// <summary>Object Z extent pinned from G-code metadata at queue creation.</summary>
    public double? PinnedObjectDimensionZ { get; set; }

    // --- Blocked-dispatch state ---

    /// <summary>
    /// Typed code explaining why the job cannot currently be dispatched without
    /// consuming its bed-clear acknowledgement (e.g., firmware mismatch).
    /// Null means the job is dispatchable.
    /// </summary>
    public JobBlockedReasonCode? BlockedReasonCode { get; set; }

    /// <summary>
    /// Structured JSON with additional detail for <see cref="BlockedReasonCode"/>.
    /// Contains no credentials or private paths.
    /// </summary>
    public string? BlockedReasonJson { get; set; }

    // --- Dispatch attempt history ---

    /// <summary>Dispatch attempts recorded against this job.</summary>
    public ICollection<QueueDispatchAttempt> DispatchAttempts { get; set; } = new List<QueueDispatchAttempt>();
}
