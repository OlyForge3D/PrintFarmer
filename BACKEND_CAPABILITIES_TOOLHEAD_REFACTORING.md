# Backend Capabilities and Toolhead Architecture Refactoring

## Summary

This refactoring separates **backend capabilities** (features supported by printer client plugins) from **hardware specifications** (nozzle size, build volume, temps). It also introduces multi-toolhead support for modern printers.

## Key Changes

### 1. Backend Capabilities Architecture

**Problem**: The system had no way to expose to the UI which features a printer backend supports (camera, file upload, temperature control, etc.).

**Solution**: Created a new capabilities exposure system:

- **`BackendCapabilities` enum** (in `IBackendCapabilityFactory.cs`): Flags indicating which capabilities a backend supports
  - `Camera`, `FileDownload`, `FileList`, `FileUpload`, `StartPrint`, `ControlOperations`, `FileMetadata`, `Movement`, `TemperatureControl`, `PrinterInformation`, `History`

- **`IBackendCapabilityFactory`**: Already existed, now fully leveraged to:
  - `GetSupportedCapabilities(PrinterBackend)`: Returns which capabilities a backend has
  - Various `TryGetXxxClient()` methods: Get typed clients for specific capabilities

- **`PrinterBackendCapabilitiesDto`** (new file `src/infra/Models/PrinterBackendCapabilitiesDto.cs`):
  - DTO exposing backend capabilities to the UI
  - Properties like `SupportsCamera`, `SupportsFileUpload`, etc.
  - `SupportedCapabilityNames`: Helper property for readable capability list

- **`IPrinterBackendCapabilitiesService`** & **`PrinterBackendCapabilitiesService`**:
  - New service to convert backend plugin capabilities to DTOs
  - Converts `BackendCapabilities` flags → boolean properties for UI
  - Registered in DI as scoped service

### 2. Toolhead (Multi-Hotend) Support

**Problem**: Modern printers (Prusa XL, Bambu Lab X1, etc.) support multiple toolheads. Current system stored only single nozzle/hotend values.

**Solution**: Introduced `Toolhead` entity (new file `src/infra/Domain/Toolhead.cs`):

- Represents a single hotend/nozzle configuration
- Properties:
  - `Index`: Zero-based toolhead index (0, 1, 2, etc.)
  - `Name`: Friendly name ("Extruder 1", "Left Tool", etc.)
  - `NozzleDiameter`: Nozzle size in mm
  - `MinHotendTemp` / `MaxHotendTemp`: Temperature ranges
  - `SupportedMaterials`: Materials this toolhead can handle
  - `IsPrimary`: Whether this is the default toolhead
  - `HasHeatedEnclosure`: For high-temp materials

- One-to-many relationship: `PrinterCapabilities` → many `Toolhead`s

### 3. Updated PrinterCapabilities Entity

Refactored to separate concerns:

**Kept (Hardware Specs)**:
- Build volume (X, Y, Z)
- Bed temperature ranges
- Heated bed, enclosure, auto-leveling
- Multi-material support
- Current material/spool tracking
- Availability and timestamps

**Deprecated (Moved to Toolheads collection)**:
- `NozzleDiameter` (now in individual Toolhead)
- `MinHotendTemp` / `MaxHotendTemp` (now in individual Toolhead)
- `SupportedMaterials` (now in individual Toolhead)
- `NumberOfExtruders` (implicit from Toolheads.Count)

**New**:
- `Toolheads` collection: `ICollection<Toolhead>` for multi-toolhead support

### 4. GetCameraUrlsAsync Fix

Fixed in `PrintersService.GetCameraUrlsAsync()`:

**Before**: 
```csharp
if (backend != PrinterBackend.PrusaLink && 
    await IsCameraAvailableAsync(...)) // Made HTTP HEAD request to verify URL
```

**After**:
```csharp
var capabilities = _capabilityFactory.GetSupportedCapabilities(backend);
if ((capabilities & BackendCapabilities.Camera) == BackendCapabilities.Camera)
{
    // Fetch camera URLs
}
```

**Benefits**:
- Uses plugin capabilities instead of hardcoded backend checks
- No HTTP validation overhead (removed `IsCameraAvailableAsync`)
- URL presence is sufficient for enabling camera button

### 5. File Organization

Following "one class per file" principle:

- **New files created**:
  - `src/infra/Domain/Toolhead.cs`: Toolhead entity
  - `src/infra/Models/PrinterBackendCapabilitiesDto.cs`: Backend capabilities DTO
  - `src/api/Services/Printers/IPrinterBackendCapabilitiesService.cs`: Service interface
  - `src/api/Services/Printers/PrinterBackendCapabilitiesService.cs`: Service implementation

- **Modified files**:
  - `src/infra/Domain/Entities.cs`: Updated PrinterCapabilities, removed duplicate Toolhead
  - `src/infra/Models.cs`: Removed duplicate PrinterBackendCapabilitiesDto
  - `src/infra/Data/AppDbContext.cs`: Added `DbSet<Toolhead>`
  - `src/api/Services/Printers/PrintersService.cs`: Fixed GetCameraUrlsAsync
  - `src/api/Infrastructure/ServiceCollectionExtensions.cs`: Registered new service in DI

## Next Steps

1. **Create EF Core migration**: Add Toolhead table and update PrinterCapabilities
2. **Update DTOs**: Create `ToolheadDto` for API responses
3. **Create API endpoints**: 
   - `GET /api/printers/backend-capabilities` - List backend capabilities
   - `GET /api/printers/{id}/backend-capabilities` - Get specific printer's capabilities
4. **Update React UI**: 
   - Display backend capabilities for each printer
   - Show toolhead information where applicable
   - Use `SupportsCamera` from API instead of hardcoded checks
5. **Migration strategy**: Backward compatibility
   - Deprecated single-toolhead properties still populated
   - Single-toolhead printers auto-create one Toolhead entry
   - Existing code continues to work during transition

## Terminology Clarification

| Term | Definition | Example |
|------|-----------|---------|
| **Backend Capability** | Feature the printer client supports (plugin interface) | Camera, FileUpload, TemperatureControl |
| **Hardware Specification** | Physical printer properties | Nozzle diameter, build volume, max temp |
| **Toolhead** | Single hotend/nozzle configuration | "Extruder 1" (0.4mm), "Extruder 2" (0.6mm) |

This distinction is critical because Moonraker, PrusaLink, and SDCP have different backend capabilities (some support cameras, some don't), which is independent of the printer hardware (which might have multiple toolheads).
