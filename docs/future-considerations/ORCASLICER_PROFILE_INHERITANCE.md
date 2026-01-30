# OrcaSlicer Profile Inheritance Model

## Overview

OrcaSlicer profiles use an **inheritance-based architecture** where profiles can inherit settings from parent profiles. This reduces duplication and makes it easy to maintain multiple variants of the same profile type.

## Profile Types

### 1. Base/Template Profiles
- **Identification**: `"instantiation": "false"`
- **Pattern**: Named like `fdm_filament_pla.json`, `process_common_mk4.json`
- **Characteristics**:
  - Have `"instantiation": "false"` - **CANNOT be used in the slicer**
  - Have `"inherits"` property pointing to parent profile (e.g., `fdm_filament_common`)
  - May not have `compatible_printers` (these are templates)
  - Examples: `fdm_filament_pla.json`, `fdm_filament_flex.json`, `process_common_mk4.json`
- **Purpose**: Define default settings for a material or printer type (other profiles inherit from these)

### 2. Named/User-Facing Profiles  
- **Identification**: `"instantiation": "true"`
- **Pattern**: Named like `Prusa Generic PLA @MK4S 0.8.json`, `0.40mm Standard @MK4.json`
- **Characteristics**:
  - Have `"instantiation": "true"` - **CAN be used in the slicer** ✅
  - May have `"inherits"` pointing to a parent profile (e.g., `Prusa Generic PLA @MK4S` → `Prusa Generic PLA`)
  - May have `"compatible_printers"` array (but not always required)
  - **ARE user-selectable** - these show up in slicer UI and API
  - Examples: 259 Prusa filament profiles (out of 272 total), many process profiles with "@" in name
- **Purpose**: Provide actual recommended settings for specific printer/material/process combinations that users can select

## Inheritance Chain Example

For PLA on Prusa MK4S:

```
fdm_filament_common.json (system base)
  ↑ inherits
fdm_filament_pla.json (system template)
  ↑ inherits
Prusa Generic PLA.json (or similar family profile)
  ↑ inherits
Prusa Generic PLA @MK4S.json (coarse nozzle)
  ↑ inherits
Prusa Generic PLA @MK4S 0.6.json (specific variant)
  ↑ inherits
Prusa Generic PLA @MK4S 0.8.json (specific variant)
```

Each level can add or override settings from its parent.

## Profile Counts (Prusa Example)

| Category | Count | Has compatible_printers | Use |
|----------|-------|------------------------|-----|
| Base system profiles | 13 | NO | Used for inheritance only |
| Named user profiles | 259 | YES | Actually used by end users |
| **Total Prusa filament** | **272** | **259 have it** | |

## Seeding Implications

### ✅ What We SHOULD Do (Correct Approach)
1. **Start with `{Manufacturer}.json`** - This is the source of truth for which profiles exist
2. **Load each profile referenced in the JSON**
3. **Filter to ONLY profiles with `"instantiation": "true"`** - These are the ones users can actually select
4. **Import these instantiable profiles** to the database **with ALL inherited properties fully resolved**
   - Since we're NOT importing base/template profiles (`instantiation: false`), the inherited properties must already be merged into the profiles we store
   - If a profile has `"inherits": "fdm_filament_pla"`, all of `fdm_filament_pla`'s properties must be merged into this profile before we save it
   - Otherwise, at runtime when the UI needs a setting from the inherited parent, it won't be found in our database
5. **Do NOT import base/template profiles** - They're only needed for inheritance resolution at seeding time, not at runtime

### ❌ What We Currently Do (WRONG)
1. Import ALL profiles from the worker response (both base and user-facing)
2. Rely on `compatible_printers` as the marker for "real" profiles (not always reliable)
3. Don't check the `instantiation` flag
4. End up with cluttered profile list including unusable templates

## Database Storage

In PrintFarmer's database:
- `MachineProfiles` table stores ONLY profiles with `instantiation: true`
- `FilamentProfiles` table stores ONLY profiles with `instantiation: true`
  - Examples: "Prusa Generic PLA @MK4S 0.8" (instantiation=true)
  - NOT: "fdm_filament_pla" (instantiation=false) 
- `ProcessProfiles` table stores ONLY profiles with `instantiation: true`
  - Examples: "0.40mm Standard @MK4" (instantiation=true)
  - NOT: "process_common_mk4" (instantiation=false)

## API/UI Behavior

When the UI needs to show available filament profiles for a specific printer:
1. Query database for all FilamentProfiles with matching `compatible_printers`
2. Display only those with `compatible_printers` (not base profiles)
3. If inheritance resolution is needed, OrcaSlicer's runtime handles it

## Key Insight

✨ **The `instantiation` field is the SILVER BULLET for determining which profiles to import.**

OrcaSlicer provides this explicit flag to tell us:
- `"instantiation": "true"` → This profile is meant to be used by end users, import it
- `"instantiation": "false"` → This is a base/template profile, don't import it (OrcaSlicer will handle inheritance at runtime)

**Our seeding must filter by `instantiation: true`, NOT by `compatible_printers` or name patterns.**

Profiles with `instantiation: false` are framework profiles that support inheritance. They shouldn't appear in our database because:
1. Users can't select them in the UI anyway
2. OrcaSlicer handles inheritance resolution internally
3. We only need the end-user-selectable profiles in our database
