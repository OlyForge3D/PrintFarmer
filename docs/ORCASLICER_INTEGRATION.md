# OrcaSlicer Integration - Comprehensive Reference

> **Last Updated**: January 30, 2026  
> **Status**: Incomplete Implementation (Components Built, End-to-End NOT Functional)  
> **Purpose**: Preserve all implementation details for future reference

This document consolidates ALL OrcaSlicer integration documentation into a single comprehensive reference. Use this to understand what was built, what's missing, and how to complete the integration.

---

## Table of Contents

1. [Current Implementation Status](#current-implementation-status)
2. [Architecture Overview](#architecture-overview)
3. [Docker Build System](#docker-build-system)
4. [Profile Hierarchy & Loading](#profile-hierarchy--loading)
5. [Profile Inheritance System](#profile-inheritance-system)
6. [Expression Parser](#expression-parser)
7. [Slicing Pipeline](#slicing-pipeline)
8. [Frontend UI Components](#frontend-ui-components)
9. [User Bundle Import Wizard](#user-bundle-import-wizard)
10. [3D Bed Visualization](#3d-bed-visualization)
11. [Remaining Work](#remaining-work)
12. [File Reference](#file-reference)
13. [Debugging & Testing](#debugging--testing)

---

## Current Implementation Status

### ⚠️ CRITICAL: End-to-End Slicing Does NOT Work

While many components are implemented, the OrcaSlicer integration is **NOT production ready** and has **NOT been tested end-to-end**. Individual components were built but never integrated into a working slicing flow.

### Backend Components

| Component | State | Notes |
|-----------|-------|-------|
| OrcaSlicer Worker Service | 🔨 Built | Container builds, services scaffolded |
| Profile Loading | 🔨 Built | `OrcaProfilesService.cs` loads bundled JSON |
| Profile Caching | 🔨 Built | `CachedOrcaProfilesService.cs` SQLite cache |
| Expression Parser | 🔨 Built | 98.2% condition coverage |
| Slicing Pipeline | 🔨 Built | `OrcaSlicingPipelineService.cs` |
| Worker Registration | 🔨 Built | SignalR heartbeat to main API |
| Redis Queue Consumer | 🔨 Built | `QueueConsumerService.cs` polls Redis |
| Binary Docker Optimization | 🔨 Built | Layer caching for faster rebuilds |
| Profile Preview Endpoint | 🔨 Built | `POST /api/slicer/profiles/import/orca/preview` |
| **Profile Import Endpoint** | ❌ Missing | `POST /api/slicer/profiles/import/orca` NOT implemented |
| **End-to-End Slicing** | ❌ Untested | Full job submission → G-code output never validated |

### Frontend Components

| Component | State | Notes |
|-----------|-------|-------|
| NewSliceJobPage | 🔨 Built | Profile selection, model picker, worker selector |
| ProfileEditorModal | 🔨 Built | Machine/filament profile editing |
| SlicerSettingsPanel | 🔨 Built | OrcaSlicer-style compact settings UI |
| OrcaImportWizard | 🔨 Built | File upload, preview works - import fails |
| SlicerContext | 🔨 Built | Hides UI when no slicer workers registered |
| 3D Model Viewer | 🔨 Built | PNG bed textures partially working |
| **Bed STL Loading** | ❌ Missing | OrcaSlicer `bed_model` STL files not loaded |
| **End-to-End Job Submission** | ❌ Untested | Submit job → receive G-code never validated |

---

## Architecture Overview

### System Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                        PrintFarmer System                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌─────────────────┐     ┌─────────────────┐                     │
│  │   React Client  │────▶│  Main API       │                     │
│  │   (port 3000)   │     │  (port 5245)    │                     │
│  └────────┬────────┘     └────────┬────────┘                     │
│           │                       │                              │
│           │ SignalR               │ HTTP + Redis Queue           │
│           ▼                       ▼                              │
│  ┌─────────────────────────────────────────────────────┐         │
│  │              OrcaSlicer Worker (Docker)              │         │
│  │              (port 8080 inside container)            │         │
│  │                                                      │         │
│  │  ┌──────────────────┐  ┌────────────────────┐        │         │
│  │  │OrcaProfilesService│  │OrcaSlicingPipeline │        │         │
│  │  │  - Load profiles  │  │  - Execute slices  │        │         │
│  │  │  - Parse conditions│  │  - Generate G-code │        │         │
│  │  └──────────────────┘  └────────────────────┘        │         │
│  │                                                      │         │
│  │  /opt/orcaslicer/                                    │         │
│  │    ├── OrcaSlicer (extracted AppImage binary)        │         │
│  │    └── resources/profiles/{manufacturer}.json        │         │
│  └─────────────────────────────────────────────────────┘         │
└─────────────────────────────────────────────────────────────────┘
```

### Intended Data Flow (NOT VALIDATED)

```
1. User selects model + profiles in NewSliceJobPage
2. Submit creates DistributedSlicingJob in database
3. Job queued to Redis
4. OrcaSlicer worker polls Redis, claims job
5. Worker downloads STL from storage
6. Worker generates profile JSON files from job.Profile
7. Worker executes: orcaslicer --slice 0 --load-settings "machine.json;process.json" --load-filaments "filament.json"
8. Worker uploads G-code to storage
9. Worker reports completion via HTTP callback to main API
10. Main API broadcasts SignalR notification
11. UI receives notification, refreshes job status
12. User downloads G-code
```

---

## Docker Build System

### Problem Solved

Original builds downloaded 200MB+ OrcaSlicer AppImage on every rebuild, taking 8-12 minutes. Binary layer caching reduces code-only rebuilds to 2-3 minutes.

### Build Architecture

```
┌────────────────────────────────────────┐
│ Dockerfile.base-orcaslicer-binaries    │
│   - Downloads AppImage from GitHub     │
│   - Extracts to /orcaslicer-dist       │
│   - Creates stub if download fails     │
└──────────────────┬─────────────────────┘
                   │
                   ▼ COPY --from=orcaslicer-binaries
┌────────────────────────────────────────┐
│ Dockerfile.multistage (orcaslicer-worker target) │
│   - Copies pre-extracted binaries      │
│   - Builds .NET worker service         │
│   - Fast rebuild (2-3 min)             │
└────────────────────────────────────────┘
```

### Binary Layer Dockerfile

**Location**: `scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries`

```dockerfile
FROM ubuntu:24.04 AS orcaslicer-binaries-base
ARG ORCASLICER_VERSION=2.3.1
ARG GITHUB_TOKEN

# Install extraction tools
RUN apt-get update && apt-get install -y \
    curl ca-certificates jq p7zip-full squashfs-tools libarchive-tools file wget

# Download and extract OrcaSlicer
# - Discovers correct URL from GitHub API
# - Multiple extraction methods: unsquashfs, --appimage-extract, 7z, bsdtar
# - Creates stub binary if all methods fail (for CI)

RUN set -e; \
    # ... download logic with multiple fallback URLs ... \
    # ... extraction logic with multiple fallback methods ... \
    # Creates /orcaslicer-dist/opt/orcaslicer/ with extracted binary

FROM scratch AS orcaslicer-binaries
COPY --from=orcaslicer-binaries-base /orcaslicer-dist /orcaslicer-dist
LABEL prebuild="true" purpose="orcaslicer-binaries" version="${ORCASLICER_VERSION}"
```

### Build Commands

```bash
# Option 1: Build script (recommended)
./scripts/build-orcaslicer-optimized.sh

# With specific version
ORCASLICER_VERSION=2.3.1 ./scripts/build-orcaslicer-optimized.sh

# With GitHub token (avoid rate limits)
GITHUB_TOKEN=your_token ./scripts/build-orcaslicer-optimized.sh

# Option 2: Manual two-stage build
# Step 1: Build binary layer (slow first time, cached after)
docker build -f scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries \
  -t orcaslicer-binaries:2.3.1 \
  --build-arg ORCASLICER_VERSION=2.3.1 \
  .

# Step 2: Build worker (fast, uses cached binaries)
docker build -f Dockerfile.multistage \
  --target orcaslicer-worker \
  -t printfarmer-orcaslicer-worker \
  --build-arg ORCASLICER_VERSION=2.3.1 \
  .

# Option 3: Docker Compose
docker compose --profile orca-binaries build orcaslicer-binaries
docker compose --profile orca build orcaslicer-worker
```

### Build Arguments

| Argument | Default | Description |
|----------|---------|-------------|
| `ORCASLICER_VERSION` | 2.3.1 | OrcaSlicer release version to download |
| `ORCASLICER_URL` | (auto-discovered) | Override download URL |
| `ALLOW_STUB` | true | Create stub binary if download fails |
| `GITHUB_TOKEN` | (optional) | Avoid GitHub API rate limits |
| `CACHE_BUST` | latest | Force layer rebuild |

### Performance

| Scenario | Before | After |
|----------|--------|-------|
| First build | 8-12 min | 8-12 min |
| Code-only rebuild | 8-12 min | 2-3 min |
| CI/CD pipeline | 8-12 min/build | 2-3 min after cache |

---

## Profile Hierarchy & Loading

### 4-List Bundle Structure

Each manufacturer has a JSON bundle at `/opt/orcaslicer/resources/profiles/{manufacturer}.json`:

```json
{
  "machine_model_list": [
    { "name": "Prusa CORE One", "sub_path": "machine/Prusa CORE One.json" }
  ],
  "machine_list": [
    { "name": "Prusa CORE One 0.4 nozzle", "sub_path": "machine/Prusa CORE One 0.4 nozzle.json" },
    { "name": "Prusa CORE One 0.6 nozzle", "sub_path": "machine/Prusa CORE One 0.6 nozzle.json" }
  ],
  "filament_list": [
    { 
      "name": "PLA @MATERIAL_PLA", 
      "sub_path": "filament/PLA @MATERIAL_PLA.json",
      "compatible_printers_condition": "printer_notes=~/.*PRINTER_MODEL_COREONE.*/"
    }
  ],
  "process_list": [
    {
      "name": "0.20mm Standard @NOZZLE_0.4",
      "sub_path": "process/0.20mm Standard @NOZZLE_0.4.json",
      "compatible_printers": ["Prusa CORE One 0.4 nozzle", "Prusa MK4S 0.4 nozzle"]
    }
  ]
}
```

### List Purposes

| List | Purpose | Example Names |
|------|---------|---------------|
| `machine_model_list` | Base printer models | "Prusa CORE One", "Bambu Lab X1" |
| `machine_list` | Variants with nozzle sizes | "Prusa CORE One 0.4 nozzle" |
| `filament_list` | Material profiles | "PLA @MATERIAL_PLA", "PETG Generic" |
| `process_list` | Quality/speed profiles | "0.20mm Standard", "0.08mm Fine" |

### Key Relationships

- **machine_list** variants are what filament/process profiles reference
- **compatible_printers** array contains exact machine_list names
- **compatible_printers_condition** is an expression evaluated against machine properties

### Profile Counts (OrcaSlicer 2.3.x)

- ~200 machine profiles (variants across manufacturers)
- ~2000 filament profiles
- ~2200 process profiles
- **98.2% coverage** on condition resolution (641/654 profiles)

---

## Profile Inheritance System

### Overview

OrcaSlicer uses inheritance to reduce duplication. Child profiles inherit all settings from parent profiles and can override specific values.

### The `instantiation` Flag (CRITICAL)

This field determines which profiles should be shown to users:

| Value | Meaning | Action |
|-------|---------|--------|
| `"true"` | User-selectable profile | ✅ Import to database |
| `"false"` | Template/framework profile | ❌ Skip, don't import |

### Inheritance Chain Example

```
fdm_filament_common.json (instantiation=false) ← System base
       ↑ inherits
fdm_filament_pla.json (instantiation=false) ← PLA template
       ↑ inherits  
Prusa Generic PLA.json ← Family profile
       ↑ inherits
Prusa Generic PLA @MK4S.json ← Printer-specific
       ↑ inherits
Prusa Generic PLA @MK4S 0.6.json (instantiation=true) ← User-selectable ✅
       ↑ inherits
Prusa Generic PLA @MK4S 0.8.json (instantiation=true) ← User-selectable ✅
```

### Full Resolution Algorithm

When importing profiles, inheritance MUST be pre-resolved:

```python
def resolve_profile(profile_path):
    profile = load_json(profile_path)
    
    if "inherits" in profile:
        parent_path = find_parent_profile(profile["inherits"])
        parent = resolve_profile(parent_path)  # Recursive
        profile = deep_merge(parent, profile)  # Child overrides parent
    
    return profile
```

### Merge Rules

| Type | Behavior |
|------|----------|
| Scalar | Child value replaces parent completely |
| Array | Child array replaces parent array entirely |
| Object | Deep merge, child properties override |
| null/undefined | Inherited from parent |

### Why Pre-Resolve?

PrintFarmer stores **fully-resolved** profiles because:
1. Parent profiles (instantiation=false) are NOT in database
2. UI queries expect complete settings on each profile
3. Runtime inheritance lookup would fail

### Profile Counts Example (Prusa)

| Category | Count | Import? |
|----------|-------|---------|
| Base system profiles | 13 | ❌ No (instantiation=false) |
| Named user profiles | 259 | ✅ Yes (instantiation=true) |
| **Total** | 272 | **259 imported** |

---

## Expression Parser

### Purpose

Evaluates `compatible_printers_condition` expressions to determine which machines a profile supports.

### Supported Syntax

```
# Regex matching (case-insensitive)
printer_notes=~/.*PRINTER_MODEL_COREONE.*/

# Equality with array indexing
nozzle_diameter[0]==0.4

# Float tolerance (±0.001mm)
nozzle_diameter[0]==0.8  # Matches 0.799-0.801

# Logical AND (higher precedence)
condition1 and condition2

# Logical OR (lower precedence)  
condition1 or condition2

# Complex example
printer_notes=~/.*PRINTER_MODEL_COREONE.*/ and nozzle_diameter[0]==0.4 and printer_notes=~/.*HF_NOZZLE.*/
```

### Evaluation Strategy

Conditions are evaluated **immediately at profile load time**:

1. Load all machine profiles first → cache by manufacturer
2. For each filament/process profile:
   - Retrieve machine cache for profile's manufacturer
   - Parse `compatible_printers_condition` expression
   - Evaluate against each cached machine
   - Collect matching machine names
3. Store matched names in `CompatiblePrinters` array
4. Store raw condition in `CompatiblePrintersCondition` (marked `[JsonIgnore]`)

### Implementation

**File**: `src/orcaslicer-worker/Services/PrinterExpressionParser.cs`

```csharp
public class PrinterExpressionParser
{
    // Recursive descent parser
    public bool Evaluate(string expression, MachineProfile machine)
    {
        // Tokenize: printer_notes=~/pattern/ → REGEX_MATCH token
        // Parse: handles and/or precedence
        // Evaluate: regex matching, array indexing, float comparison
    }
}
```

### Coverage Statistics

- **Total profiles with conditions**: 654
- **Successfully resolved**: 641 (98.2%)
- **Unresolved**: 13 (edge cases with complex expressions)

---

## Slicing Pipeline

### Overview

The slicing pipeline downloads models, generates config files, executes OrcaSlicer, and uploads results.

### Pipeline Service

**File**: `src/orcaslicer-worker/Services/OrcaSlicingPipelineService.cs`

```csharp
public class OrcaSlicingPipelineService : ISlicingPipelineService
{
    public async Task<SlicingResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken ct)
    {
        // 1. Create working directory
        string jobWorkDir = Path.Combine(_workingDirectory, job.Id.ToString());
        
        // 2. Download STL file (10% progress)
        string stlPath = await FetchStlFileAsync(job, jobWorkDir, ct);
        
        // 3. Generate profile JSON files (20% progress)
        var profilePaths = await GenerateProfileJsonFilesAsync(job.Profile, jobWorkDir, ct);
        
        // 4. Execute OrcaSlicer (30-80% progress)
        string gcodePath = await RunOrcaSlicerAsync(stlPath, profilePaths, jobWorkDir, job, ct);
        
        // 5. Extract metadata (80% progress)
        var metadata = await ExtractGcodeMetadataAsync(gcodePath, ct);
        
        // 6. Upload G-code (90% progress)
        string gcodeUrl = await UploadGcodeAsync(gcodePath, job, ct);
        
        // 7. Return result (100% progress)
        return new SlicingResult { ResultFileUrl = gcodeUrl, ... };
    }
}
```

### OrcaSlicer Command Line

```bash
orcaslicer --slice 0 \
  --load-settings "machine.json;process.json" \
  --load-filaments "filament.json" \
  --allow-newer-file \
  --outputdir "/tmp/slice-{jobId}/output" \
  "/tmp/slice-{jobId}/model.stl"
```

### Worker Program Structure

**File**: `src/orcaslicer-worker/Program.cs`

```csharp
// Key services
builder.Services.AddSingleton<IWorkerStateService, WorkerStateService>();
builder.Services.AddSingleton<IOrcaBinaryDetector, OrcaBinaryDetector>();
builder.Services.AddScoped<ISlicingPipelineService, OrcaSlicingPipelineService>();
builder.Services.AddSingleton<ISlicerProfilesService>(sp => sp.GetRequiredService<CachedOrcaProfilesService>());

// Background services
builder.Services.AddHostedService<GracefulShutdownService>();
builder.Services.AddHostedService<QueueConsumerService>();      // Polls Redis
builder.Services.AddHostedService<RegistrationBackgroundService>(); // Heartbeat to API

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<WorkerLivenessHealthCheck>("liveness")
    .AddCheck<WorkerReadinessHealthCheck>("readiness")
    .AddCheck<OrcaBinaryHealthCheck>("orca_binary");
```

---

## Frontend UI Components

### NewSliceJobPage

**File**: `src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx`

Features:
- Model selection from library or upload
- Slicer type selection (OrcaSlicer, PrusaSlicer)
- Profile selectors (Machine, Filament, Process)
- Process preset quick buttons (Draft, Standard, Fine)
- 6 tabbed settings categories with OrcaSlicer-style compact inputs
- Worker selection with capability filtering
- 3D model preview with bed visualization

### SlicerSettingsPanel

**File**: `src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx`

Compact OrcaSlicer-style settings editor with:
- `CompactSettingRow`: Label on left, small input on right
- `SettingSection`: Grouped settings with icon header
- Categories: Quality, Strength, Speed, Support, Material, Other

### SlicerContext

**File**: `src/Web/ReactApp/src/contexts/SlicerContext.tsx`

Tracks slicer availability:
- `settingEnabled`: Is slicing enabled in app settings?
- `hasWorkers`: Are any slicer workers registered?
- `isSlicerAvailable`: Both conditions must be true

When slicer unavailable, hides:
- Navigation items (Slice, Slicer Profiles)
- Models tab in Files page
- Slicing settings in Settings applet

### ProfileEditorModal

**File**: `src/Web/ReactApp/src/features/slicer/components/ProfileEditorModal.tsx`

Edits machine and filament profiles (process edited inline in settings panel).

---

## User Bundle Import Wizard

### Overview

Allows users to import their own OrcaSlicer config bundle JSON files.

### Components

**Wizard UI**: `src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/ui/components/OrcaImportWizard.tsx`

```
Step 1: Upload
  - File input with drag-drop zone
  - Validates JSON format
  - Triggers preview mutation

Step 2: Preview & Selection  
  - Shows preset counts (printers, filaments, processes)
  - Checkbox selection per preset category
  - "Select All" per category
  - Preview metadata (bed size, temperatures, layer height)

Step 3: Import (❌ BROKEN - endpoint missing)
  - Would persist selected presets to database
  - Show import statistics
  - Navigate to profile browser
```

### API Endpoints

| Endpoint | State | Purpose |
|----------|-------|---------|
| `POST /api/slicer/profiles/import/orca/preview` | ✅ Works | Parse bundle, return metadata |
| `POST /api/slicer/profiles/import/orca` | ❌ Missing | Persist selected profiles to database |
| `POST /api/slicer/profiles/import/orca/map` | ❌ Missing | Fuzzy match profiles to catalog |

### What Works Today

```bash
# Preview works - parses bundle and returns preset counts
curl -X POST http://localhost:5245/api/slicer/profiles/import/orca/preview \
  -H "Content-Type: application/json" \
  -d '{"bundleJson": "{\"printer\": [...], \"filament\": [...], \"process\": [...]}"}'

# Response:
{
  "printers": [{ "name": "...", "manufacturer": "...", "bedWidth": 256, ... }],
  "filaments": [{ "name": "...", "material": "PLA", "nozzleTemp": 210, ... }],
  "processes": [{ "name": "...", "layerHeight": 0.2, "infill": 15, ... }]
}
```

### What's Missing

```bash
# Import endpoint NOT implemented - will 404
curl -X POST http://localhost:5245/api/slicer/profiles/import/orca \
  -H "Content-Type: application/json" \
  -d '{
    "bundleJson": "...",
    "selectedPrinters": ["Bambu Lab X1 Carbon"],
    "selectedFilaments": ["Generic PLA"],
    "selectedProcesses": ["0.20mm Standard"]
  }'
```

### Wizard Flow Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│ STEP 1: UPLOAD                                                   │
│  ┌─────────────────────────────────────────┐                     │
│  │   📄 Click to select bundle file        │                     │
│  │   Supports OrcaSlicer config bundle JSON│                     │
│  └─────────────────────────────────────────┘                     │
│  [Preview Bundle] ──► POST /api/.../preview                      │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│ STEP 2: PREVIEW & SELECTION                                      │
│  Summary: [8 Printers] [12 Filaments] [6 Processes]              │
│                                                                  │
│  ☑ Printer Presets                                               │
│    ☑ Bambu Lab X1 Carbon (256x256x256mm, 0.4mm nozzle)           │
│    ☑ Prusa MK4 (250x210x220mm, 0.4mm nozzle)                     │
│                                                                  │
│  ☑ Filament Presets                                              │
│    ☑ Generic PLA (210°C / 60°C)                                  │
│    ☑ PolyLite PETG (240°C / 80°C)                                │
│                                                                  │
│  [← Back]                              [Import Selected →]       │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼ POST /api/.../import (❌ 404 ERROR)
┌──────────────────────────────────────────────────────────────────┐
│ STEP 3: COMPLETION (never reached due to missing endpoint)       │
└──────────────────────────────────────────────────────────────────┘
```

---

## 3D Bed Visualization

### Current State

**File**: `src/Web/ReactApp/src/features/models3d/components/3d/ModelViewer3D.tsx`

| Feature | State | Notes |
|---------|-------|-------|
| Plain bed (solid color) | ✅ Works | Default blue bed surface |
| PNG bed texture | ✅ Works | Loads via `bedTextureUrl` prop |
| SVG bed texture | 🔨 Scaffolded | Overlay logic exists, not fully tested |
| Bed STL model | ❌ Missing | OrcaSlicer `bed_model` field not used |

### Props

```typescript
interface ModelViewerProps {
  modelUrl: string;
  fileType: 'stl' | '3mf' | 'obj' | 'ply';
  bedDimensions?: {
    width: number;   // X axis (mm)
    depth: number;   // Y axis (mm)
    height?: number; // Z axis (mm)
  };
  bedTextureUrl?: string;        // URL to PNG/SVG texture
  bedTextureFormat?: 'svg' | 'png';
}
```

### OrcaSlicer Assets Available

Inside OrcaSlicer container at `/opt/orcaslicer/resources/`:

```
profiles/{manufacturer}/
├── *.json                    # Profile JSONs
├── {printer}_bed.stl         # 3D bed geometry ← NOT LOADED
├── {printer}_texture.svg     # Bed surface texture
├── {printer}_texture.png     # Bed surface texture (alternate)
└── {printer}_cover.png       # Printer preview image
```

### Example Machine Profile with Assets

```json
{
  "name": "Prusa MK4S 0.4 nozzle",
  "bed_model": "prusa_mk4s_bed.stl",
  "bed_texture": "prusa_mk4s_texture.svg",
  "printable_area": [[0,0], [250,0], [250,210], [0,210]],
  "bed_shape": [[0,0], [250,0], [250,210], [0,210]]
}
```

### Missing Implementation

To load bed STL models:

1. Parse `bed_model` field from machine profile
2. Add API endpoint to serve STL files from worker container
3. Add STL loader in `ModelViewer3D.tsx` (three-stdlib `STLLoader`)
4. Position bed model at Z=0 (print surface on top)
5. Handle case where bed_model is missing (fall back to plain bed)

---

## Remaining Work

### Phase 1: Complete Bundle Import (High Priority)

**Goal**: Allow users to import their own OrcaSlicer config bundles

**Tasks**:
1. Implement `POST /api/slicer/profiles/import/orca` endpoint in `ProfilesController.cs`
2. Create import service to persist profiles to database
3. Implement inheritance resolution during import
4. Add duplicate detection (same name + manufacturer)
5. Test wizard end-to-end

### Phase 2: End-to-End Slicing (Critical)

**Goal**: Validate complete slicing workflow

**Tasks**:
1. Deploy OrcaSlicer worker with valid binary (not stub)
2. Configure Redis for job queue
3. Submit test job from UI
4. Debug any pipeline failures
5. Verify G-code output and upload

### Phase 3: Bed Visualization (Medium Priority)

**Goal**: Show accurate printer bed in 3D viewer

**Tasks**:
1. Parse `bed_model` field from machine profiles
2. Create API endpoint to serve STL files from worker
3. Add STL loader for bed model in viewer
4. Handle missing bed models gracefully

### Phase 4: Asset Integration (Medium Priority)

**Goal**: Use OrcaSlicer printer images across UI

**Tasks**:
1. Create asset serving endpoint on worker
2. Generate asset manifest per manufacturer
3. Show printer cover images in printer cards
4. Cache assets for performance

### Phase 5: Profile Authoring (Low Priority)

**Goal**: Create and edit profiles within PrintFarmer

**Tasks**:
- Visual profile editor with validation
- Show inherited vs overridden values differently
- Export to OrcaSlicer format

### Phase 6: Advanced Features (Future)

- Print time estimation display
- Filament weight/cost calculation
- Profile conflict detection
- Profile sharing between users

---

## File Reference

### Backend (C#)

| File | Purpose |
|------|---------|
| `src/orcaslicer-worker/Program.cs` | Worker entry point, DI setup |
| `src/orcaslicer-worker/Services/OrcaSlicingPipelineService.cs` | Main slicing logic |
| `src/orcaslicer-worker/Services/OrcaProfilesService.cs` | Profile loading from JSON |
| `src/orcaslicer-worker/Services/CachedOrcaProfilesService.cs` | SQLite profile cache |
| `src/orcaslicer-worker/Services/PrinterExpressionParser.cs` | Condition evaluation |
| `src/orcaslicer-worker/Services/QueueConsumerService.cs` | Redis queue polling |
| `src/orcaslicer-worker/Services/RegistrationBackgroundService.cs` | API heartbeat |
| `src/api/Controllers/Slicing/ProfilesController.cs` | Profile API endpoints |
| `src/api/Services/Slicing/OrcaBundleParsingService.cs` | Bundle parsing |

### Frontend (TypeScript/React)

| File | Purpose |
|------|---------|
| `src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx` | Job submission UI |
| `src/Web/ReactApp/src/features/slicer/components/ProfileEditorModal.tsx` | Profile editing |
| `src/Web/ReactApp/src/features/slicer/components/settings/SlicerSettingsPanel.tsx` | Settings UI |
| `src/Web/ReactApp/src/features/models3d/components/3d/ModelViewer3D.tsx` | 3D viewer |
| `src/Web/ReactApp/src/contexts/SlicerContext.tsx` | Slicer availability state |
| `src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/ui/components/OrcaImportWizard.tsx` | Import wizard |

### Docker

| File | Purpose |
|------|---------|
| `scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries` | Binary layer |
| `Dockerfile.multistage` | Worker build (orcaslicer-worker target) |
| `scripts/build-orcaslicer-optimized.sh` | Build script |
| `docker-compose.yml` | Service definitions |

---

## Debugging & Testing

### Verify Worker Container

```bash
# Check if worker is running
docker ps | grep orcaslicer

# Check worker logs
docker logs printfarmer-orcaslicer-worker

# Check if OrcaSlicer binary exists (not stub)
docker exec printfarmer-orcaslicer-worker ls -la /usr/local/bin/orcaslicer
docker exec printfarmer-orcaslicer-worker /usr/local/bin/orcaslicer --version

# Check health endpoints
docker exec printfarmer-orcaslicer-worker curl http://localhost:8080/healthz
docker exec printfarmer-orcaslicer-worker curl http://localhost:8080/health/ready
```

### Verify Profile Loading

```bash
# Get all profiles (from inside container)
docker exec printfarmer-orcaslicer-worker curl http://localhost:8080/api/profiles | jq '.byHierarchy | keys'

# Check specific manufacturer
docker exec printfarmer-orcaslicer-worker curl http://localhost:8080/api/profiles | jq '.byHierarchy.Prusa.models | keys'

# Check compatible_printers resolution
docker exec printfarmer-orcaslicer-worker curl http://localhost:8080/api/profiles | jq '.filamentProfiles."Unknown"[0].compatiblePrinters'
```

### Test Bundle Preview

```bash
# From outside container (main API)
curl -X POST http://localhost:5245/api/slicer/profiles/import/orca/preview \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"bundleJson": "{\"printer\": [], \"filament\": [], \"process\": []}"}'
```

### Integration Tests

```bash
# Run OrcaSlicer-specific tests
cd src
dotnet test --filter "FullyQualifiedName~OrcaSlicer"

# Test files:
# - src/tests/Farm.Web.Api.Tests/Slicing/OrcaBundlePreviewTests.cs
# - src/tests/Farm.Web.Api.Tests/Slicing/OrcaMappingAccuracyTests.cs
# - src/tests/Farm.Web.Api.Tests/Slicing/OrcaBundleIntegrationTests.cs
```

---

## Conclusion

OrcaSlicer integration has substantial infrastructure built but is **NOT production ready**:

- ✅ Docker build system with binary caching
- ✅ Profile loading and expression parsing
- ✅ Slicing pipeline service (untested end-to-end)
- ✅ Frontend UI for job submission
- ❌ Bundle import endpoint missing
- ❌ End-to-end slicing never validated
- ❌ Bed STL model loading not implemented

To complete the integration, focus on:
1. Implementing the missing import endpoint
2. Testing the full slicing pipeline end-to-end
3. Adding bed model visualization

This document preserves all implementation details for future reference.
