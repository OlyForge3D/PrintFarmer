---
name: orcaslicer-profiles
description: >-
  Understand OrcaSlicer profile hierarchy, inheritance, and how PrintFarmer
  resolves profiles for a given printer. Use when debugging empty profiles,
  alias mismatches, missing machine/filament/process profiles, or investigating
  how profile lookup works end-to-end from the React UI through the API to the
  OrcaSlicer worker cache.
---

# OrcaSlicer Profile System

This skill explains how OrcaSlicer organises printer profiles and how PrintFarmer discovers, caches, and serves them. Use it whenever profiles are missing, empty, or mismatched for a printer.

---

## 1. The 4-List Bundle Structure

Each manufacturer has a JSON bundle at `/opt/orcaslicer/resources/profiles/{Manufacturer}.json` inside the OrcaSlicer worker container. The bundle contains four lists:

| List | Purpose | Example Entry |
|------|---------|---------------|
| `machine_model_list` | Base printer models (no nozzle variant) | `"Prusa CORE One"` |
| `machine_list` | Machine profiles — one per nozzle size | `"Prusa CORE One 0.4 nozzle"` |
| `filament_list` | Material profiles, optionally scoped to machines | `"PLA @MATERIAL_PLA"` |
| `process_list` | Quality/speed profiles, optionally scoped to machines | `"0.20mm Standard @NOZZLE_0.4"` |

Each entry has a `sub_path` pointing to a JSON file under the manufacturer's directory:

```
/opt/orcaslicer/resources/profiles/
  Prusa.json                              ← bundle index
  Prusa/
    machine/Prusa CORE One.json           ← machine_model (base)
    machine/Prusa CORE One 0.4 nozzle.json← machine (nozzle variant)
    machine/Prusa CORE One L 0.4 nozzle.json
    filament/Prusa Generic PLA @MK4S.json
    process/0.20mm Standard @NOZZLE_0.4.json
```

### Key field: `printer_model`

Inside each **machine profile JSON** (`machine_list` entry), the `printer_model` field links it back to a `machine_model_list` entry:

```json
{
  "name": "Prusa CORE One L 0.4 nozzle",
  "printer_model": "Prusa CORE One L",
  "inherits": "Prusa CORE One L HF 0.4 nozzle",
  "nozzle_diameter": [0.4],
  ...
}
```

**This field is what the worker uses for profile lookup.** The query is:

```sql
SELECT json_data FROM machine_profiles
WHERE printer_model = $alias COLLATE NOCASE
```

So if the alias sent by the API doesn't match any stored `printer_model` value, the result is empty.

---

## 2. Profile Inheritance

OrcaSlicer uses cascading inheritance to avoid duplication.

### The `instantiation` Flag

| Value | Meaning | Import? |
|-------|---------|---------|
| `"true"` | User-selectable profile | ✅ Yes |
| `"false"` | Template / abstract base | ❌ No |
| absent | Treated as `"true"` | ✅ Yes |

### Inheritance Chain Example

```
fdm_filament_common.json          (instantiation=false) ← system base
    ↑ inherits
fdm_filament_pla.json             (instantiation=false) ← PLA template
    ↑ inherits
Prusa Generic PLA.json            ← family profile
    ↑ inherits
Prusa Generic PLA @MK4S.json      ← printer-specific
    ↑ inherits
Prusa Generic PLA @MK4S 0.6.json  (instantiation=true) ← user-selectable ✅
```

### Resolution Algorithm

PrintFarmer stores **fully-resolved** profiles. At import time, every `inherits` chain is walked to the root and deep-merged so that each stored profile is self-contained.

```python
def resolve_profile(path):
    profile = load_json(path)
    if "inherits" in profile:
        parent = resolve_profile(find_parent(profile["inherits"]))
        profile = deep_merge(parent, profile)   # child overrides parent
    return profile
```

Merge rules:
- Scalar → child replaces parent
- Array → child replaces parent entirely
- Object → deep merge, child keys override
- null/undefined → inherited from parent

---

## 3. Compatibility Expressions

Filament and process profiles declare which machines they work with via two fields:

| Field | Type | Example |
|-------|------|---------|
| `compatible_printers` | String array of exact `machine_list` names | `["Prusa CORE One 0.4 nozzle"]` |
| `compatible_printers_condition` | Expression evaluated against machine settings | `printer_notes=~/.*PRINTER_MODEL_COREONE.*/` |

Expression syntax:
```
printer_notes=~/.*PRINTER_MODEL_COREONE.*/       # Regex match
nozzle_diameter[0]==0.4                           # Equality with array index
condition1 and condition2                         # Logical AND
condition1 or condition2                          # Logical OR
! condition                                       # Negation
```

The worker's expression parser has 98.2% coverage (641/654 profiles).

---

## 4. PrintFarmer Profile Lookup Chain

When the React UI requests profiles for a printer, the lookup traverses four layers:

```
React UI
  → GET /api/slicer/profiles/machine/for-model/{modelId}
    → ProfilesController.GetMachineProfilesForModelIdAsync()
      1. _catalogService.GetModelByIdAsync(modelId)       → PrinterModelDto
      2. _catalogService.GetModelAliasesAsync(modelId)    → List<SlicerModelAliasDto>
      3. Filter for SlicerType == "OrcaSlicer"            → extract SlicerModelName strings
      4. If no OrcaSlicer aliases → 404
      5. For each alias:
           → GET {workerUrl}/api/profiles/machine/{alias}
             → Worker normalises underscores to spaces
             → SQLite: WHERE printer_model = $alias COLLATE NOCASE
             → Returns matching MachineProfileDto[]
      6. Aggregate results → 200 OK
```

### Key files in the chain

| Layer | File | Key method/line |
|-------|------|-----------------|
| Controller | `src/slicer/Farm.Slicer.Module.Api/Controllers/Slicing/ProfilesController.cs` | `GetMachineProfilesForModelIdAsync` (~line 808) |
| Service | `src/slicer/Farm.Slicer.Module.Api/Services/ProfilesService.cs` | `GetMachineProfilesForCatalogModelAsync` (~line 1852) |
| Worker controller | `src/orcaslicer-worker/Controllers/SlicerProfilesController.cs` | `GET machine/{printerModel}` (~line 331) |
| Worker cache | `src/orcaslicer-worker/Services/ProfileCacheDb.cs` | `GetMachineProfilesByPrinterModelAsync` (~line 413) |
| Worker profile loader | `src/orcaslicer-worker/Services/CachedOrcaProfilesService.cs` | `EnsureInitializedAsync` |

### Seed data aliases

Printer-to-slicer name mappings are defined in `src/api/Data/seed/printer-models.yaml`:

```yaml
- name: "Prusa CORE One L"
  manufacturer: "Prusa"
  slicerAliases:
    - slicerModelName: "Prusa CORE One L"
      slicerType: "OrcaSlicer"
    - slicerModelName: "COREONEL"
      slicerType: "PrusaSlicer"
```

The `slicerModelName` for OrcaSlicer **must exactly match** the `printer_model` value inside OrcaSlicer machine profile JSONs (case-insensitive).

---

## 5. Worker Profile Cache

The OrcaSlicer worker builds an SQLite cache on startup by scanning all profile JSON files from the OrcaSlicer installation.

### Cache schema (machine_profiles table)

```sql
CREATE TABLE machine_profiles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    manufacturer TEXT NOT NULL,
    printer_model TEXT,          -- from profile JSON "printer_model" field
    nozzle_diameter REAL,
    inherits TEXT,
    json_data TEXT NOT NULL,
    UNIQUE(name, manufacturer)
);
CREATE INDEX idx_machine_model ON machine_profiles(printer_model);
```

### Cache population

`ProfileCacheDb.StoreMachineProfilesAsync()` iterates each `MachineProfileDto` and inserts:
- `printer_model` ← `profile.PrinterModel` ← from the JSON's `"printer_model"` field
- The query is `COLLATE NOCASE` so matching is case-insensitive

### Fallback

If the cache DB is not available, the worker falls back to in-memory filtering:
```csharp
result = (await _profileService.ListAvailableMachineProfilesAsync(ct))
    .Where(p => (p.PrinterModel ?? "").Equals(normalizedModel, StringComparison.OrdinalIgnoreCase))
    .ToList();
```

---

## 6. Debugging Empty Profiles

When a printer returns no profiles, follow this diagnostic path:

### Step 1: Check the endpoint response

```bash
# From the API server (or via proxy)
curl -s "http://localhost:5245/api/slicer/profiles/machine/for-model/{modelId}" | python3 -m json.tool
```

- **404** → Model ID not found or no OrcaSlicer aliases configured
- **200 + empty `[]`** → Aliases exist but worker returned nothing for them

### Step 2: Check seed data aliases

```bash
grep -A5 "CORE One L" src/api/Data/seed/printer-models.yaml
```

Confirm:
- An alias with `slicerType: "OrcaSlicer"` exists
- The `slicerModelName` value is correct

### Step 3: Check what `printer_model` OrcaSlicer actually uses

```bash
python3 -c "
import json, glob
for f in glob.glob('/path/to/OrcaSlicer/profiles/*/machine/*.json'):
    with open(f) as fh:
        d = json.load(fh)
    pm = d.get('printer_model', '')
    if 'core one l' in pm.lower():
        print(f'{pm:40s} ← {f.split(\"/\")[-1]}')
"
```

On macOS with installed OrcaSlicer:
```bash
python3 -c "
import json, glob
base = '/Users/$USER/Library/Application Support/OrcaSlicer/system'
for f in glob.glob(f'{base}/*/machine/*.json'):
    with open(f) as fh:
        d = json.load(fh)
    pm = d.get('printer_model', '')
    if pm:  # only profiles with printer_model set
        print(f'{pm:40s} ← {f.split(\"/\")[-1]}')
" | sort | head -30
```

### Step 4: Test the worker endpoint directly

```bash
# URL-encode spaces as %20
curl -s "http://localhost:5100/api/profiles/machine/Prusa%20CORE%20One%20L" | python3 -m json.tool
```

If empty, the alias doesn't match any `printer_model` in the worker's cache.

### Step 5: Inspect the worker cache

```bash
# Inside the worker container (Docker deployments)
docker exec printfarmer-orcaslicer-worker-1 bash -c "
sqlite3 /tmp/orca-profile-cache.db \
  \"SELECT DISTINCT printer_model FROM machine_profiles WHERE printer_model LIKE '%CORE One%' ORDER BY 1;\"
"
```

This shows the actual `printer_model` values in the cache — compare with the alias.

### Step 6: Check worker logs

The worker logs the count on each lookup:
```
Returning {Count} machine profiles for '{NormalizedModel}'
```

If `Count=0`, the `NormalizedModel` doesn't match.

---

## 7. Common Root Causes

| Symptom | Root Cause | Fix |
|---------|-----------|-----|
| 200 OK + empty `[]` | Alias doesn't match `printer_model` in OrcaSlicer profiles | Update seed alias or add a matching one |
| 404 | No OrcaSlicer alias for the printer model | Add `slicerType: "OrcaSlicer"` alias to seed data |
| Profiles for wrong printer | Alias matches a different printer's `printer_model` | Check for ambiguous aliases |
| Base profiles missing nozzle variants | Profile has `printer_model` but inheritance chain not resolved | Check worker startup logs for parse errors |
| New printer not found after OrcaSlicer upgrade | New printer added in newer OrcaSlicer version but seed alias not updated | Add alias to `printer-models.yaml`, reseed |
| Worker returns profiles but API returns empty | Slicer module adapter converts alias incorrectly | Check `ProfilesService.GetMachineProfilesForCatalogModelAsync()` |

---

## 8. Profile Counts Reference (OrcaSlicer 2.3.x)

| Metric | Count |
|--------|-------|
| Manufacturers | ~63 |
| Machine model profiles | ~200 (base models) |
| Machine profiles (nozzle variants) | ~600 |
| Filament profiles | ~2,000 |
| Process profiles | ~2,200 |
| Expression coverage | 98.2% (641/654) |

---

## 9. Related Files Quick Reference

| Purpose | Path |
|---------|------|
| Seed data (aliases) | `src/api/Data/seed/printer-models.yaml` |
| API profile controller | `src/slicer/Farm.Slicer.Module.Api/Controllers/Slicing/ProfilesController.cs` |
| API profile service | `src/slicer/Farm.Slicer.Module.Api/Services/ProfilesService.cs` |
| Worker profile controller | `src/orcaslicer-worker/Controllers/SlicerProfilesController.cs` |
| Worker cache DB | `src/orcaslicer-worker/Services/ProfileCacheDb.cs` |
| Worker profile loader | `src/orcaslicer-worker/Services/OrcaProfilesService.cs` |
| Worker cached service | `src/orcaslicer-worker/Services/CachedOrcaProfilesService.cs` |
| MachineProfileDto | `src/slicer/Farm.Slicer.Module/Dtos/MachineProfileDto.cs` |
| SlicerModelAliasDto | `src/infra/Dtos/SlicerModelAliasDto.cs` |
| Manifest (frontend assets) | `src/Web/ReactApp/public/assets/orcaslicer/manifest.json` |
| Full integration docs | `docs/ORCASLICER_INTEGRATION.md` |
| Upgrade skill | `.github/skills/orcaslicer-upgrade/SKILL.md` |
