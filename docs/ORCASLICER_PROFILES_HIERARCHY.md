# OrcaSlicer Profiles Hierarchy & Architecture

## Overview

OrcaSlicer profiles are organized in a complex 4-list hierarchy system. This document explains how profiles are structured, loaded, related to each other, and exposed through the PrintFarmer API.

## Bundle Structure

Each OrcaSlicer manufacturer has a JSON bundle file located at `/opt/orcaslicer/resources/profiles/{manufacturer_name}.json`. Each bundle contains 4 distinct JSON lists:

### 1. Machine Model List (`machine_model_list`)

**Purpose**: Defines base printer models without specific variants

**Location in JSON**: `machine_model_list`

**Example**:
```json
{
  "machine_model_list": [
    {
      "name": "Prusa CORE One",
      "sub_path": "machine/Prusa CORE One.json"
    }
  ]
}
```

**Properties**:
- `name`: Human-readable model name (e.g., "Prusa CORE One")
- `sub_path`: Relative path to the machine profile JSON file

**Loading**: These are loaded first to establish the base model hierarchy.

### 2. Machine List (`machine_list`)

**Purpose**: Defines variant profiles for each model (typically one per nozzle size)

**Location in JSON**: `machine_list`

**Example**:
```json
{
  "machine_list": [
    {
      "name": "Prusa CORE One 0.4 nozzle",
      "sub_path": "machine/Prusa CORE One 0.4 nozzle.json"
    },
    {
      "name": "Prusa CORE One 0.6 nozzle",
      "sub_path": "machine/Prusa CORE One 0.6 nozzle.json"
    }
  ]
}
```

**Properties**:
- `name`: Variant name including nozzle size (e.g., "Prusa CORE One 0.4 nozzle")
- `sub_path`: Relative path to the machine profile JSON file

**Critical Note**: Both lists are loaded by the service. The variants in `machine_list` are the names referenced by filament and process profiles' `compatible_printers` arrays.

**Size Variation**: A typical manufacturer may have:
- ~10 base models from `machine_model_list`
- ~50-100 variants in `machine_list` (multiple nozzle sizes per model)
- Total: ~110-200 machine profiles per manufacturer

### 3. Process List (`process_list`)

**Purpose**: Defines process/speed profiles with slicing parameters

**Location in JSON**: `process_list`

**Example**:
```json
{
  "process_list": [
    {
      "name": "0.10mm Quality @NOZZLE_0.4",
      "sub_path": "process/0.10mm Quality @NOZZLE_0.4.json",
      "compatible_printers": [
        "Prusa CORE One 0.4 nozzle",
        "Prusa CORE One 0.6 nozzle",
        "Prusa MINI+ 0.4 nozzle"
      ]
    }
  ]
}
```

**Properties**:
- `name`: Process name describing quality/speed settings
- `sub_path`: Relative path to the process profile JSON file
- **`compatible_printers`**: Array of machine variant names (CRITICAL)

**Key Insight**: The `compatible_printers` array contains exact names from `machine_list`, NOT from `machine_model_list`. This is the primary link between process profiles and machine variants.

**Quantity**: ~2200 process profiles across all manufacturers

### 4. Filament List (`filament_list`)

**Purpose**: Defines material/filament profiles with print parameters

**Location in JSON**: `filament_list`

**Example**:
```json
{
  "filament_list": [
    {
      "name": "PLA @MATERIAL_PLA",
      "sub_path": "filament/PLA @MATERIAL_PLA.json",
      "compatible_printers": [
        "Prusa CORE One 0.4 nozzle",
        "Prusa i3 MK3S+ 0.4 nozzle",
        "Prusa i3 MK3S+ 0.6 nozzle"
      ]
    }
  ]
}
```

**Properties**:
- `name`: Filament name with material type marker
- `sub_path`: Relative path to the filament profile JSON file
- **`compatible_printers`**: Array of machine variant names (CRITICAL)

**Quantity**: ~2000 filament profiles across all manufacturers

## Hierarchy Relationships

The profile hierarchy is organized as follows:

```
Manufacturer (e.g., "Prusa")
├── Model Base Name (e.g., "CORE One")
│   ├── Machine Variants (from machine_list)
│   │   ├── "Prusa CORE One 0.4 nozzle"
│   │   ├── "Prusa CORE One 0.6 nozzle"
│   │   └── ...
│   ├── Associated Filament Profiles (matched via compatible_printers)
│   │   ├── "PLA @MATERIAL_PLA"
│   │   ├── "PETG @MATERIAL_PETG"
│   │   └── ...
│   └── Associated Process Profiles (matched via compatible_printers)
│       ├── "0.10mm Quality @NOZZLE_0.4"
│       ├── "0.20mm Normal @NOZZLE_0.4"
│       └── ...
└── Next Model...
```

**Critical Relationships**:
1. **Base Model → Variants**: Extracted from machine variant names
   - "Prusa CORE One 0.4 nozzle" → Base model "Prusa CORE One"
   - Multiple variants per base model (one per nozzle size)

2. **Variants → Filament/Process**: Via `compatible_printers` array matching
   - Filament/Process profile lists machine variant names
   - Service groups profiles with matching variants under the same base model

3. **No Manufacturer Grouping for Materials**: Filament and process profiles in the response are grouped under "Unknown" manufacturer
   - This is intentional: materials are generic, not manufacturer-specific
   - They're associated to machines via the compatible_printers relationship

## JSON Property Name Mapping

⚠️ **CRITICAL**: OrcaSlicer uses snake_case JSON property names. DTOs must use [JsonPropertyName] attributes:

```csharp
// Bundle level (ManufacturerBundleDto)
[JsonPropertyName("machine_model_list")]
public IList<ManufacturerBundleProfileEntry> MachineModelList { get; set; }

[JsonPropertyName("machine_list")]
public IList<ManufacturerBundleProfileEntry> MachineList { get; set; }

[JsonPropertyName("process_list")]
public IList<ManufacturerBundleProfileEntry> ProcessList { get; set; }

[JsonPropertyName("filament_list")]
public IList<ManufacturerBundleProfileEntry> FilamentList { get; set; }

// Entry level (ManufacturerBundleProfileEntry)
[JsonPropertyName("sub_path")]
public string SubPath { get; set; }

// Profile level (FilamentProfileDto, ProcessProfileDto)
[JsonPropertyName("compatible_printers")]
public IList<string> CompatiblePrinters { get; set; }
```

## Service Implementation

### OrcaProfilesService

Located in: `src/orcaslicer-worker/Services/OrcaProfilesService.cs`

**Key Methods**:

1. **`ListAvailableMachineProfilesAsync()`**
   - Loads BOTH `MachineModelList` AND `MachineList` from bundles
   - Returns all machine profiles (base models + variants)
   - Expected: ~50-200 profiles per manufacturer
   - Sets `Manufacturer` property on all profiles

2. **`ListAvailableFilamentProfilesAsync()`**
   - Loads all filament profiles from `FilamentList`
   - Parses `compatible_printers` arrays
   - Expected: ~2000 total profiles (across all manufacturers)
   - Groups under "Unknown" manufacturer (materials are generic)

3. **`ListAvailableProcessProfilesAsync()`**
   - Loads all process profiles from `ProcessList`
   - Parses `compatible_printers` arrays
   - Expected: ~2200 total profiles (across all manufacturers)
   - Groups under "Generic" manufacturer (processes are generic)

4. **`ParseManufacturerBundle()`**
   - Loads and deserializes the bundle JSON file
   - Handles snake_case → PascalCase conversion via JsonPropertyName

## API Endpoint Response Structure

### GET /api/profiles

**Response Type**: `AllProfilesResponseDto`

**Structure**:
```json
{
  "byHierarchy": {
    "Prusa": {
      "name": "Prusa",
      "models": {
        "Prusa_CORE_One": {
          "machineProfiles": [
            {
              "name": "Prusa CORE One 0.4 nozzle",
              "id": "prusa_core_one_04",
              "manufacturer": "Prusa",
              ...
            }
          ],
          "filamentProfiles": [
            {
              "name": "PLA @MATERIAL_PLA",
              "compatiblePrinters": ["Prusa CORE One 0.4 nozzle", ...],
              ...
            }
          ],
          "processProfiles": [
            {
              "name": "0.10mm Quality @NOZZLE_0.4",
              "compatiblePrinters": ["Prusa CORE One 0.4 nozzle", ...],
              ...
            }
          ]
        }
      }
    },
    "Anycubic": { ... },
    ...
  },
  "machineProfiles": [ ... ],
  "filamentProfiles": { "Unknown": [ ... ] },
  "processProfiles": { "Generic": [ ... ] }
}
```

**Key Properties**:
- `byHierarchy`: Manufacturer → Model → (Machines + Filaments + Processes)
- `machineProfiles`: Flat list of all machine profiles
- `filamentProfiles`: Dictionary, keyed by manufacturer (typically "Unknown")
- `processProfiles`: Dictionary, keyed by manufacturer (typically "Generic")

## Data Statistics

**Expected Profile Counts** (as of OrcaSlicer 2.3.1):
- **Machine Profiles**: ~50-200 per manufacturer (variants with nozzle sizes)
  - Example Prusa: ~60 profiles (multiple models × multiple nozzles)
- **Filament Profiles**: ~2000 total (across all manufacturers)
  - Shared across multiple machine variants
  - Organized under "Unknown" manufacturer in response
- **Process Profiles**: ~2200 total (across all manufacturers)
  - Shared across multiple machine variants
  - Organized under "Generic" manufacturer in response
- **Total**: ~7500-8000 JSON files

## Debugging & Testing

### Check Bundle Structure
```bash
# See what's in a manufacturer bundle
curl http://localhost:8080/api/profiles | jq '.byHierarchy | keys'
```

### Check Model Details
```bash
# See all models for a manufacturer
curl http://localhost:8080/api/profiles | jq '.byHierarchy.Prusa.Models | keys'

# See details of a specific model
curl http://localhost:8080/api/profiles | jq '.byHierarchy.Prusa.Models."Prusa_CORE_One"'
```

### Verify Compatible Printers Matching
```bash
# Check if filaments have compatible_printers arrays
curl http://localhost:8080/api/profiles | jq '.filamentProfiles."Unknown"[0].compatiblePrinters'

# Check if processes have compatible_printers arrays
curl http://localhost:8080/api/profiles | jq '.processProfiles."Generic"[0].compatiblePrinters'
```

### Verify Machine Variant Names
```bash
# Get machine profile names for a model
curl http://localhost:8080/api/profiles | jq '.byHierarchy.Prusa.Models."Prusa_CORE_One".machineProfiles[].name'
```

## Integration Points

### PrintFarmer API
- Endpoint: `GET /api/profiles`
- Served by: `SlicerProfilesController` in orcaslicer-worker
- Used by: Main API to fetch profiles for printer setup UI

### Database Seeding (Future)
- Import profiles from worker API into database
- Create `SlicerProfile` entities with manufacturer/model relationships
- Link profiles to `PrinterModel` entities for UI display

### React UI (Future)
- Display hierarchy in printer setup wizard
- Allow users to select:
  1. Manufacturer → Model base
  2. Machine variant (nozzle size)
  3. Associated filament profiles
  4. Associated process profiles

## Common Issues & Solutions

### Issue: Empty filamentProfiles/processProfiles in hierarchy
**Cause**: compatible_printers array in profiles not matching machine variant names
**Solution**: Ensure `ListAvailableMachineProfilesAsync()` loads from BOTH lists (model_list + machine_list)

### Issue: Profiles grouped under "Unknown"/"Generic"
**Cause**: Filament/process profiles don't have a manufacturer (intentional)
**Solution**: This is by design - materials are generic, linked via compatible_printers

### Issue: JSON deserialization errors
**Cause**: Missing [JsonPropertyName] attributes on snake_case properties
**Solution**: Add [JsonPropertyName("snake_case_name")] to all DTO properties matching JSON keys

### Issue: 404 errors when calling /api/profiles from host
**Cause**: Docker network routing or nginx reverse proxy issue
**Solution**: Test from inside container: `docker exec printfarmer-orcaslicer-worker curl http://localhost:8080/api/profiles`

## Files Reference

**Implementation Files**:
- `src/shared/Models.cs` - DTOs: `ManufacturerBundleDto`, `FilamentProfileDto`, `ProcessProfileDto`, `AllProfilesResponseDto`, etc.
- `src/orcaslicer-worker/Services/OrcaProfilesService.cs` - Profile loading and parsing
- `src/orcaslicer-worker/Controllers/SlicerProfilesController.cs` - REST API endpoint
- `/opt/orcaslicer/resources/profiles/` - Manufacturer bundle files (runtime, container only)

**Configuration**:
- Bundle path: `/opt/orcaslicer/resources/profiles/`
- Service: `ISlicerProfilesService` (injected into controller)
- Endpoint: `GET /api/profiles` (returns `AllProfilesResponseDto`)
