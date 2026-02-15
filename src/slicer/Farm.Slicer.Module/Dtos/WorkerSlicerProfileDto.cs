namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Flat slicer profile data sent by the worker service during slicing operations.
/// </summary>
public class WorkerSlicerProfileDto
{
    public double LayerHeight { get; set; } = 0.2;

    public int InfillPercentage { get; set; } = 20;

    public int PrintSpeed { get; set; } = 50;

    public int NozzleTemperature { get; set; } = 210;

    public int BedTemperature { get; set; } = 60;

    public bool Supports { get; set; }

    public string Material { get; set; } = "PLA";

    public string Quality { get; set; } = "standard";
}
