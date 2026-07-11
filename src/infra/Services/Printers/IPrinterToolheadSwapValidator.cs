using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Result of validating a scanned spool against a printer toolhead's expected material.
/// Consumed by <c>GET /api/printers/{id}/toolheads/{i}/swap-validation?spoolId=</c>.
/// </summary>
/// <param name="Status">
/// Three-state outcome (<c>ok</c> / <c>mismatch</c> / <c>unknown</c>). See
/// <see cref="SwapValidationStatus"/>. <c>ok</c> means the scanned material matches or there
/// is no requirement; <c>mismatch</c> means a concrete requirement exists and differs;
/// <c>unknown</c> means validation could not be performed (spool unresolved / no material
/// metadata) and MUST NOT be overridden.
/// </param>
/// <param name="Expected">
/// The expected material for the given toolhead index. Derived from the active job when
/// present, otherwise from the earliest queued job assigned to the printer. Null when no
/// expectation can be resolved.
/// </param>
/// <param name="Scanned">
/// The scanned spool's material (as reported by Spoolman). Null when the spool cannot be
/// resolved.
/// </param>
/// <param name="AffectedJobs">
/// Assigned or active jobs on this printer whose per-tool requirement for the given
/// toolhead index disagrees with the scanned material. Always empty unless
/// <see cref="Status"/> is <see cref="SwapValidationStatus.Mismatch"/>.
/// </param>
/// <param name="Reason">
/// Optional human-readable reason for mismatch or missing data (e.g., "Spoolman not
/// configured", "Spool not found"). Null on the happy path.
/// </param>
public sealed record SwapValidationResultDto(
    [property: JsonConverter(typeof(SwapValidationStatusJsonConverter))]
    SwapValidationStatus Status,
    string? Expected,
    string? Scanned,
    IReadOnlyList<SwapValidationAffectedJobDto> AffectedJobs,
    string? Reason = null);

/// <summary>
/// Minimal projection of a print job impacted by a swap-validation mismatch.
/// </summary>
/// <param name="JobId">The affected job's identifier.</param>
/// <param name="Name">The job's display name for operator context.</param>
/// <param name="Status">Current lifecycle status.</param>
/// <param name="Tool">Toolhead index whose requirement mismatches the scanned spool.</param>
/// <param name="ExpectedMaterial">Expected material for the affected job at that toolhead.</param>
public sealed record SwapValidationAffectedJobDto(
    Guid JobId,
    string Name,
    PrintJobStatus Status,
    int Tool,
    string ExpectedMaterial);

/// <summary>
/// Immutable context describing an authorized guided-swap override, passed from the HTTP
/// layer into <c>IPrintersService.SetToolheadSpoolAsync</c> so the durable audit record is
/// written in the SAME unit of work / transaction as the spool binding (GitHub issue
/// OlyForge3D/PrintFarmer#710, B6). Supplied ONLY for a genuine mismatch that was overridden
/// with an explicit flag + non-empty reason; never for ok/unknown/disabled-gate paths.
/// </summary>
/// <param name="UserId">Authenticated user identity (NameIdentifier). Null when unresolved.</param>
/// <param name="UserName">Display name of the authorizing user, when available.</param>
/// <param name="Reason">Operator-supplied override reason (required, non-empty).</param>
/// <param name="ExpectedMaterial">Expected material at override time, when known.</param>
/// <param name="ScannedMaterial">Scanned spool material at override time, when known.</param>
/// <param name="AffectedJobIds">Print-job ids whose requirement disagreed with the scanned spool.</param>
public sealed record FilamentSwapOverrideContext(
    string? UserId,
    string? UserName,
    string Reason,
    string? ExpectedMaterial,
    string? ScannedMaterial,
    IReadOnlyList<Guid> AffectedJobIds);

/// <summary>
/// Discriminates why a swap validation could not produce a body versus a concrete
/// validation result. Lets the HTTP layer map to the correct status code WITHOUT any
/// brittle string matching and, critically, guarantees an unmaterialized-but-valid MMU
/// gate is validated rather than blindly bound (GitHub issue OlyForge3D/PrintFarmer#710, B2).
/// </summary>
public enum SwapValidationOutcome
{
    /// <summary>A concrete <see cref="SwapValidationResultDto"/> was produced.</summary>
    Validated,

    /// <summary>The printer does not exist. HTTP 404, no write.</summary>
    PrinterNotFound,

    /// <summary>
    /// The requested lane is not a valid filament source on this printer (e.g., a lane that
    /// cannot be resolved to a G-code tool such as the shared MMU hotend at index 0, or a
    /// gate index that this non-MMU printer cannot have). HTTP 404, no write.
    /// </summary>
    ToolheadNotFound,

    /// <summary>
    /// The requested lane index is structurally out of range (negative or beyond the maximum
    /// supported toolhead index). HTTP 400, no write.
    /// </summary>
    ToolheadOutOfRange,
}

/// <summary>
/// Envelope returned by <see cref="IPrinterToolheadSwapValidator.ValidateAsync"/> carrying
/// either a concrete <see cref="SwapValidationResultDto"/> (<see cref="SwapValidationOutcome.Validated"/>)
/// or a not-found / out-of-range discriminator so the controller never falls through to a
/// blind bind.
/// </summary>
/// <param name="Outcome">The high-level outcome discriminator.</param>
/// <param name="Result">
/// The validation body when <paramref name="Outcome"/> is
/// <see cref="SwapValidationOutcome.Validated"/>; otherwise <c>null</c>.
/// </param>
public sealed record SwapValidationResult(
    SwapValidationOutcome Outcome,
    SwapValidationResultDto? Result);

/// <summary>
/// Validates a scanned spool against the expected material for a specific printer toolhead.
/// Backs the guided filament swap flow used by the mobile app and web UI.
/// </summary>
/// <remarks>
/// Operator-feature gate integration (issue OlyForge3D/PrintFarmer#725): the HTTP endpoint
/// that consumes this service is expected to consult <c>IOperatorFeatureGate</c> for the
/// <c>guidedSwapEnabled</c> flag and short-circuit to a <c>404</c> ProblemDetails with
/// <c>code: "featureDisabled"</c> before invoking this validator. The validator itself is
/// gate-agnostic and safe to reuse from any future consumer (e.g., background reconciliation).
/// </remarks>
public interface IPrinterToolheadSwapValidator
{
    /// <summary>
    /// Computes the validation result for scanning <paramref name="spoolId"/> against
    /// <paramref name="toolheadIndex"/> on <paramref name="printerId"/>.
    /// </summary>
    /// <param name="printerId">The printer being serviced.</param>
    /// <param name="toolheadIndex">Zero-based toolhead index (T0, T1, ...).</param>
    /// <param name="spoolId">Spoolman spool identifier being scanned.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="SwapValidationResult"/> whose <see cref="SwapValidationResult.Outcome"/>
    /// distinguishes a concrete validation body from printer-not-found / lane-not-found /
    /// out-of-range so the controller maps codes precisely and never binds without validating.
    /// This method performs NO writes.
    /// </returns>
    Task<SwapValidationResult> ValidateAsync(
        Guid printerId,
        int toolheadIndex,
        int spoolId,
        CancellationToken ct);
}
