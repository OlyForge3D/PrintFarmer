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
}
