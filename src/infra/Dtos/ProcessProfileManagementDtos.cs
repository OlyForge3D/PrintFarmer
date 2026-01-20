namespace Farm.Infrastructure;

// Slicer Profile Management DTOs
public class CreateProcessProfileDto
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string SlicerType { get; set; } = "PrusaSlicer"; // PrusaSlicer, OrcaSlicer, etc.

    public Guid? PrinterModelId { get; set; }

    public Guid? SpecificPrinterId { get; set; }

    public double LayerHeight { get; set; } = 0.2;

    public int InfillPercentage { get; set; } = 20;

    public double PrintSpeed { get; set; } = 50;

    public int NozzleTemperature { get; set; } = 210;

    public int BedTemperature { get; set; } = 60;

    public bool EnableSupports { get; set; }

    public string Material { get; set; } = "PLA";

    public string Quality { get; set; } = "Standard"; // Draft, Standard, Fine

    public string? AdvancedSettings { get; set; }

    public bool IsDefault { get; set; }

    public bool IsPublic { get; set; } = true;
}

public class ProcessProfileResponseDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string SlicerType { get; set; } = string.Empty;

    public Guid? PrinterModelId { get; set; }

    public string? PrinterModelName { get; set; }

    public Guid? SpecificPrinterId { get; set; }

    public string? SpecificPrinterName { get; set; }

    public double LayerHeight { get; set; }

    public int InfillPercentage { get; set; }

    public int PrintSpeed { get; set; }

    public int NozzleTemperature { get; set; }

    public int BedTemperature { get; set; }

    public bool EnableSupports { get; set; }

    public string Material { get; set; } = string.Empty;

    public string Quality { get; set; } = string.Empty;

    public string? AdvancedSettings { get; set; }

    public bool IsDefault { get; set; }

    public bool IsPublic { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

// Advanced profile import/export DTOs (Phase 6)
public class ImportProcessProfileDto
{
    public string RawJson { get; set; } = string.Empty; // Raw profile JSON from slicer export

    public string? Name { get; set; } // Optional override; if null we derive from profileType + layerHeight

    public string? Description { get; set; }

    public string SlicerType { get; set; } = "PrusaSlicer"; // PrusaSlicer, OrcaSlicer, etc.

    public bool AllowSystemOverride { get; set; } = false; // If true, system profile match by hash can be overridden

    public bool SetDefault { get; set; } = false; // If true, sets profile as default after import (scope: global if user absent)

    public bool IsPublic { get; set; } = true; // Visibility to other users
}

public class ProcessProfileExtendedDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string SlicerType { get; set; } = string.Empty;

    public double LayerHeight { get; set; }

    public int InfillPercentage { get; set; }

    public double PrintSpeed { get; set; }

    public bool EnableSupports { get; set; }

    public string Quality { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public bool IsPublic { get; set; }

    public bool IsSystem { get; set; }

    public string Hash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Dictionary<string, object?> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ProcessProfileExportDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SlicerType { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public string RawJson { get; set; } = string.Empty; // Sanitized raw profile JSON

    public Dictionary<string, object?> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
