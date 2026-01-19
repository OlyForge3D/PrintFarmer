namespace Farm.Infrastructure;

/// <summary>
/// Flat slicer profile data sent by the worker service during slicing operations.
/// This represents the profile parameters in a flat structure as understood by the worker.
/// Used internally for worker communication only - not exposed through the public API.
/// </summary>
public class WorkerSlicerProfileDto
{
    /// <summary>
    /// Gets or sets the layer height in millimeters.
    /// </summary>
    public double LayerHeight { get; set; } = 0.2;

    /// <summary>
    /// Gets or sets the infill percentage (0-100).
    /// </summary>
    public int InfillPercentage { get; set; } = 20;

    /// <summary>
    /// Gets or sets the print speed in mm/s.
    /// </summary>
    public int PrintSpeed { get; set; } = 50;

    /// <summary>
    /// Gets or sets the nozzle temperature in °C.
    /// </summary>
    public int NozzleTemperature { get; set; } = 210;

    /// <summary>
    /// Gets or sets the bed temperature in °C.
    /// </summary>
    public int BedTemperature { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether support structures are enabled.
    /// </summary>
    public bool Supports { get; set; }

    /// <summary>
    /// Gets or sets the material type (e.g., PLA, PETG, ABS).
    /// </summary>
    public string Material { get; set; } = "PLA";

    /// <summary>
    /// Gets or sets the quality preset (draft, standard, fine).
    /// </summary>
    public string Quality { get; set; } = "standard";
}
