---
name: orcaslicer-upgrade
description: >-
  Full OrcaSlicer version upgrade across the PrintFarmer stack. Use when
  upgrading OrcaSlicer binary, profiles, printer assets, OR setting metadata.
  Covers Dockerfiles, runtime deps, AppImage download, metadata extraction,
  SVG icon sync, profile editor verification, and end-to-end validation.
---

# OrcaSlicer Version Upgrade Skill

Use this skill when upgrading OrcaSlicer to a new version. Every upgrade touches multiple layers — binary, dependencies, profiles, metadata, icons, and frontend assets. Missing any layer causes subtle failures (blank profile editors, broken icons, slice jobs rejected by the CLI).

This skill consolidates the binary upgrade and the metadata synchronization into a single workflow. There is no separate metadata-sync skill — everything lives here.

## Quick Upgrade (experienced users)

Condensed command sequence. Each line must succeed before continuing.

```bash
# ── 0. Set variables ──────────────────────────────────────────────────────
OLD=2.3.2                # current version
NEW=2.4.0                # target version
ORCA_SRC=~/s/Orca/orcaslicer   # local OrcaSlicer source checkout
PFARM=~/s/PFarm1                # repo root

# ── 1. Pre-flight ─────────────────────────────────────────────────────────
# Confirm stable release exists on GitHub
open "https://github.com/SoftFever/OrcaSlicer/releases/tag/v${NEW}"
# Check out matching OrcaSlicer source tag
cd "$ORCA_SRC" && git fetch --tags && git checkout "v${NEW}"

# ── 2. Version bumps (all locations) ─────────────────────────────────────
cd "$PFARM"
sed -i '' "s/ORCASLICER_VERSION=${OLD}/ORCASLICER_VERSION=${NEW}/" \
  scripts/docker/dockerfiles/Dockerfile.multistage \
  scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries \
  scripts/docker/container-versions.conf
# Sync Dockerfile copies to root + dockerfiles/
cp scripts/docker/dockerfiles/Dockerfile.multistage Dockerfile.multistage
cp scripts/docker/dockerfiles/Dockerfile.multistage dockerfiles/Dockerfile.multistage

# Update compose templates
sed -i '' "s/ORCASLICER_VERSION:-${OLD}/ORCASLICER_VERSION:-${NEW}/" \
  scripts/docker/compose-templates/docker-compose.orcaslicer-worker.yml \
  scripts/docker/compose-templates/docker-compose.common.yml

# ── 3. Extract metadata ──────────────────────────────────────────────────
python3 tools/extract-orca-metadata.py "$ORCA_SRC/src" \
  --output src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json

# ── 4. Copy new SVG icons ────────────────────────────────────────────────
cp "$ORCA_SRC/resources/images/param_"*.svg  src/Web/ReactApp/public/icons/orca/
cp "$ORCA_SRC/resources/images/custom-gcode_"*.svg src/Web/ReactApp/public/icons/orca/

# ── 5. Update printer assets (macOS — requires OrcaSlicer.app vNEW) ─────
./scripts/restore-orcaslicer-assets.js

# ── 6. Review diffs ──────────────────────────────────────────────────────
git diff src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json | head -100
git diff src/Web/ReactApp/public/icons/orca/ | head -40

# ── 7. Frontend build + lint ─────────────────────────────────────────────
cd src/Web/ReactApp && npm run build && npm run lint && cd ../../..

# ── 8. .NET tests (serialization round-trip) ─────────────────────────────
cd src && dotnet test ./tests/Farm.OrcaSlicer.Worker.Tests/ \
  --filter "SettingsSerializationTests" -c Debug && cd ..

# ── 9. Visual verification (manual) ──────────────────────────────────────
# Start dev servers, open each profile editor, confirm rendering + icons

# ── 10. Deploy & end-to-end slice test ───────────────────────────────────
ssh pi@10.0.0.20 "cd /home/pi/pfarm && \
  sed -i 's/ORCASLICER_VERSION=${OLD}/ORCASLICER_VERSION=${NEW}/' .env && \
  docker compose --env-file .env build orcaslicer-worker && \
  docker compose --env-file .env up -d orcaslicer-worker"
```

## Prerequisites

- **macOS**: OrcaSlicer.app installed at `/Applications/OrcaSlicer.app` — download the target version from [OrcaSlicer releases](https://github.com/SoftFever/OrcaSlicer/releases)
- **OrcaSlicer source** checked out locally (e.g., `~/s/Orca/orcaslicer`) — must be checked out to the target version tag
- **Python 3.8+** with standard library (no extra packages)
- **Server**: SSH access to deployment server (pi@10.0.0.20)

---

## Full Upgrade Checklist

### Step 1: Pre-Flight Checks

Before starting, confirm:

1. **Stable release**: The target version is a stable release (not beta/rc). Check [OrcaSlicer releases](https://github.com/SoftFever/OrcaSlicer/releases).
2. **Source checkout matches**: The local OrcaSlicer source is checked out to the matching tag:
   ```bash
   cd /path/to/orcaslicer
   git fetch --tags
   git checkout v2.X.Y   # Must match the version you're upgrading to
   ```
3. **macOS app updated**: Download and install the matching OrcaSlicer.app version (required for printer asset extraction in Step 6).
4. **Clean working tree**: `git status` in the PrintFarmer repo shows no uncommitted changes.

### Step 2: Bump the Version Number

Update `ORCASLICER_VERSION` in **all locations**:

| File (edit these) | Line Pattern |
|---|---|
| `scripts/docker/dockerfiles/Dockerfile.multistage` | `ARG ORCASLICER_VERSION=X.Y.Z` |
| `scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries` | `ARG ORCASLICER_VERSION=X.Y.Z` |
| `scripts/docker/container-versions.conf` | `export ORCASLICER_VERSION="${ORCASLICER_VERSION:-X.Y.Z}"` |
| `scripts/docker/compose-templates/docker-compose.orcaslicer-worker.yml` | `ORCASLICER_VERSION: ${ORCASLICER_VERSION:-X.Y.Z}` |
| `scripts/docker/compose-templates/docker-compose.common.yml` | `ORCASLICER_VERSION: ${ORCASLICER_VERSION:-X.Y.Z}` |

Then **sync the Dockerfile copies** (per the Docker file hierarchy — always edit templates, never root copies):

```bash
cp scripts/docker/dockerfiles/Dockerfile.multistage Dockerfile.multistage
cp scripts/docker/dockerfiles/Dockerfile.multistage dockerfiles/Dockerfile.multistage
```

Also update the server `.env` (during deploy, or ahead of time):
```bash
ssh pi@10.0.0.20 "cd /home/pi/pfarm && sed -i 's/ORCASLICER_VERSION=OLD/ORCASLICER_VERSION=NEW/' .env"
```

### Step 3: Check for New Shared Library Dependencies

OrcaSlicer frequently adds new shared library requirements between versions. The runtime deps are installed in the `slicer-base` Dockerfile stage.

**Current deps** (for 2.3.2):
```dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl wget ca-certificates \
    libgtk-3-0 libglx0 libglib2.0-0 libstdc++6 libopengl0 \
    libglu1-mesa libegl1 \
    libgstreamer1.0-0 libgstreamer-plugins-base1.0-0 libmspack0 \
    libwebkit2gtk-4.1-0 libjavascriptcoregtk-4.1-0 \
    xvfb fuse squashfs-tools file p7zip-full \
    && rm -rf /var/lib/apt/lists/*
```

**How to find missing deps after building:**
```bash
docker exec printfarmer-orcaslicer-worker-1 bash -c \
  "ldd /opt/orcaslicer/bin/orca-slicer 2>&1 | grep 'not found'"
```

If anything shows `not found`, identify the Ubuntu package:
```bash
# Inside the container:
apt-file search libFoo.so.1
# or on the host:
dpkg -S libFoo.so.1
```

Add the package to the `slicer-base` stage in the **template** `Dockerfile.multistage` (`scripts/docker/dockerfiles/`), then sync copies.

### Step 4: Check for CLI Behavior Changes

OrcaSlicer CLI behavior changes between versions. Read the release notes for:

- **New CLI flags** or removed flags
- **Stricter type validation** (e.g., 2.3.2 rejects string-encoded integers)
- **New required parameters**
- **Changed exit codes**

Key file: `src/orcaslicer-worker/Services/OrcaSlicingPipelineService.cs`
- `SettingsDictToNativeJson()` — serializes profile settings to JSON for `--load-settings`
- `SanitizeForCli()` — clamps values the CLI rejects (e.g., speed=0 → speed=1)
- `BuildCommandLine()` — assembles the CLI invocation

### Step 5: Extract Setting Metadata

The metadata drives how filament, machine, and process profile editors render fields — labels, tooltips, units, tab/section layout, and section icons all come from this extraction.

The extraction tool parses two source files:
- `src/libslic3r/PrintConfig.cpp` — setting definitions (label, tooltip, type, min/max, default, mode)
- `src/slic3r/GUI/Tab.cpp` — UI tab/section layout for all three editors

```bash
cd /path/to/PFarm1

python3 tools/extract-orca-metadata.py \
  /path/to/orcaslicer/src \
  --output src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json
```

The tool outputs a summary:
```
Extracted 781 settings from PrintConfig.cpp
Filament layout: 7 tabs, 89 fields
Machine layout: 5 tabs, 106 fields
Process layout: 6 tabs, 318 fields
Icons: 115 section icons
```

#### Review the diff

```bash
git diff src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json
```

Look for:
- **Added fields**: New settings the profile editors need to render
- **Removed fields**: Deprecated settings that can be cleaned up
- **Changed labels/tooltips**: Improved descriptions from upstream
- **New tabs or sections**: Structural layout changes
- **Changed defaults/min/max**: Value constraint updates

#### Validate metadata counts

After extraction, verify the `_meta` section hasn't regressed:

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

| Metric | v2.3.2 Baseline | Check |
|---|---|---|
| `totalSettings` | 781 | Should increase or stay same (never decrease significantly) |
| `filamentSettings` | 108 | Filament-specific settings count |
| `machineSettings` | 113 | Machine-specific settings count |
| `processSettings` | 344 | Process-specific settings count |
| Filament tabs | 7 | Tab layout count |
| Machine tabs | 5 | Tab layout count |
| Process tabs | 6 | Tab layout count |
| Section icons | 115 | SVG icon mappings |

### Step 6: Copy New SVG Icons

OrcaSlicer uses SVG icons for setting sections. Copy any new ones from the source tree:

```bash
ORCA_SRC="/path/to/orcaslicer"
DEST="src/Web/ReactApp/public/icons/orca"

# Parameter tab icons (e.g., speed, infill, support)
cp "$ORCA_SRC/resources/images/param_"*.svg "$DEST/"

# Custom gcode icons
cp "$ORCA_SRC/resources/images/custom-gcode_"*.svg "$DEST/"
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

### Step 7: Update Printer Assets (Images, Bed Models, Textures)

Assets are extracted from the local OrcaSlicer.app installation to the React public folder.

**Run the extraction script:**
```bash
cd /path/to/PFarm1
./scripts/restore-orcaslicer-assets.js
```

This copies from `/Applications/OrcaSlicer.app/Contents/Resources/profiles/` to `src/Web/ReactApp/public/assets/orcaslicer/` and updates `manifest.json`.

**What gets extracted per manufacturer:**
- `{PrinterName}_cover.png` — Printer cover/product images
- `{PrinterName}_texture.{png|svg}` — Print bed texture overlays
- `{PrinterName}_bed.stl` — 3D print bed models for the slicer visualizer
- `{PrinterName}_buildplate_texture.{png|svg}` — Alternate texture naming

**Alternative extraction (bash, also updates manifest):**
```bash
./scripts/extract-orcaslicer-assets.sh
```

**After extraction, verify:**
```bash
# Count assets
find src/Web/ReactApp/public/assets/orcaslicer/ -name "*.png" | wc -l
find src/Web/ReactApp/public/assets/orcaslicer/ -name "*.stl" | wc -l
find src/Web/ReactApp/public/assets/orcaslicer/ -name "*.svg" | wc -l

# Check for new manufacturers
ls src/Web/ReactApp/public/assets/orcaslicer/ | sort
```

### Step 8: Check for New Material Types

OrcaSlicer's filament type list lives in `src/libslic3r/MaterialType.cpp`. If the new version adds material types, update the frontend constant.

```bash
# Extract material types from OrcaSlicer source
grep -oP '"[A-Z][A-Z0-9-]+"' /path/to/orcaslicer/src/libslic3r/MaterialType.cpp | sort -u
```

Compare with `ORCA_FILAMENT_TYPES` in:
```
src/Web/ReactApp/src/features/slicer/components/settings/FilamentProfileEditor.tsx
```

If new types exist, add them to the `ORCA_FILAMENT_TYPES` array.

### Step 9: Frontend Build and Lint

```bash
cd src/Web/ReactApp
npm run build    # Must succeed with 0 TypeScript errors
npm run lint     # Must succeed with 0 ESLint errors
```

### Step 10: Run Unit Tests

```bash
cd src
dotnet test ./tests/Farm.OrcaSlicer.Worker.Tests/ --filter "SettingsSerializationTests" -c Debug
```

The `SettingsSerializationTests` verify the profile JSON serialization round-trip matches what the CLI expects.

### Step 11: Profile Editor Visual Verification

Start the dev servers and verify all three profile editors render correctly:

```bash
# Terminal 1: API
cd src && dotnet run --project ./api/Farm.Web.Api.csproj

# Terminal 2: React
cd src/Web/ReactApp && npm run dev
```

**Check each editor:**

1. **Filament profile editor** — all 7 tabs render with correct labels and section icons
2. **Machine profile editor** — all 5 tabs with correct sections (Basic, G-code, Multimaterial, Extruder, Motion)
3. **Process profile editor** — all 6 tabs including Quality, Strength, Speed, Support, Others
4. Hover over fields to verify tooltips display
5. Check that section icons load (no broken image placeholders)

### Step 12: Profiles (Automatic via AppImage)

Slicer profiles (machine, process, filament, machine_model) are **automatically included** in the Docker image — they come from the AppImage extraction at build time.

Located at `/opt/orcaslicer/resources/profiles/` in the container.

No manual work required here, but verify the count after deploy (Step 13).

### Step 13: Build, Deploy, and End-to-End Test

```bash
# 1. Build the worker image on the server
ssh pi@10.0.0.20 "cd /home/pi/pfarm && docker compose --env-file .env build orcaslicer-worker"

# 2. Deploy
ssh pi@10.0.0.20 "cd /home/pi/pfarm && docker compose --env-file .env up -d orcaslicer-worker"

# 3. Verify version
ssh pi@10.0.0.20 "docker exec printfarmer-orcaslicer-worker-1 \
  /opt/orcaslicer/bin/orca-slicer --help 2>&1 | head -1"
# Should show: OrcaSlicer-X.Y.Z:

# 4. Verify all shared libs resolve
ssh pi@10.0.0.20 "docker exec printfarmer-orcaslicer-worker-1 bash -c \
  'ldd /opt/orcaslicer/bin/orca-slicer 2>&1 | grep not.found || echo OK'"

# 5. Verify profiles loaded
ssh pi@10.0.0.20 "docker exec printfarmer-orcaslicer-worker-1 bash -c \
  'find /opt/orcaslicer/resources/profiles -name \"*.json\" | wc -l'"

# 6. Verify worker registered with correct version
curl -s http://10.0.0.20:5245/api/slicer/workers | python3 -m json.tool
# Should show the worker with version=X.Y.Z and supportedFormats including "3mf"

# 7. End-to-end slice test — submit a job via UI or API and confirm G-code output
```

### Step 14: Commit Everything

```bash
git add -A
git commit -m "feat: upgrade OrcaSlicer to vX.Y.Z

- Bump ORCASLICER_VERSION in Dockerfiles, container-versions.conf, compose templates
- Add/update runtime library dependencies
- Re-extract settings metadata (N settings, M layout fields)
- Update section SVG icons
- Update printer assets (cover images, bed models, textures)
- Update manifest.json with new printer entries
- [Added N new material types]
- [CLI serialization changes]"
```

---

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

**Cause**: Some settings are defined in PrintConfig.cpp but not placed on any tab in Tab.cpp. These are either computed/internal settings or settings managed by specialized UI outside the tab system.

**This is normal.** The gap between total settings and layout fields is expected.

### Missing tooltips

**Symptom**: Fields render without tooltip hover text.

**Cause**: The setting's `->tooltip` is set in a different code path or conditionally.

**Fix**: Search PrintConfig.cpp for the setting key and verify the tooltip is being parsed. The parser may need to handle `def->tooltip = L("...")` on a separate line.

### Icons show as broken images

**Symptom**: Section headers show broken SVG placeholders.

**Cause**: The icon file wasn't copied to `src/Web/ReactApp/public/icons/orca/`.

**Fix**: Run the icon verification script from Step 6 to find missing icons, then copy them from the OrcaSlicer resources directory.

### New mode value not recognized

**Symptom**: Settings have `mode: null` instead of `simple`, `advanced`, or `developer`.

**Cause**: OrcaSlicer added a new mode constant not in the parser's `MODE_MAP`.

**Fix**: Check PrintConfig.cpp for new `comXxx` constants and add them to `MODE_MAP` in `extract-orca-metadata.py`.

### Version mismatch across files

**Symptom**: Docker build uses wrong OrcaSlicer version, or container reports an unexpected version.

**Cause**: Version was not bumped in all locations. The version must match in Dockerfile ARGs, `container-versions.conf`, compose templates, and the server `.env`.

**Fix**: Re-run the grep check from Step 2 and ensure all files reference the same version.

---

## Architecture Reference

```
OrcaSlicer Source                     PrintFarmer
─────────────────                     ────────────────────

src/libslic3r/PrintConfig.cpp    ──┐
  (setting definitions)             │
src/slic3r/GUI/Tab.cpp           ──┤  tools/extract-orca-metadata.py
  (tab/section layout)             │  ────────────────────────────►
src/libslic3r/MaterialType.cpp   ──┘
                                      src/Web/ReactApp/src/features/slicer/generated/
                                        orcaSettingsMetadata.json
                                        └── _meta (counts, generator info)
                                        └── filament.tabs[] → FilamentProfileEditor.tsx
                                        └── machine.tabs[]  → MachineProfileEditor.tsx
                                        └── process.tabs[]  → ProcessProfileEditor.tsx
                                        └── icons{}         → section SVG mappings

resources/images/*.svg           ──►  src/Web/ReactApp/public/icons/orca/*.svg
  (section icons)

AppImage (GitHub Release)        ──►  Docker image: /opt/orcaslicer/
  └── Extracted in                      ├── bin/orca-slicer (CLI binary)
      Dockerfile.multistage             └── resources/profiles/ (JSON profiles)
                                            ├── {Manufacturer}.json (bundle index)
                                            └── {Manufacturer}/
                                                ├── machine/*.json
                                                ├── process/*.json
                                                ├── filament/*.json
                                                ├── *_cover.png
                                                ├── *_texture.{png|svg}
                                                └── *_bed.stl

OrcaSlicer.app (macOS)           ──►  src/Web/ReactApp/public/assets/orcaslicer/
  └── scripts/restore-orcaslicer-       ├── manifest.json (asset registry)
      assets.js                         └── {manufacturer}/*_cover.png, *_bed.stl, ...
```

## Key Files

| Purpose | File |
|---|---|
| **Version configuration** | |
| Dockerfile template (source of truth) | `scripts/docker/dockerfiles/Dockerfile.multistage` |
| Base binaries Dockerfile | `scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries` |
| Container versions | `scripts/docker/container-versions.conf` |
| Compose template (worker) | `scripts/docker/compose-templates/docker-compose.orcaslicer-worker.yml` |
| Compose template (common) | `scripts/docker/compose-templates/docker-compose.common.yml` |
| Dockerfile copy (root) | `Dockerfile.multistage` |
| Dockerfile copy (dockerfiles/) | `dockerfiles/Dockerfile.multistage` |
| Server env | `/home/pi/pfarm/.env` |
| **Metadata & icons** | |
| Metadata extraction tool | `tools/extract-orca-metadata.py` |
| Generated metadata JSON | `src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json` |
| Section icon SVGs | `src/Web/ReactApp/public/icons/orca/` |
| Material types constant | `src/Web/ReactApp/src/features/slicer/components/settings/FilamentProfileEditor.tsx` |
| **Backend** | |
| CLI invocation | `src/orcaslicer-worker/Services/OrcaSlicingPipelineService.cs` |
| Profile loading | `src/orcaslicer-worker/Services/OrcaProfilesService.cs` |
| Profile caching | `src/orcaslicer-worker/Services/CachedOrcaProfilesService.cs` |
| Serialization tests | `src/tests/Farm.OrcaSlicer.Worker.Tests/SettingsSerializationTests.cs` |
| **Assets** | |
| Asset extraction (Node.js) | `scripts/restore-orcaslicer-assets.js` |
| Asset extraction (bash) | `scripts/extract-orcaslicer-assets.sh` |
| Asset manifest | `src/Web/ReactApp/public/assets/orcaslicer/manifest.json` |

## Version History

| Version | Profile Count | Settings | Key Changes |
|---|---|---|---|
| 2.3.1 | ~9200 | ~750 | Initial version; CLI segfault in headless mode (calc_exclude_triangles) |
| 2.3.2 | ~9961 | 781 | Fixed CLI segfault; stricter type validation (requires native JSON types for integers); new deps: libglu1-mesa, libegl1, libgstreamer, libmspack0; 6+ new manufacturers |
