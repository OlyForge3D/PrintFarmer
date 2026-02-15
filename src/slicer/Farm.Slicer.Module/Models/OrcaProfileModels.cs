using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Models;

/// <summary>
/// Preview of an imported OrcaSlicer config bundle containing printer, filament, and process presets.
/// </summary>
public class OrcaBundlePreviewDto
{
    public List<OrcaPrinterPresetDto> Printers { get; set; } = new();

    public List<OrcaFilamentPresetDto> Filaments { get; set; } = new();

    public List<OrcaProcessPresetDto> Processes { get; set; } = new();

    public Dictionary<string, string> Metadata { get; set; } = new();
}

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Printer preset from OrcaSlicer bundle.
/// </summary>
public class OrcaPrinterPresetDto
{
    public string Name { get; set; } = string.Empty;

    public string? InherentFrom { get; set; } // Base profile reference

    public string? PrinterModel { get; set; }

    public string? Manufacturer { get; set; }

    public double BedWidth { get; set; }

    public double BedDepth { get; set; }

    public double MaxZHeight { get; set; }

    public double NozzleDiameter { get; set; } = 0.4;

    public int MaxBedTemperature { get; set; }

    public int MaxHotendTemperature { get; set; }

    public bool HasHeatedBed { get; set; }

    public string? PrinterTechnology { get; set; } // FFF, SLA

    public Dictionary<string, object?> RawParameters { get; set; } = new();
}

/// <summary>
/// Filament preset from OrcaSlicer bundle.
/// </summary>
public class OrcaFilamentPresetDto
{
    public string Name { get; set; } = string.Empty;

    public string? InherentFrom { get; set; }

    public string? FilamentType { get; set; } // PLA, PETG, ABS, etc.

    public int? NozzleTemperature { get; set; }

    public int? BedTemperature { get; set; }

    public string? Manufacturer { get; set; }

    public double? Density { get; set; } // g/cm³

    public double? Cost { get; set; } // per kg

    public string? Color { get; set; }

    public Dictionary<string, object?> RawParameters { get; set; } = new();
}

/// <summary>
/// Process preset (print settings) from OrcaSlicer bundle.
/// </summary>
public class OrcaProcessPresetDto
{
    public string Name { get; set; } = string.Empty;

    public string? InherentFrom { get; set; }

    public double LayerHeight { get; set; } = 0.2;

    public double FirstLayerHeight { get; set; }

    public int InfillPercentage { get; set; } = 20;

    public string? InfillPattern { get; set; }

    public int? PrintSpeed { get; set; } // mm/s

    public int? InfillSpeed { get; set; }

    public int? OuterWallSpeed { get; set; }

    public int? InnerWallSpeed { get; set; }

    public bool EnableSupports { get; set; }

    public string? SupportType { get; set; }

    public int? SupportAngle { get; set; }

    public int Perimeters { get; set; } = 3;

    public int TopLayers { get; set; } = 4;

    public int BottomLayers { get; set; } = 4;

    public string? Quality { get; set; } // Derived from layer height or explicit

    public Dictionary<string, object?> RawParameters { get; set; } = new();
}

/// <summary>
/// Request payload for importing an OrcaSlicer config bundle (multi-profile JSON).
/// </summary>
public class ImportOrcaBundleDto
{
    public string BundleJson { get; set; } = string.Empty; // Raw JSON bundle

    public bool AllowSystemOverride { get; set; }

    public bool SetDefaults { get; set; } // Set imported profiles as defaults

    public bool ImportPrinters { get; set; } = true;

    public bool ImportFilaments { get; set; } = true;

    public bool ImportProcesses { get; set; } = true;
}

/// <summary>
/// Result of importing an OrcaSlicer bundle with summary counts and any errors.
/// </summary>
public class ImportOrcaBundleResultDto
{
    public int PrintersImported { get; set; }

    public int FilamentsImported { get; set; }

    public int ProcessesImported { get; set; }

    public List<string> Warnings { get; set; } = new();

    public List<string> Errors { get; set; } = new();

    public bool Success { get; set; }
}

/// <summary>
/// Request payload for exporting PrintFarmer profiles to OrcaSlicer config bundle format.
/// </summary>
public class ExportOrcaBundleRequest
{
    /// <summary>
    /// Optional list of printer model IDs to export. If null/empty, exports all.
    /// </summary>
    public IReadOnlyList<Guid>? PrinterModelIds { get; set; }

    /// <summary>
    /// Optional list of filament type IDs to export. If null/empty, exports all.
    /// </summary>
    public IReadOnlyList<Guid>? FilamentTypeIds { get; set; }

    /// <summary>
    /// Include process/print profiles in export.
    /// </summary>
    public bool IncludeProcessProfiles { get; set; } = true;

    /// <summary>
    /// Include metadata (version, timestamp, source) in export.
    /// </summary>
    public bool IncludeMetadata { get; set; } = true;
}

#pragma warning restore SA1402
