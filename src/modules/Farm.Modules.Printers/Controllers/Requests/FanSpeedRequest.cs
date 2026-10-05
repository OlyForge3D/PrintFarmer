using System.ComponentModel.DataAnnotations;

namespace Farm.Modules.Printers.Controllers.Requests;

/// <summary>Request to set the part-cooling fan speed.</summary>
public sealed class FanSpeedRequest
{
    /// <summary>Requested fan speed, from 0 (off) to 100 (full speed).</summary>
    [Required]
    [Range(0, 100)]
    public int? SpeedPercent { get; set; }
}
