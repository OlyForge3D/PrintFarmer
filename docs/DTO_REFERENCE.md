# DTO Definitions Reference

This document provides the exact class definitions and constructor signatures for the key DTOs used in PrintFarmer tests.

## 1. PrinterFastDto

**File**: `/home/pi/pfarm/src/infra/Models.cs` (Lines ~225-245)

**Definition**:
```csharp
/// <summary>
/// Fast printer information for dashboard loading - excludes camera URLs which are available via separate endpoint.
/// </summary>
public partial record PrinterFastDto(
    Guid Id,
    string Name,
    string ServerUrl,
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
    bool IsEnabled = true);
```

**Key Points**:
- **No Progress field** - This is a lightweight DTO for fast dashboard loading
- Progress field is NOT included in PrinterFastDto
- It's a `record` (C# positional record), so constructor parameters map directly to properties
- Parameters can be passed positionally or by name
- Default values provided for optional parameters

---

## 2. CreatePrinterDto

**File**: `/home/pi/pfarm/src/infra/Models.cs` (Lines ~390-430)

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
    /// Date the printer was acquired (optional metadata).
    /// </summary>
    public DateTime? DateAcquired { get; set; }

    /// <summary>
    /// Whether this printer is visible to normal users.
    /// false = pending admin approval, hidden from normal users
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Create from discovered printer info with optional catalog metadata.
    /// </summary>
    public static CreatePrinterDto FromDiscovered(
        DiscoveredPrinterDto discovered,
        Guid? manufacturerId = null,
        Guid? modelId = null,
        string? newManufacturerName = null,
        string? newModelName = null) =>
        new CreatePrinterDto
        {
            Name = discovered.Name,
            ServerUrl = discovered.ServerUrl,
            OriginalServerUrl = discovered.OriginalServerUrl,
            IpAddress = discovered.IpAddress,
            Backend = discovered.Backend,
            BackendPort = discovered.BackendPort,
            FrontendPort = discovered.FrontendPort,
            CameraStreamUrl = discovered.CameraStreamUrl,
            CameraSnapshotUrl = discovered.CameraSnapshotUrl,
            Manufacturer = discovered.Manufacturer,
            Model = discovered.Model,
            Notes = discovered.Notes,
            ApiKey = discovered.ApiKey,
            DiscoveredAt = discovered.DiscoveredAt,
            IsReachable = discovered.IsReachable,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            NewManufacturerName = newManufacturerName,
            NewModelName = newModelName,
            IsEnabled = true
        };
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

---

## 3. UpdatePrinterDto

**File**: `/home/pi/pfarm/src/infra/Models.cs` (Lines ~432-460)

**Definition**:
```csharp
/// <summary>
/// Update payload for modifying core printer attributes or reassigning catalog metadata.
/// </summary>
public record UpdatePrinterDto(
    string Name,
    string ServerUrl,
    string? Notes,
    Guid? ManufacturerId,
    Guid? ModelId,
    string? NewManufacturerName,
    string? NewModelName,
    DateTime? DateAcquired,
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
    int? MinHotendTemp = null,
    int? MaxHotendTemp = null,
    int? MinBedTemp = null,
    int? MaxBedTemp = null,
    bool? SupportsAutoLeveling = null,
    int? MaxPrintSpeed = null,
    int? BackendPort = null,
    int? FrontendPort = null,
    // Approval workflow
    bool? IsEnabled = null);
```

**Key Points**:
- It's a `record` (C# positional record)
- **Required parameters** (no defaults):
  - `Name`, `ServerUrl`, `Notes`, `ManufacturerId`, `ModelId`, `NewManufacturerName`, `NewModelName`, `DateAcquired`
- **Optional parameters** (all with defaults):
  - All other parameters default to null or their stated value
- Constructor signature: `new UpdatePrinterDto(name, serverUrl, notes, manufacturerId, modelId, newMfgName, newModelName, dateAcquired, ...)`
- Can use positional or named parameters when calling

---

## 4. PrintJobStatusDto

**File**: `/home/pi/pfarm/src/infra/PrintJobStatusDto.cs` (Full file)

**Definition**:
```csharp
using System;

namespace Farm.Infrastructure
{
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
}
```

**Key Points**:
- Simple **POCO class** (Plain Old C# Object) - NOT a record
- **No constructor parameters** - uses property-based initialization
- All properties are optional (nullable types)
- Property list: `State`, `Progress`, `JobName`, `ThumbnailUrl`, `Error`
- Usage: `new PrintJobStatusDto { State = "Printing", Progress = 45.5, ... }`

---

## 5. Enum: PrinterBackend

**File**: `/home/pi/pfarm/src/infra/Models.cs` (Lines ~13-25)

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
| **PrinterFastDto** | Positional Record | `new PrinterFastDto(id, name, serverUrl, ...)` | Models.cs:~225 |
| **CreatePrinterDto** | Class (extends PrinterInfoDto) | `new CreatePrinterDto { Name = "...", ... }` | Models.cs:~390 |
| **UpdatePrinterDto** | Positional Record | `new UpdatePrinterDto(name, serverUrl, ...)` | Models.cs:~432 |
| **PrintJobStatusDto** | POCO Class | `new PrintJobStatusDto { State = "...", ... }` | PrintJobStatusDto.cs |
| **PrinterBackend** | Enum | N/A (enum values) | Models.cs:~13 |

---

## Important Notes for Tests

### PrinterFastDto - NO Progress Field
- Tests attempting to pass `Progress` to PrinterFastDto will fail
- Use `PrinterStatusDto` or `PrinterDto` for progress tracking
- PrinterFastDto is optimized for dashboard loading without real-time status

### CreatePrinterDto - Inherits PrinterInfoDto
- Set base properties through property initialization: `Name`, `ServerUrl`, `IpAddress`, `Backend`, etc.
- Set derived properties through property initialization: `ManufacturerId`, `NewManufacturerName`, etc.
- All properties are optional strings/values (no constructor parameters)

### UpdatePrinterDto - Required Parameters
- Must provide: Name, ServerUrl, Notes (can be null), ManufacturerId, ModelId, NewManufacturerName, NewModelName, DateAcquired
- All others are optional with null/default values
- Use named parameters for clarity when calling

### PrintJobStatusDto - Simple POCO
- All properties nullable
- Use property initialization only
- No special constructor

### PrinterBackend - Standard Enum
- Use enum values: `PrinterBackend.Moonraker`, `PrinterBackend.PrusaLink`, etc.
- Custom converter handles JSON serialization automatically
- Can be cast from int: `(PrinterBackend)1` → `Moonraker`
