using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure;
#pragma warning disable SA1649 // File name should match first type name
#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Coverage status for a toolhead slot or an entire printer, considering the
/// currently active print job and any print jobs explicitly assigned to the
/// same printer. This feature uses a local lowercase wire contract rather than
/// the repository-wide enum converter.
/// </summary>
[JsonConverter(typeof(FilamentCoverageStatusJsonConverter))]
public enum FilamentCoverageStatus
{
    /// <summary>
    /// Known remaining filament comfortably covers all known demand
    /// (active job remaining plus assigned queued jobs) with any configured
    /// safety buffer applied.
    /// </summary>
    Covers = 0,

    /// <summary>
    /// Known demand exceeds known remaining filament. The response will
    /// include predicted runout data when the active job supplies enough
    /// telemetry to compute it.
    /// </summary>
    Runout = 1,

    /// <summary>
    /// Coverage cannot be safely determined because critical data is missing
    /// (no Spoolman remaining weight, no per-extruder gcode metadata, or the
    /// gcode is silent about filament usage). The client MUST NOT surface a
    /// runout claim in this state.
    /// </summary>
    Unknown = 2
}

/// <summary>
/// Feature-local JSON converter for Dallas's canonical
/// <c>unknown|covers|runout</c> status vocabulary.
/// </summary>
public sealed class FilamentCoverageStatusJsonConverter : JsonConverter<FilamentCoverageStatus>
{
    public override FilamentCoverageStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Filament coverage status must be a string.");
        }

        return reader.GetString() switch
        {
            "covers" or "Covers" => FilamentCoverageStatus.Covers,
            "runout" or "Runout" or "Insufficient" => FilamentCoverageStatus.Runout,
            "unknown" or "Unknown" => FilamentCoverageStatus.Unknown,
            _ => throw new JsonException("Unknown filament coverage status."),
        };
    }

    public override void Write(Utf8JsonWriter writer, FilamentCoverageStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            FilamentCoverageStatus.Covers => "covers",
            FilamentCoverageStatus.Runout => "runout",
            FilamentCoverageStatus.Unknown => "unknown",
            _ => throw new JsonException("Unknown filament coverage status."),
        });
    }
}

/// <summary>
/// Coverage snapshot for a single toolhead slot on one printer.
/// </summary>
/// <param name="ToolheadIndex">Zero-based toolhead index matching gcode T-commands (T0 = 0).</param>
/// <param name="ToolheadName">Friendly toolhead name (e.g. "Extruder 1").</param>
/// <param name="SpoolId">Spoolman spool ID currently loaded on this toolhead, if any.</param>
/// <param name="Material">Denormalized material (e.g. "PLA"). Null if unknown.</param>
/// <param name="FilamentColor">Denormalized hex color (e.g. "#FF0000"). Null if unknown.</param>
/// <param name="RemainingGrams">Grams remaining on the loaded spool, or null if unavailable/unbound.</param>
/// <param name="CurrentJobRequiredGrams">Total grams the active job requires on this toolhead. Null when the active job's gcode does not expose per-extruder usage.</param>
/// <param name="CurrentJobRemainingGrams">Grams still to consume on the active job for this toolhead, prorated by live progress. Null when demand or progress is unknown; falls back to <see cref="CurrentJobRequiredGrams"/> when progress is unavailable but demand is known.</param>
/// <param name="QueuedRequiredGrams">Grams required across all assigned queued jobs for this toolhead. Zero when there are no assigned queued jobs. Null when at least one assigned queued job has unknown demand.</param>
/// <param name="TotalDemandGrams">Sum of <see cref="CurrentJobRemainingGrams"/> and <see cref="QueuedRequiredGrams"/>. Null when either component is unknown.</param>
/// <param name="Status">Per-slot coverage verdict.</param>
/// <param name="StatusReason">Machine-readable reason code when <see cref="Status"/> is Unknown or Runout. Never localized. Examples: "spoolman-unconfigured", "spool-source-unavailable", "spool-not-found", "no-spool-assigned", "spool-remaining-unknown", "no-gcode-metadata", "no-per-extruder-metadata", "queued-job-metadata-unknown", "material-mismatch", "spool-material-unknown", "toolhead-unavailable", "insufficient-remaining".</param>
/// <param name="PredictedRunoutAt">UTC timestamp at which the active print is projected to exhaust this spool, if the active job is printing, has a known duration, and the spool holds less filament than the remainder of the active job on this toolhead. Null otherwise.</param>
/// <param name="PredictedRunoutLayer">Layer number at which the runout is projected, if the active job's total layer count is known. Null otherwise.</param>
public record ToolheadCoverageDto(
    int ToolheadIndex,
    string ToolheadName,
    int? SpoolId,
    string? Material,
    string? FilamentColor,
    double? RemainingGrams,
    double? CurrentJobRequiredGrams,
    double? CurrentJobRemainingGrams,
    double? QueuedRequiredGrams,
    double? TotalDemandGrams,
    FilamentCoverageStatus Status,
    string? StatusReason,
    DateTime? PredictedRunoutAt,
    int? PredictedRunoutLayer);

/// <summary>
/// Coverage snapshot for one printer, summarizing every toolhead slot.
/// </summary>
/// <param name="PrinterId">Printer identifier.</param>
/// <param name="PrinterName">Denormalized printer name for display.</param>
/// <param name="Status">Aggregate coverage verdict across all toolheads. Runout if any slot is Runout; otherwise Unknown if any slot is Unknown; otherwise Covers.</param>
/// <param name="Toolheads">Per-toolhead coverage rows, ordered by <see cref="ToolheadCoverageDto.ToolheadIndex"/>.</param>
/// <param name="ActiveJobId">Identifier of the print job currently active on this printer, if any.</param>
/// <param name="ActiveJobName">Display name of the active job, if any.</param>
/// <param name="ActiveJobProgress">Live progress percentage 0-100 reported by the backend, or null when unavailable.</param>
/// <param name="EarliestPredictedRunoutAt">Earliest predicted runout time across all toolheads, or null when no slot predicts a runout.</param>
/// <param name="AssignedQueuedJobCount">Number of jobs (not including the active job) that are Assigned or Queued and explicitly bound to this printer.</param>
/// <param name="EvaluatedAtUtc">UTC evaluation timestamp; useful for cache validation and UI staleness detection.</param>
public record PrinterFilamentCoverageDto(
    Guid PrinterId,
    string PrinterName,
    FilamentCoverageStatus Status,
    IReadOnlyList<ToolheadCoverageDto> Toolheads,
    Guid? ActiveJobId,
    string? ActiveJobName,
    double? ActiveJobProgress,
    DateTime? EarliestPredictedRunoutAt,
    int AssignedQueuedJobCount,
    DateTime EvaluatedAtUtc);

/// <summary>
/// Fleet-wide coverage response returned by the batch endpoint.
/// </summary>
/// <param name="Printers">Per-printer coverage entries. Order matches the request-time printer listing.</param>
/// <param name="EvaluatedAtUtc">UTC evaluation timestamp for the whole batch.</param>
public record FleetFilamentCoverageDto(
    IReadOnlyList<PrinterFilamentCoverageDto> Printers,
    DateTime EvaluatedAtUtc);

/// <summary>
/// Minimal payload consumed by the attention feed adapter (#707). Deliberately
/// does not import attention DTOs so coverage computation remains independent
/// of the feed's presentation contract.
/// </summary>
/// <param name="PrinterId">Printer whose spool is projected to run out.</param>
/// <param name="PrinterName">Denormalized printer name for display.</param>
/// <param name="ToolheadIndex">Toolhead index whose spool triggers the warning.</param>
/// <param name="SpoolId">Spoolman spool ID whose remaining filament is at risk.</param>
/// <param name="Material">Loaded material (for display / grouping).</param>
/// <param name="RemainingGrams">Grams currently remaining on the affected spool.</param>
/// <param name="PredictedRunoutAt">UTC timestamp the runout is projected to occur.</param>
/// <param name="Reason">Machine-readable reason: "runout-during-active-job" or "insufficient-for-assigned-queue".</param>
public record FilamentRunoutWarningDto(
    Guid PrinterId,
    string PrinterName,
    int ToolheadIndex,
    int? SpoolId,
    string? Material,
    double? RemainingGrams,
    DateTime? PredictedRunoutAt,
    string Reason);

#pragma warning restore SA1649
#pragma warning restore SA1402
