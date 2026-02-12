using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request to send a raw G-code command to a printer.
/// Used for Klipper macros (LOAD_FILAMENT, UNLOAD_FILAMENT) and standard G-code (M600).
/// </summary>
public sealed class GcodeCommandRequest
{
    /// <summary>
    /// The G-code command string to execute (e.g., "LOAD_FILAMENT", "UNLOAD_FILAMENT", "M600").
    /// </summary>
    [JsonPropertyName("command")]
    [Required(ErrorMessage = "command is required")]
    [MinLength(1, ErrorMessage = "command cannot be empty")]
    public string Command { get; set; } = string.Empty;
}
