---
name: orcaslicer-upgrade
description: Upgrade OrcaSlicer version across the PrintFarmer stack. Use when upgrading OrcaSlicer binary, profiles, or printer assets. Covers Dockerfiles, runtime deps, profile extraction, asset extraction, and end-to-end validation.
---

# OrcaSlicer Version Upgrade Skill

Use this skill when upgrading OrcaSlicer to a new version. Every upgrade touches multiple layers — binary, dependencies, profiles, and frontend assets. Missing any layer causes subtle failures.

## Prerequisites

- **macOS**: OrcaSlicer.app installed at `/Applications/OrcaSlicer.app` (download from [OrcaSlicer releases](https://github.com/OrcaSlicer/OrcaSlicer/releases))
- **Server**: SSH access to deployment server (pi@10.0.0.20)
- Confirm the new version is a **stable release** (not beta/rc)

## Upgrade Checklist — All Steps Required

### Step 1: Update the Version Number

Update `ORCASLICER_VERSION` in **all four files**:

| File | Line Pattern |
|------|-------------|
| `Dockerfile.multistage` | `ARG ORCASLICER_VERSION=X.Y.Z` |
| `dockerfiles/Dockerfile.multistage` | `ARG ORCASLICER_VERSION=X.Y.Z` |
| `scripts/docker/dockerfiles/Dockerfile.multistage` | `ARG ORCASLICER_VERSION=X.Y.Z` |
| `scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries` | `ARG ORCASLICER_VERSION=X.Y.Z` |

Also update the server `.env`:
```bash
ssh pi@10.0.0.20 "cd /home/pi/pfarm && sed -i 's/ORCASLICER_VERSION=OLD/ORCASLICER_VERSION=NEW/' .env"
```

### Step 2: Check for New Shared Library Dependencies

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
# Build the worker image, deploy it, then check:
docker exec printfarmer-orcaslicer-worker-1 bash -c \
  "ldd /opt/orcaslicer/bin/orca-slicer 2>&1 | grep 'not found'"
```

If anything shows `not found`, identify the Ubuntu package providing that `.so`:
```bash
# Inside the container:
apt-file search libFoo.so.1
# or on the host:
dpkg -S libFoo.so.1
```

Add the package to the `slicer-base` stage in **all three** `Dockerfile.multistage` files.

### Step 3: Check for CLI Behavior Changes

OrcaSlicer CLI behavior changes between versions. Read the release notes for:

- **New CLI flags** or removed flags
- **Stricter type validation** (e.g., 2.3.2 rejects string-encoded integers)
- **New required parameters**
- **Changed exit codes**

Key file: `src/orcaslicer-worker/Services/OrcaSlicingPipelineService.cs`
- `SettingsDictToNativeJson()` — serializes profile settings to JSON for `--load-settings`
- `SanitizeForCli()` — clamps values the CLI rejects (e.g., speed=0 → speed=1)
- `BuildCommandLine()` — assembles the CLI invocation

### Step 4: Update Printer Assets (Images, Bed Models, Textures)

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

### Step 5: Profiles (Automatic via AppImage)

Slicer profiles (machine, process, filament, machine_model) are **automatically included** in the Docker image — they come from the AppImage extraction at build time.

Located at `/opt/orcaslicer/resources/profiles/` in the container.

**Verify profile count after build:**
```bash
docker exec printfarmer-orcaslicer-worker-1 bash -c \
  "find /opt/orcaslicer/resources/profiles -name '*.json' | wc -l"
```

Expected: increases with each version (2.3.1 had ~9200, 2.3.2 has ~9961).

### Step 6: Build, Deploy, and Test

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

# 6. End-to-end slice test (submit a job via UI or API)
```

### Step 7: Run Unit Tests

```bash
cd src
dotnet test ./tests/Farm.OrcaSlicer.Worker.Tests/ --filter "SettingsSerializationTests" -c Debug
```

The `SettingsSerializationTests` verify the profile JSON serialization round-trip matches what the CLI expects.

### Step 8: Commit Everything

```bash
git add -A
git commit -m "feat: upgrade OrcaSlicer to vX.Y.Z

- Bump ORCASLICER_VERSION in all Dockerfiles
- Add/update runtime library dependencies
- Update printer assets (cover images, bed models, textures)
- Update manifest.json with new printer entries
- [any CLI serialization changes]"
```

## Architecture Reference

```
OrcaSlicer resources flow:

AppImage (GitHub Release)
  └── Extracted in Dockerfile.multistage → /opt/orcaslicer/
      ├── bin/orca-slicer                  ← CLI binary
      └── resources/profiles/              ← JSON profiles (auto-included)
          ├── {Manufacturer}.json          ← Bundle index
          └── {Manufacturer}/              ← Profile directory
              ├── machine/*.json
              ├── process/*.json
              ├── filament/*.json
              ├── *_cover.png              ← Printer images
              ├── *_texture.{png|svg}      ← Bed textures
              └── *_bed.stl                ← 3D bed models

OrcaSlicer.app (local macOS)
  └── Extracted by scripts/ → src/Web/ReactApp/public/assets/orcaslicer/
      ├── manifest.json                    ← Asset registry
      └── {manufacturer}/                  ← Per-manufacturer directory
          ├── *_cover.png
          ├── *_texture.{png|svg}
          └── *_bed.stl
```

## Key Files

| Purpose | File |
|---------|------|
| Dockerfile (production, root) | `Dockerfile.multistage` |
| Dockerfile (template) | `scripts/docker/dockerfiles/Dockerfile.multistage` |
| Dockerfile (dockerfiles copy) | `dockerfiles/Dockerfile.multistage` |
| Base binaries Dockerfile | `scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries` |
| CLI invocation | `src/orcaslicer-worker/Services/OrcaSlicingPipelineService.cs` |
| Profile loading | `src/orcaslicer-worker/Services/OrcaProfilesService.cs` |
| Profile caching | `src/orcaslicer-worker/Services/CachedOrcaProfilesService.cs` |
| Serialization tests | `src/tests/Farm.OrcaSlicer.Worker.Tests/SettingsSerializationTests.cs` |
| Asset extraction (bash) | `scripts/extract-orcaslicer-assets.sh` |
| Asset extraction (node) | `scripts/restore-orcaslicer-assets.js` |
| Asset manifest | `src/Web/ReactApp/public/assets/orcaslicer/manifest.json` |
| Server env | `/home/pi/pfarm/.env` (`ORCASLICER_VERSION=X.Y.Z`) |

## Version History

| Version | Profile Count | Key Changes |
|---------|--------------|-------------|
| 2.3.1 | ~9200 | Initial version; CLI segfault in headless mode (calc_exclude_triangles) |
| 2.3.2 | ~9961 | Fixed CLI segfault; stricter type validation (requires native JSON types for integers); new deps: libglu1-mesa, libegl1, libgstreamer, libmspack0; 6+ new manufacturers |
