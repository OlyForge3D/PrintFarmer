using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Durable audit record of an operator deliberately overriding a guided filament-swap
/// material mismatch (GitHub issue OlyForge3D/PrintFarmer#710, B6).
/// </summary>
/// <remarks>
/// <para>
/// A row is written ONLY when a genuine <c>mismatch</c> swap validation is overridden by an
/// authorized operator (explicit override flag + non-empty reason) AND the resulting spool
/// binding succeeds. The audit insert shares the binding's unit of work so the two commit
/// atomically: a failed bind, an unknown/ok validation, an invalid override, or a disabled
/// gate never leave an audit record behind.
/// </para>
/// <para>
/// This is a write-once forensic record; it is not a foreign-key graph. Affected job ids and
/// the expected/scanned material are captured verbatim so the audit remains meaningful even if
/// the referenced jobs or spools later change or are deleted.
/// </para>
/// </remarks>
public class FilamentSwapOverride
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Printer whose toolhead was bound under override.</summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Zero-based toolhead / MMU-gate index that was bound (T0 = physical hotend, T1..N = gates).
    /// </summary>
    public int ToolheadIndex { get; set; }

    /// <summary>Spoolman spool id that was loaded despite the mismatch.</summary>
    public int SpoolId { get; set; }

    /// <summary>
    /// Authenticated user identity (NameIdentifier claim) that authorized the override.
    /// Null only when the identity could not be resolved from the request principal.
    /// </summary>
    [MaxLength(256)]
    public string? UserId { get; set; }

    /// <summary>Display name of the authorizing user, when available.</summary>
    [MaxLength(256)]
    public string? UserName { get; set; }

    /// <summary>Operator-supplied reason for the override (required, non-empty).</summary>
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Expected material for the toolhead at override time, when known.</summary>
    [MaxLength(128)]
    public string? ExpectedMaterial { get; set; }

    /// <summary>Scanned spool material at override time, when known.</summary>
    [MaxLength(128)]
    public string? ScannedMaterial { get; set; }

    /// <summary>
    /// JSON array of the print-job ids whose requirement disagreed with the scanned spool at
    /// override time (from the validation result's affected jobs). Empty array when none.
    /// </summary>
    public string AffectedJobIdsJson { get; set; } = "[]";

    /// <summary>UTC instant the override was recorded.</summary>
    public DateTime CreatedAtUtc { get; set; }
}
