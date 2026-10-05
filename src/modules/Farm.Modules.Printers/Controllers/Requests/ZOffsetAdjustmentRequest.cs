using System.ComponentModel.DataAnnotations;

namespace Farm.Modules.Printers.Controllers.Requests;

/// <summary>Request for one bounded transient Z-offset adjustment during an active print.</summary>
public sealed class ZOffsetAdjustmentRequest
{
    /// <summary>Signed relative adjustment in millimeters. A single request is limited to 0.2 mm.</summary>
    [Required]
    [Range(typeof(decimal), "-0.2", "0.2")]
    public decimal? OffsetMm { get; set; }
}
