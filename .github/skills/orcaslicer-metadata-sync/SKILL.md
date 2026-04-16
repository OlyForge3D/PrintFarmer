---
name: orcaslicer-metadata-sync
description: Synchronize OrcaSlicer setting metadata (labels, tooltips, tab layout, icons) when upgrading OrcaSlicer versions. Use when ORCASLICER_VERSION changes, new settings appear, or profile editors need updating.
---

# OrcaSlicer Metadata Synchronization Skill

Use this skill when upgrading OrcaSlicer version and the profile editor metadata needs to stay in sync with the new binary. The metadata drives how filament, machine, and process profile editors render fields — labels, tooltips, units, tab/section layout, and section icons all come from this extraction.

## When to Use

- **Version upgrade**: After bumping `ORCASLICER_VERSION` in `.env` or Dockerfiles
- **New settings**: When a new OrcaSlicer release adds, removes, or renames print settings
- **Profile editor gaps**: When users report missing fields or wrong labels in the slicer UI
- **Icon refresh**: When OrcaSlicer updates its setting category icons

## Prerequisites

- **OrcaSlicer source code** checked out locally (e.g., `/Users/jpapiez/s/Orca/orcaslicer`)
  - Must match the version being deployed — `git checkout v2.X.Y` to the target version tag
- **Python 3.8+** with standard library (no extra packages needed)
- **Node.js / npm** for frontend build verification

## Synchronization Steps

### Step 1: Check Out the Correct OrcaSlicer Version

```bash
cd /path/to/orcaslicer
git fetch --tags
git checkout v2.X.Y   # Match the version you're upgrading to
```

The extraction tool reads two source files:
- `src/libslic3r/PrintConfig.cpp` — setting definitions (label, tooltip, type, min/max, default)
- `src/slic3r/GUI/Tab.cpp` — UI tab/section layout for filament, machine, and process editors

### Step 2: Run the Metadata Extraction Tool

```bash
cd /path/to/PFarm1

python3 tools/extract-orca-metadata.py \
  /path/to/orcaslicer/src \
  --output src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json
```

The tool outputs a summary like:
```
Extracted 781 settings from PrintConfig.cpp
Filament layout: 7 tabs, 89 fields
Machine layout: 5 tabs, 106 fields
Process layout: 6 tabs, 318 fields
Icons: 115 section icons
```

### Step 3: Review the Diff

```bash
git diff src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json
```

Look for:
- **Added fields**: New settings the profile editors need to render
- **Removed fields**: Deprecated settings that can be cleaned up from custom overrides
- **Changed labels/tooltips**: Improved descriptions from upstream
- **New tabs or sections**: Structural layout changes
- **Changed defaults/min/max**: Value constraint updates

### Step 4: Copy New SVG Icons

OrcaSlicer uses SVG icons for setting sections. Copy any new ones:

```bash
ORCA_SRC="/path/to/orcaslicer"
DEST="src/Web/ReactApp/public/icons/orca"

# Parameter tab icons (e.g., speed, infill, support)
cp "$ORCA_SRC/resources/images/param_"*.svg "$DEST/"

# Custom gcode icons
cp "$ORCA_SRC/resources/images/custom-gcode_"*.svg "$DEST/"

# Other icons referenced in the metadata (check icons key in JSON)
# Common ones: advanced.svg, fuzzy_skin.svg, note.svg, etc.
```

Verify all icons referenced in the metadata exist:

```bash
python3 -c "
import json, os
with open('src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json') as f:
    d = json.load(f)
icon_dir = 'src/Web/ReactApp/public/icons/orca'
missing = []
for name, path in d.get('icons', {}).items():
    basename = os.path.basename(path)
    if not os.path.exists(os.path.join(icon_dir, basename)):
        missing.append(basename)
if missing:
    print(f'MISSING {len(missing)} icons:')
    for m in missing:
        print(f'  {m}')
else:
    print(f'All {len(d[\"icons\"])} icons present ✓')
"
```

### Step 5: Check for New Material Types

OrcaSlicer's filament type list lives in `src/libslic3r/MaterialType.cpp`. If the new version adds material types, update the frontend constant.

```bash
# Extract material types from OrcaSlicer source
grep -oP '"[A-Z][A-Z0-9-]+"' /path/to/orcaslicer/src/libslic3r/MaterialType.cpp | sort -u
```

Compare with `ORCA_FILAMENT_TYPES` in:
```
src/Web/ReactApp/src/features/slicer/components/settings/FilamentProfileEditor.tsx
```

If new types exist, add them to the `ORCA_FILAMENT_TYPES` array (around line 63).

### Step 6: Build and Test

```bash
# Build frontend to catch TypeScript errors
cd src/Web/ReactApp
npm run build

# Run React tests
npm run test:run 2>&1 | tee /tmp/react-test-results.log

# Run .NET tests (if backend profile handling changed)
cd ../..
dotnet test ./farm-web.sln -c Debug 2>&1 | tee /tmp/dotnet-test-results.log
```

### Step 7: Visual Verification

Start the dev servers and verify profile editors render correctly:

1. Open a filament profile editor — all 7 tabs should render with correct labels
2. Open a machine profile editor — all 5 tabs with correct sections
3. Open a process profile editor — all 6 tabs including Quality, Strength, Speed, Support, Others
4. Check that section icons load (no broken image placeholders)
5. Hover over fields to verify tooltips display

### Step 8: Commit

```bash
cd /path/to/PFarm1
git add src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json
git add src/Web/ReactApp/public/icons/orca/
git add src/Web/ReactApp/src/features/slicer/components/settings/FilamentProfileEditor.tsx  # if material types changed

git commit -m "feat: sync OrcaSlicer metadata to vX.Y.Z

- Re-extracted settings metadata (N settings, M layout fields)
- Added N new section icons
- [Added N new material types]"
```

## Validation Checklist

After extraction, verify these counts in the `_meta` section of the JSON:

| Metric | v2.3.2 Baseline | Check |
|--------|-----------------|-------|
| `totalSettings` | 781 | Should increase or stay same (never decrease significantly) |
| `filamentSettings` | 108 | Filament-specific settings count |
| `machineSettings` | 113 | Machine-specific settings count |
| `processSettings` | 344 | Process-specific settings count |
| Filament tabs | 7 | Tab layout count |
| Machine tabs | 5 | Tab layout count |
| Process tabs | 6 | Tab layout count |
| Section icons | 115 | SVG icon mappings |

Quick validation command:

```bash
python3 -c "
import json
with open('src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json') as f:
    d = json.load(f)
m = d['_meta']
print(f\"Total settings: {m['totalSettings']}\")
print(f\"Filament: {m['filamentSettings']} settings, {len(d['filament']['tabs'])} tabs\")
print(f\"Machine: {m['machineSettings']} settings, {len(d['machine']['tabs'])} tabs\")
print(f\"Process: {m['processSettings']} settings, {len(d['process']['tabs'])} tabs\")
print(f\"Icons: {len(d['icons'])} section icons\")
"
```

## Troubleshooting

### Parser doesn't extract a new setting

**Symptom**: A setting exists in PrintConfig.cpp but is missing from the output JSON.

**Cause**: The setting definition uses an unfamiliar C++ pattern the regex parser doesn't handle.

**Fix**: Open `tools/extract-orca-metadata.py` and check the parsing regex patterns. Common issues:
- Multi-line `L("string" "continuation")` patterns not captured
- New config option types (e.g., `coPoint3`) not in `TYPE_MAP`
- Setting defined with a macro wrapper instead of direct `this->add()`

### Tab layout has fewer fields than expected

**Symptom**: `_meta` shows 344 process settings but layout only has 318 fields.

**Cause**: Some settings are defined in PrintConfig.cpp but not placed on any tab in Tab.cpp. These are either:
- Computed/internal settings not meant for user editing
- Settings managed by specialized UI outside the tab system

**This is normal.** The gap between total settings and layout fields is expected.

### Missing tooltips

**Symptom**: Fields render without tooltip hover text.

**Cause**: The setting exists in Tab.cpp layout but its `->tooltip` is set in a different code path or conditionally.

**Fix**: Search PrintConfig.cpp for the setting key and verify the tooltip is being parsed. The parser may need to handle `def->tooltip = L("...")` on a separate line from `def = this->add(...)`.

### Icons show as broken images

**Symptom**: Section headers show broken SVG placeholders.

**Cause**: The icon file wasn't copied to `src/Web/ReactApp/public/icons/orca/`.

**Fix**: Run the icon verification script from Step 4 to find which icons are missing, then copy them from the OrcaSlicer resources directory.

### New mode value not recognized

**Symptom**: Settings have `mode: null` instead of `simple`, `advanced`, or `developer`.

**Cause**: OrcaSlicer added a new mode constant not in the parser's `MODE_MAP`.

**Fix**: Check PrintConfig.cpp for new `comXxx` constants and add them to `MODE_MAP` in `extract-orca-metadata.py`.

## Architecture Reference

```
OrcaSlicer Source                     PrintFarmer Frontend
─────────────────                     ────────────────────
src/libslic3r/PrintConfig.cpp    ──┐
  (setting definitions)             │  tools/extract-orca-metadata.py
src/slic3r/GUI/Tab.cpp           ──┤  ────────────────────────────►
  (tab/section layout)             │
src/libslic3r/MaterialType.cpp   ──┘

                                      orcaSettingsMetadata.json
                                      └── _meta (counts, generator info)
                                      └── filament.tabs[] (7 tabs)
                                      └── machine.tabs[] (5 tabs)
                                      └── process.tabs[] (6 tabs)
                                      └── icons{} (115 section→SVG mappings)

resources/images/*.svg           ──►  public/icons/orca/*.svg
  (section icons)                       (118 SVG files)

                                      FilamentProfileEditor.tsx
                                        └── ORCA_FILAMENT_TYPES[] (material list)
                                      MachineProfileEditor.tsx
                                      ProcessProfileEditor.tsx
                                        └── All editors consume orcaSettingsMetadata.json
```

## Related Skills

- **orcaslicer-upgrade**: Full binary/profile/asset upgrade (run that skill first, then this one)
- **testing**: Validation workflow for build + test after metadata changes
