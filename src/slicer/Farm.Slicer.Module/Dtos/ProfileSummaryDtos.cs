using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Dtos;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Lightweight projection of <see cref="FilamentProfileDto"/> for list/lookup endpoints
/// (e.g. <c>filament/for-machines</c>) where a calibration client only needs identity and
/// applicability metadata to populate a dropdown. Deliberately omits <c>StartGcode</c>,
/// <c>EndGcode</c>, and the opaque <c>Settings</c> bag, which are only needed once a specific
/// profile has been resolved for slicing (see #2049).
/// </summary>
public class FilamentProfileSummaryDto
{
    public string Name { get; set; } = string.Empty;

    public string Material { get; set; } = "PLA";

    public string? Manufacturer { get; set; }

    public string? Description { get; set; }

    public string? Color { get; set; }

    [JsonPropertyName("compatible_printers")]
    public IList<string> CompatiblePrinters { get; set; } = [];

    public bool Instantiation { get; set; } = true;

    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    // ── Promoted display fields (cheap, non-opaque) ────────────────────────
    public int NozzleTemperature { get; set; } = 210;

    public int BedTemperature { get; set; } = 60;

    public int PrintSpeed { get; set; } = 50;

    /// <summary>
    /// Projects a full <see cref="FilamentProfileDto"/> down to its summary fields, dropping
    /// <see cref="FilamentProfileDto.StartGcode"/>, <see cref="FilamentProfileDto.EndGcode"/>,
    /// and <see cref="FilamentProfileDto.Settings"/>.
    /// </summary>
    public static FilamentProfileSummaryDto FromFull(FilamentProfileDto profile) => new()
    {
        Name = profile.Name,
        Material = profile.Material,
        Manufacturer = profile.Manufacturer,
        Description = profile.Description,
        Color = profile.Color,
        CompatiblePrinters = new List<string>(profile.CompatiblePrinters),
        Instantiation = profile.Instantiation,
        Inherits = profile.Inherits,
        NozzleTemperature = profile.NozzleTemperature,
        BedTemperature = profile.BedTemperature,
        PrintSpeed = profile.PrintSpeed,
    };
}

/// <summary>
/// Lightweight projection of <see cref="ProcessProfileDto"/> for list/lookup endpoints
/// (e.g. <c>process/for-machines</c>) where a calibration client only needs identity and
/// applicability metadata to populate a dropdown. Deliberately omits the opaque
/// <c>Settings</c> bag, which is only needed once a specific profile has been resolved for
/// slicing (see #2049).
/// </summary>
public class ProcessProfileSummaryDto
{
    public string Name { get; set; } = string.Empty;

    public string Quality { get; set; } = "standard";

    public string? Description { get; set; }

    [JsonPropertyName("compatible_printers")]
    public IList<string> CompatiblePrinters { get; set; } = [];

    public bool Instantiation { get; set; } = true;

    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    // ── Promoted display fields (cheap, non-opaque) ────────────────────────
    public double LayerHeight { get; set; } = 0.2;

    public int InfillPercentage { get; set; } = 20;

    public int PrintSpeed { get; set; } = 50;

    public bool Supports { get; set; }

    /// <summary>
    /// Projects a full <see cref="ProcessProfileDto"/> down to its summary fields, dropping
    /// <see cref="ProcessProfileDto.Settings"/>.
    /// </summary>
    public static ProcessProfileSummaryDto FromFull(ProcessProfileDto profile) => new()
    {
        Name = profile.Name,
        Quality = profile.Quality,
        Description = profile.Description,
        CompatiblePrinters = new List<string>(profile.CompatiblePrinters),
        Instantiation = profile.Instantiation,
        Inherits = profile.Inherits,
        LayerHeight = profile.LayerHeight,
        InfillPercentage = profile.InfillPercentage,
        PrintSpeed = profile.PrintSpeed,
        Supports = profile.Supports,
    };
}

#pragma warning restore SA1402
