using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request for file operations (print, delete, etc.)
/// Uses body parameter to handle filenames with special characters
/// </summary>
public sealed class FileOperationRequest
{
    [JsonPropertyName("fileName")]
    [Required(ErrorMessage = "fileName is required")]
    [MinLength(1, ErrorMessage = "fileName cannot be empty")]
    public string FileName { get; set; } = string.Empty;
}
