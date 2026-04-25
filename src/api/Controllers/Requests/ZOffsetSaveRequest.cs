using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request to save the calibrated Z-offset for a printer.
/// </summary>
public sealed class ZOffsetSaveRequest
{
    /// <summary>
    /// The Z-offset value in millimeters. Negative values move the nozzle closer to the bed.
    /// </summary>
    [JsonPropertyName("offsetMm")]
    [Required(ErrorMessage = "offsetMm is required")]
    [Range(-5.0, 5.0, ErrorMessage = "offsetMm must be between -5.0 and 5.0")]
    public decimal OffsetMm { get; set; }

    /// <summary>
    /// Whether to also send save commands to the printer firmware (M500 for Marlin, SAVE_CONFIG for Klipper).
    /// </summary>
    [JsonPropertyName("saveToFirmware")]
    public bool SaveToFirmware { get; set; } = true;
}
