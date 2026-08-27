using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request to set or clear the active Spoolman spool on a printer.
/// </summary>
public sealed class SetActiveSpoolRequest
{
    /// <summary>
    /// The Spoolman spool ID to activate. Null or omitted to clear the active spool.
    /// </summary>
    [JsonPropertyName("spoolId")]
    public int? SpoolId { get; set; }

    /// <summary>
    /// Set to true when the operator is deliberately loading a spool that does not match the
    /// swap-validation result (e.g., "Load anyway" from the guided flow's mismatch card). The
    /// backend logs the override intent for later audit; the assignment itself proceeds as
    /// normal.
    /// </summary>
    [JsonPropertyName("overrideMismatch")]
    public bool OverrideMismatch { get; set; }

    /// <summary>
    /// Optional operator-supplied reason recorded alongside the override. Limited to 500
    /// characters to prevent unbounded audit-log growth.
    /// </summary>
    [JsonPropertyName("overrideReason")]
    [MaxLength(500)]
    public string? OverrideReason { get; set; }
}
