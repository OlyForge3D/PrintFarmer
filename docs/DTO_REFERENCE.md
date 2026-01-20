# DTO Definitions Reference

This document provides the exact class definitions and constructor signatures for the key DTOs used in PrintFarmer tests.

**Last Updated**: January 18, 2026

## 1. PrinterFastDto

**File**: `/home/pi/pfarm/src/infra/Models.cs`

**Definition**:
```csharp
/// <summary>
/// Fast printer information for dashboard loading - excludes camera URLs which are available via separate endpoint.
/// </summary>
public record PrinterFastDto(
    Guid Id,
    string Name,
    string? Notes,
    bool IsOnline,
    string? State,
    string? ManufacturerName = null,
    string? ModelName = null,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null,
    int BackendPort = 80,
    int? FrontendPort = null,
    bool InMaintenance = false,
    bool IsEnabled = true,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    string? BackendUrl = null,
    string? FrontendUrl = null);
```

**Key Points**:
- **No Progress field** - This is a lightweight DTO for fast dashboard loading
- **No ServerUrl field** - Use `BackendUrl` or `FrontendUrl` instead
- It's a `record` (C# positional record), so constructor parameters map directly to properties
- Parameters can be passed positionally or by name
- Default values provided for optional parameters

---

## 2. CreatePrinterDto

**File**: `/home/pi/pfarm/src/infra/Models.cs`

**Definition**:
```csharp
/// <summary>
/// Request payload for creating a new printer entry.
/// </summary>
public class CreatePrinterDto : PrinterInfoDto
{
    /// <summary>
    /// Reference to existing manufacturer in catalog.
    /// If null and NewManufacturerName is provided, a new manufacturer will be created.
    /// </summary>
    public Guid? ManufacturerId { get; set; }

    /// <summary>
    /// Reference to existing model in catalog.
    /// If null and NewModelName is provided, a new model will be created.
    /// </summary>
    public Guid? ModelId { get; set; }

    /// <summary>
    /// Create new manufacturer with this name if ManufacturerId is not provided.
    /// </summary>
    public string? NewManufacturerName { get; set; }

    /// <summary>
    /// Create new model with this name if ModelId is not provided.
    /// </summary>
    public string? NewModelName { get; set; }

    /// <summary>
    /// Location name to assign printer to during import.
    /// Location must already exist or will be skipped.
    /// </summary>
    public string? LocationName { get; set; }

    /// <summary>
    /// Date the printer was acquired (optional metadata).
    /// </summary>
    public DateTime? DateAcquired { get; set; }

    /// <summary>
    /// Whether this printer is visible to normal users.
    /// false = pending admin approval, hidden from normal users
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Hardware specification fields - populated from exported printer data or discovery
    /// </summary>
    public double? MaxBuildVolumeX { get; set; }
    public double? MaxBuildVolumeY { get; set; }
    public double? MaxBuildVolumeZ { get; set; }
    public bool HasHeatedBed { get; set; } = true;
    public bool HasEnclosure { get; set; } = false;
    public bool MultiMaterial { get; set; } = false;
    public bool SupportsAutoLeveling { get; set; } = false;
    public double? NozzleDiameter { get; set; }
    public string[]? SupportedMaterials { get; set; }
    public int? MaxHotendTemp { get; set; }
    public int? MaxBedTemp { get; set; }
    public string? CurrentMaterial { get; set; }
    public int? CurrentSpoolId { get; set; }

    /// <summary>
    /// Toolhead configurations for multi-toolhead printers.
    /// If provided during import, these will be created instead of the default single toolhead.
    /// If null, a default single toolhead will be created.
    /// </summary>
    public List<CreateToolheadDto>? Toolheads { get; set; }

    /// <summary>
    /// Create from discovered printer info with optional catalog metadata.
    /// </summary>
    public static CreatePrinterDto FromDiscovered(
        DiscoveredPrinterDto discovered,
        Guid? manufacturerId = null,
        Guid? modelId = null,
        string? newManufacturerName = null,
        string? newModelName = null) => new() { ... };
}
```

**Base Class - PrinterInfoDto**:
```csharp
public class PrinterInfoDto
{
    public string Name { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string? OriginalServerUrl { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public PrinterBackend Backend { get; set; }
    public int? BackendPort { get; set; }
    public int? FrontendPort { get; set; }
    public string? CameraStreamUrl { get; set; }
    public string? CameraSnapshotUrl { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Notes { get; set; }
    public string? ApiKey { get; set; }
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
    public bool IsReachable { get; set; }
}
```

**Key Points**:
- CreatePrinterDto inherits from PrinterInfoDto
- Uses property-based initialization (not positional record)
- Constructor: `new CreatePrinterDto { ... }` with property assignments
- `IsEnabled` defaults to `true`
- Has factory method `FromDiscovered()` for creation from discovered printers
- Includes hardware specs and toolhead support for imports

---

## 3. UpdatePrinterDto

**File**: `/home/pi/pfarm/src/infra/Models.cs`

**Definition**:
```csharp
/// <summary>
/// Update payload for modifying core printer attributes or reassigning catalog metadata.
/// </summary>
public record UpdatePrinterDto(
    string? Name = null,
    string? ServerUrl = null,
    string? Notes = null,
    Guid? ManufacturerId = null,
    Guid? ModelId = null,
    string? NewManufacturerName = null,
    string? NewModelName = null,
    DateTime? DateAcquired = null,
    PrinterBackend? Backend = null,
    string? ApiKey = null,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    string? OriginalServerUrl = null,
    // Printer capabilities
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool? HasHeatedBed = null,
    bool? HasEnclosure = null,
    bool? MultiMaterial = null,
    int? NumberOfExtruders = null,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,
    bool? SupportsAutoLeveling = null,
    int? MaxPrintSpeed = null,
    int? BackendPort = null,
    int? FrontendPort = null,
    // Approval workflow
    bool? IsEnabled = null,
    // Toolheads - for updating individual toolhead settings
    UpdateToolheadDto[]? Toolheads = null);
```

**Key Points**:
- It's a `record` (C# positional record)
- **ALL parameters are optional** with `= null` defaults
- Supports partial updates - only non-null values are applied
- Includes `Toolheads` array for updating individual toolhead settings
- Can use positional or named parameters when calling

---

## 4. Toolhead DTOs

### ToolheadDto (Read)

**File**: `/home/pi/pfarm/src/infra/Models.cs`

**Definition**:
```csharp
/// <summary>
/// Toolhead data for reading/display purposes.
/// </summary>
public record ToolheadDto(
    Guid Id,
    string? Name,
    int Index,
    double? NozzleDiameter,
    int? MaxHotendTemp,
    string[]? SupportedMaterials,
    bool IsPrimary,
    DateTime? LastUpdated = null);
```

### UpdateToolheadDto (Update)

**Definition**:
```csharp
/// <summary>
/// Update payload for modifying toolhead settings.
/// </summary>
public record UpdateToolheadDto(
    Guid Id,
    string? Name = null,
    int? Index = null,
    double? NozzleDiameter = null,
    int? MaxHotendTemp = null,
    string[]? SupportedMaterials = null,
    bool? IsPrimary = null);
```

### CreateToolheadDto (Create)

**Definition**:
```csharp
/// <summary>
/// Toolhead configuration for creating new toolheads.
/// </summary>
public class CreateToolheadDto
{
    public Guid? Id { get; set; }
    public string? Name { get; set; }
    public int Index { get; set; }
    public double? NozzleDiameter { get; set; }
    public int? MaxHotendTemp { get; set; }
    public string[]? SupportedMaterials { get; set; }
    public bool IsPrimary { get; set; }
}
```

**Key Points**:
- `ToolheadDto` is for reading toolhead data (returned from API)
- `UpdateToolheadDto` is for updating - only `Id` is required, all others optional
- `CreateToolheadDto` is a POCO class for creating toolheads during printer import
- Multi-toolhead printers use these for each extruder/tool

---

## 5. PrinterCapabilitiesDto

**File**: `/home/pi/pfarm/src/infra/Models.cs`

**Definition**:
```csharp
public record PrinterCapabilitiesDto(
    Guid Id,
    Guid PrinterId,
    string PrinterName,
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    bool SupportsAutoLeveling = false,
    int NumberOfExtruders = 1,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,
    int? MaxPrintSpeed = null,
    string? CurrentMaterial = null,
    int? CurrentSpoolId = null,
    bool IsAvailable = true,
    DateTime LastUpdated = default);
```

**Key Points**:
- Used in PrinterDetailsDto for backward compatibility
- Primary toolhead values are used for `NozzleDiameter`, `MaxHotendTemp`, `SupportedMaterials`
- `NumberOfExtruders` comes from Toolheads collection count

---

## 6. PrintJobStatusDto

**File**: `/home/pi/pfarm/src/infra/PrintJobStatusDto.cs`

**Definition**:
```csharp
/// <summary>
/// DTO for reporting print job status and all available properties.
/// </summary>
public class PrintJobStatusDto
{
    public string? State { get; set; }
    public double? Progress { get; set; }
    public string? JobName { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? Error { get; set; }
}
```

**Key Points**:
- Simple **POCO class** (Plain Old C# Object) - NOT a record
- **No constructor parameters** - uses property-based initialization
- All properties are optional (nullable types)
- Usage: `new PrintJobStatusDto { State = "Printing", Progress = 45.5, ... }`

---

## 7. Enum: PrinterBackend

**File**: `/home/pi/pfarm/src/infra/Models.cs`

**Definition**:
```csharp
[JsonConverter(typeof(Json.PrinterBackendJsonConverter))]
public enum PrinterBackend
{
    Unknown = 0,
    Moonraker = 1,
    PrusaLink = 2,
    SDCP = 3,
    OctoPrint = 4
}
```

**Key Points**:
- Has custom JSON converter for backward compatibility (numeric + string input)
- Values: `Unknown`, `Moonraker`, `PrusaLink`, `SDCP`, `OctoPrint`
- Used in all printer DTOs

---

## Summary Table

| DTO | Type | Constructor Style | File |
|-----|------|-------------------|------|
| **PrinterFastDto** | Positional Record | `new PrinterFastDto(id, name, ...)` | Models.cs |
| **CreatePrinterDto** | Class (extends PrinterInfoDto) | `new CreatePrinterDto { Name = "...", ... }` | Models.cs |
| **UpdatePrinterDto** | Positional Record | `new UpdatePrinterDto(name: "...", ...)` | Models.cs |
| **ToolheadDto** | Positional Record | `new ToolheadDto(id, name, ...)` | Models.cs |
| **UpdateToolheadDto** | Positional Record | `new UpdateToolheadDto(id, name: "...")` | Models.cs |
| **CreateToolheadDto** | POCO Class | `new CreateToolheadDto { Name = "...", ... }` | Models.cs |
| **PrinterCapabilitiesDto** | Positional Record | `new PrinterCapabilitiesDto(id, printerId, ...)` | Models.cs |
| **PrintJobStatusDto** | POCO Class | `new PrintJobStatusDto { State = "...", ... }` | PrintJobStatusDto.cs |
| **PrinterBackend** | Enum | N/A (enum values) | Models.cs |

---

## Important Notes for Tests

### PrinterFastDto - NO Progress/ServerUrl Fields
- Tests attempting to pass `Progress` to PrinterFastDto will fail
- Tests using `ServerUrl` should use `BackendUrl` or `FrontendUrl` instead
- Use `PrinterStatusDto` or `CompletePrinterDto` for progress tracking
- PrinterFastDto is optimized for dashboard loading without real-time status

### CreatePrinterDto - Inherits PrinterInfoDto
- Set base properties through property initialization: `Name`, `ServerUrl`, `IpAddress`, `Backend`, etc.
- Set derived properties through property initialization: `ManufacturerId`, `NewManufacturerName`, etc.
- Includes hardware specs: `MaxBuildVolumeX/Y/Z`, `HasHeatedBed`, `NozzleDiameter`, etc.
- Supports `Toolheads` for multi-toolhead printer imports

### UpdatePrinterDto - ALL Optional Parameters
- **Every parameter has a default value** (`= null`)
- Only provide the fields you want to update
- Includes `Toolheads` array for updating individual toolhead settings
- Use named parameters for clarity: `new UpdatePrinterDto(name: "New Name", maxBedTemp: 120)`

### Toolhead DTOs
- `ToolheadDto` - Read-only, returned from API
- `UpdateToolheadDto` - For updates, only `Id` is required
- `CreateToolheadDto` - For imports, uses property initialization
- One toolhead per extruder/tool on multi-tool printers

### PrintJobStatusDto - Simple POCO
- All properties nullable
- Use property initialization only
- No special constructor

### PrinterBackend - Standard Enum
- Use enum values: `PrinterBackend.Moonraker`, `PrinterBackend.PrusaLink`, etc.
- Custom converter handles JSON serialization automatically
- Can be cast from int: `(PrinterBackend)1` → `Moonraker`
