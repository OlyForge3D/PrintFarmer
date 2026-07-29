using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Legacy request body retained so the retired raw G-code route can return a typed
/// <c>410 Gone</c> response. New callers must use bounded printer-control endpoints.
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
