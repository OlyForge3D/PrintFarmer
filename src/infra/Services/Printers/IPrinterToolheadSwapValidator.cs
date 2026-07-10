using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Result of validating a scanned spool against a printer toolhead's expected material.
/// Consumed by <c>GET /api/printers/{id}/toolheads/{i}/swap-validation?spoolId=</c>.
/// </summary>
/// <param name="Ok">
/// True when the scanned spool's material matches the expected requirement (or when no
/// requirement is present because the printer has neither an active nor an assigned job).
/// False when a requirement exists and the material does not match.
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
/// toolhead index disagrees with the scanned material. Always empty when
/// <see cref="Ok"/> is true.
/// </param>
/// <param name="Reason">
/// Optional human-readable reason for mismatch or missing data (e.g., "Spoolman not
/// configured", "Spool not found"). Null on the happy path.
/// </param>
public sealed record SwapValidationResultDto(
    bool Ok,
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
    /// A <see cref="SwapValidationResultDto"/> when the printer and toolhead exist;
    /// <c>null</c> when the printer is not found or the toolhead index is invalid so the
    /// controller can map to <c>404 Not Found</c>.
    /// </returns>
    Task<SwapValidationResultDto?> ValidateAsync(
        Guid printerId,
        int toolheadIndex,
        int spoolId,
        CancellationToken ct);
}
