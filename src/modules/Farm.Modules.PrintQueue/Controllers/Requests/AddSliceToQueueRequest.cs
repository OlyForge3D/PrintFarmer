using Farm.Infrastructure;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request body for adding a completed slice job's gcode to the print queue.
/// </summary>
public sealed record AddSliceToQueueRequest
{
    /// <summary>
    /// Optional job priority. Defaults to <see cref="PrintJobPriority.Normal"/> when null.
    /// </summary>
    public PrintJobPriority? Priority { get; init; }

    /// <summary>
    /// Optional Spoolman spool ID to pre-assign filament tracking to the queued job.
    /// When provided, the spool's filament details are resolved and denormalized onto the print job.
    /// Gracefully ignored if Spoolman is unavailable or the spool cannot be found.
    /// </summary>
    public int? SpoolId { get; init; }

    /// <summary>
    /// Number of copies to print. Defaults to 1 when null.
    /// </summary>
    public int? Copies { get; init; }

    /// <summary>
    /// Optional required printer model name or slicer alias (e.g. "COREONEL", "MK4IS").
    /// Overrides the value extracted from the gcode file if provided.
    /// </summary>
    public string? RequiredPrinterModel { get; init; }

    /// <summary>
    /// Optional required filament material type (e.g. "PLA", "PETG").
    /// Overrides the value extracted from the gcode file if provided.
    /// </summary>
    public string? RequiredMaterialType { get; init; }

    /// <summary>
    /// Optional required nozzle diameter in mm (e.g. 0.4).
    /// Overrides the value extracted from the gcode file if provided.
    /// </summary>
    public decimal? RequiredNozzleDiameter { get; init; }
}
