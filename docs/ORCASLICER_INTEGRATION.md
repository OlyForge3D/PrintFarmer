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

## Dual-Engine (Current + Previous) Support

As of issue #578, PrintFarmer can run **two** OrcaSlicer engine versions concurrently — the current default (`2.4.2`) plus the previous version (`2.3.1`). This lets operators finish jobs sliced against a prior engine while migrating to the newer one, without a big-bang cutover.

### How dispatch works

- Every OrcaSlicer plugin registers itself under a `(name, version)` key in `ISlicerRegistry`. `SlicerPluginDiscovery` de-duplicates on that tuple so a repeated version is a no-op.
- Slice jobs carry an optional `slicerEngineVersion` (persisted on `SliceJob` — see the `AddSliceJobSlicerEngineVersion` migrations for PostgreSQL and SQL Server). `SliceJobController.SubmitAsync` validates the version against the registry and returns HTTP 400 with the list of registered versions if it's unknown.
- The controller **server-derives** `RequiredCapabilitiesJson`:
  - Pinned to a version → `["orcaslicer:<version>"]` only. No generic `orcaslicer` capability.
  - Unpinned → `["orcaslicer"]` only.
  This is deliberate: `EfSliceJobRepository.ClaimNextJobAsync` uses OR-match, so a mixed capability set would let a wrong-version worker claim a pinned job.
- Workers advertise their supported capabilities via `WorkerCapabilityProvider`, which emits `["orcaslicer","orcaslicer:<Worker:EngineVersion>","stl-processing","gcode-generation"]`. Both `SlicerRegistrationClient` (initial registration payload) and `QueueConsumerService` (per-poll capability list) use the same provider so they never drift.
- `GET /api/slicers/engines` returns `[{ engine, versions[], latest }]` for the React version picker.

### Deploying a second (previous) worker

1. Set `Worker__EngineVersion=2.3.1` (or your previous version), and set **distinct** values for `SlicerRegistry__Host`, `Worker__WorkerId`, `Worker__InstanceId`, and `SlicerRegistry__ServiceName` so `SlicersService.UpsertAsync` doesn't collapse the two workers into one row.
2. The bundled compose template `scripts/docker/compose-templates/docker-compose.orcaslicer-worker-previous.yml` does exactly this. Enable it in generated stacks with `ENABLE_ORCA_WORKER_PREVIOUS=yes` when running `scripts/docker/compose-generator.sh` (default is off, so single-engine installs are unchanged).
3. The Dockerfile (`scripts/docker/dockerfiles/Dockerfile.multistage`) restores and publishes both `Farm.Slicers.OrcaSlicer.v2_4_0` and `Farm.Slicers.OrcaSlicer.v2_3_1` plugins into the shared plugin drop that the host loads via `Slicer:PluginsPath`.

### Lifecycle policy

We ship **at most two** engine versions in-tree at any time: the current default and one prior. When a new default is promoted, retire the oldest plugin project and its Dockerfile restore stanza only after completing the drain gate below. This keeps the arm64 emulation cost, image size, and cache surface bounded, and matches the operational reality that operators rarely need more than one migration window overlap.

### Retiring the previous engine safely

> **Drain gate:** Never disable or remove the previous-version worker while jobs
> remain pinned to it. A pinned job carries only the capability
> `orcaslicer:<version>`; after the matching worker disappears, no current
> worker can claim that job.

Use this sequence before retiring a version such as `2.3.1`:

1. **Stop new pins.** Start a slicing maintenance window, or require users to
   leave **New Slice Job → Engine version** on **Latest**. The page defaults to
   the `latest` version returned by `GET /api/slicers/engines`; a specific
   previous version is an explicit user selection. There is currently no
   admin setting or environment allowlist that hides only the previous
   version while its worker remains online, so do not allow unrestricted new
   submissions during the drain. Do **not** set
   `ENABLE_ORCA_WORKER_PREVIOUS=no` yet.
2. **Find every pinned queued or in-flight job.** The Jobs view is at
   **Admin → Manage → Operations → Workers → Jobs**. Use the database as the
   authoritative version filter because the queue view does not expose the
   engine-version pin. For PostgreSQL:

   ```sql
   SELECT
       "Id",
       "Status",
       "SlicerEngineVersion",
       "RequiredCapabilitiesJson",
       "LeaseExpiresAt",
       "QueuedAt"
   FROM slicer."SliceJobs"
   WHERE (
       "SlicerEngineVersion" = '2.3.1'
       OR "RequiredCapabilitiesJson" LIKE '%"orcaslicer:2.3.1"%'
   )
   AND "Status" IN ('Queued', 'Processing')
   ORDER BY "QueuedAt";
   ```

   SQL Server deployments use the same columns under
   `[slicer].[SliceJobs]`. Include all `Processing` rows: an expired lease
   remains eligible for the retiring worker to reclaim.
3. **Drain, migrate, or cancel.** Keep the previous worker healthy until the
   query returns zero rows. Let compatible jobs complete. To migrate a job,
   cancel it and submit an equivalent replacement from **New Slice Job** with
   **Latest** or a specific still-supported version after checking its
   version-scoped profiles and settings. There is no in-place repin API or UI;
   changing only `SlicerEngineVersion` in the database would leave
   `RequiredCapabilitiesJson` and the settings snapshot inconsistent. If
   compatibility is uncertain, cancel the job in the Jobs view and ask its
   owner to review the settings and resubmit.
4. **Remove the lane only after the query is empty.** Regenerate the compose
   file through the deployment script, then reconcile the stack with orphan
   removal:

   ```bash
   ENABLE_ORCA_WORKER_PREVIOUS=no \
     ./scripts/deploy-docker.sh --regenerate-config
   docker compose --env-file .env -f docker-compose.yml \
     up -d --remove-orphans
   ```

   After `orcaslicer-worker-previous` is absent from `docker compose ps`, the
   retired image and bind-mounted temporary state can be removed:

   ```bash
   docker image rm printfarmer-orcaslicer-worker-previous
   rm -rf .volumes/printfarmer-orcaslicer-previous-temp
   ```

5. **Validate the retirement.** Run the SQL query again and confirm that it
   returns zero rows. Check the queue view for no stranded `Queued` or
   `Processing` jobs, then inspect engine discovery. The default targets
   monolithic/single-container deployments on the main API port (`5245`); for
   microservices deployments, override the URL to reach `slicer-host` on
   `5246` (nginx routes `/api/slicers` there in split mode):

   ```bash
   SLICER_ENGINES_URL="${SLICER_ENGINES_URL:-http://localhost:5245/api/slicers/engines}"
   curl -fsS "$SLICER_ENGINES_URL" | jq .
   ```

   After deploying the application release that removes the retired plugin,
   the retired version must no longer appear. An entry that still advertises
   `2.3.1` with `available: false` means the plugin is loaded but its worker is
   missing; either restore the lane immediately or finish deploying the
   release that removes that plugin before reopening submissions.

If the drain was incomplete and orphaned jobs surface, roll back the worker
removal before changing or cancelling those jobs:

```bash
ORCASLICER_VERSION_PREVIOUS=2.3.1 \
  ENABLE_ORCA_WORKER_PREVIOUS=yes \
  ./scripts/deploy-docker.sh --regenerate-config
docker compose --env-file .env -f docker-compose.yml up -d --build
```

Wait for `GET /api/slicers/engines` to report `2.3.1` as
`available: true`, then resume the drain from step 2.

### Caveats

- **arm64 hosts**: OrcaSlicer AppImages are x86-64; running two workers on an arm64 host doubles the emulation overhead. Prefer running the previous-version worker on x86-64 nodes when possible.
- **Profile/asset caches** are keyed per plugin assembly, so v2.3.1 and v2.4.0 have independent embedded resources and cannot poison each other.
- **Backwards compatibility**: `slicerEngineVersion` is nullable everywhere. Jobs submitted before this change (or by clients that omit the field) route to any registered `orcaslicer` worker exactly as before.

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
ARG ORCASLICER_VERSION=2.4.2
ARG ORCASLICER_SHA256=d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd

FROM --platform=linux/amd64 ubuntu:24.04 AS orca-download
ARG ORCASLICER_VERSION
ARG ORCASLICER_SHA256

# Install extraction tools
RUN apt-get update && apt-get install -y \
    curl ca-certificates jq p7zip-full squashfs-tools libarchive-tools file wget

# Download the exact official x86_64 AppImage and verify it before extraction.
ADD https://github.com/OrcaSlicer/OrcaSlicer/releases/download/v${ORCASLICER_VERSION}/OrcaSlicer_Linux_AppImage_Ubuntu2404_V${ORCASLICER_VERSION}.AppImage /tmp/orcaslicer.AppImage
RUN echo "${ORCASLICER_SHA256}  /tmp/orcaslicer.AppImage" | sha256sum -c --strict - && \
    # ... extract the AppImage and write orcaslicer.version/orcaslicer.sha256 ...
    test -x /orcaslicer-dist/opt/orcaslicer/AppRun

FROM scratch AS orcaslicer-binaries
ARG ORCASLICER_VERSION
ARG ORCASLICER_SHA256
COPY --from=orca-download /orcaslicer-dist /orcaslicer-dist
LABEL orcaslicer.version="${ORCASLICER_VERSION}" \
      orcaslicer.sha256="${ORCASLICER_SHA256}"
```

### Build Commands

```bash
# Option 1: Build script (recommended)
./scripts/build-orcaslicer-optimized.sh

# With specific version
ORCASLICER_VERSION=2.4.2 ./scripts/build-orcaslicer-optimized.sh

# With GitHub token (avoid rate limits)
GITHUB_TOKEN=your_token ./scripts/build-orcaslicer-optimized.sh

# Option 2: Manual two-stage build
# Step 1: Build binary layer (slow first time, cached after)
docker build -f scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries \
  -t orcaslicer-binaries:2.4.2 \
  --build-arg ORCASLICER_VERSION=2.4.2 \
  --build-arg ORCASLICER_SHA256=d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd \
  .

# Step 2: Build worker (fast, uses cached binaries)
docker build -f Dockerfile.multistage \
  --target orcaslicer-worker \
  -t printfarmer-orcaslicer-worker \
  --build-arg ORCASLICER_VERSION=2.4.2 \
  --build-arg ORCASLICER_SHA256=d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd \
  .

# Option 3: Docker Compose
docker compose --profile orca-binaries build orcaslicer-binaries
docker compose --profile orca build orcaslicer-worker
```

### Build Arguments

| Argument | Default | Description |
|----------|---------|-------------|
| `ORCASLICER_VERSION` | 2.4.2 | Stable OrcaSlicer release version to download |
| `ORCASLICER_SHA256` | `d12fb8...029fd` | Official x86_64 Ubuntu 24.04 AppImage SHA-256 |
| `ALLOW_STUB` | false | Explicit CI-only escape hatch; production builds remain fail-closed |

Cached binary images are reusable only when both `orcaslicer.version` and
`orcaslicer.sha256` labels exactly match the requested release. The same
values are embedded in the binary layer. Missing or mismatched metadata causes
the deploy/build path to reject the cached image instead of retagging it.

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

### Profile Counts (OrcaSlicer 2.4.x)

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

**Wizard UI**: `src/Web/ReactApp/src/features/slicer/orca/components/OrcaImportWizard.tsx`

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
| `src/Web/ReactApp/src/features/slicer/orca/components/OrcaImportWizard.tsx` | Import wizard |

### Docker

| File | Purpose |
|------|---------|
| `scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries` | Binary layer |
| `Dockerfile.multistage` | Worker build (orcaslicer-worker target) |
| `scripts/build-orcaslicer-optimized.sh` | Build script |
| `docker-compose.yml` | Service definitions |

---

## Debugging & Testing

### Pinned Worker Publication & Mandatory Calibration Smoke Gate

Calibration generation (issue #899) is only allowed to report itself operational after the **published,
digest-pinned** OrcaSlicer 2.3.1 worker has completed a real calibration run. This is enforced by
`.github/workflows/orcaslicer-strict-build.yml`, which is manual-dispatch only and double-guarded.

**Publication (`build-orcaslicer-strict`)**

- Job permissions are exactly `contents: read` and `packages: write`; `GITHUB_TOKEN` is used only to log
  in to GHCR.
- Only an image that already passed `scripts/verify-orcaslicer-worker.sh require-real` is pushed.
- Two immutable tags are pushed to `ghcr.io/<owner>/printfarmer-orcaslicer-worker-pinned`:
  `sha-<commit>` and `<orcaVersion>-sha-<commit>`.
- The manifest digest is taken from the push result **and** re-read from the registry with
  `docker buildx imagetools inspect`; both must agree and must match `^sha256:[0-9a-f]{64}$`. The digest
  is never invented or pre-embedded.
- The published image is then re-verified **by digest** (`repository@sha256:...`).
- Job outputs `image`, `digest` and `image_ref` carry the identity forward; a small non-secret evidence
  artifact (`pinned-orca-publication.json`) records repository, tags, digest and the pinned upstream
  checksum.

General 2.4.x slicing is unaffected: it is built and published by `docker-publish.yml` and
`orcaslicer-base-image.yml`, which this workflow does not touch.

**Mandatory smoke gate (`calibration-pinned-smoke`)**

Permissions are `contents: read` and `packages: read`. The job pulls the published image by digest and
runs the explicitly filtered gate:

```bash
cd src
RunIntegrationTests=true \
PRINTFARMER_ORCA_SMOKE=required \
PRINTFARMER_ORCASLICER_IMAGE=ghcr.io/<owner>/printfarmer-orcaslicer-worker-pinned \
PRINTFARMER_ORCASLICER_IMAGE_DIGEST=sha256:<64 hex> \
dotnet test ./tests/Farm.Web.IntegrationTests/Farm.Web.IntegrationTests.csproj \
  -c Release -p:RunIntegrationTests=true --filter 'Category=PinnedOrcaSmoke'
```

The gate (`src/tests/Farm.Web.IntegrationTests/Calibration/`) drives every hop through production code:

1. The API runs on a **real Kestrel loopback listener** (`KestrelCalibrationApiHost`), not an in-memory
   test server, so the container can dial it. The container joins the runner's network namespace.
2. Capability is asserted **false** first, with `pinned_worker_unavailable` as the only blocked hop.
3. The published worker is pulled and run **by digest**; `Worker__ContainerDigest` is injected at
   runtime only, because embedding it during the build would change the digest it claims.
4. The worker registers itself through `POST /api/slicers/register` with `X-Slicer-Api-Key`, receives its
   registry-issued identity and key, and claims work with `X-Worker-Key` + `X-Worker-Id` under an active
   lease and fencing token.
5. A tiny deterministic STL is uploaded through `POST /api/3d-models/upload` and proven to round-trip
   byte for byte through the authenticated download route.
6. The immutable snapshot is seeded with the **exact native profiles the running container publishes**
   (`GET /api/profiles`), so OrcaSlicer receives its own documents back and verifies their digests.
7. The worker downloads the model over the authenticated worker route, runs the pinned build, uploads its
   artifact and completes the job; the saga reconciles, annotates, safety-validates and promotes the
   result to an immutable `GcodeFile`, and the test asserts byte, hash and lineage equality.

If the gate cannot execute, `PRINTFARMER_ORCA_SMOKE=required` turns the blocker into a failure, so the
workflow fails and capability never flips. Without that variable the same test reports the concrete
blocker and asserts capability stayed false, which is the only honest local outcome.

Digest validation and gating rules are unit-tested in the default suite
(`Farm.Web.Api.Tests.Calibration.Generation.PinnedOrcaPublicationTests`); the gate compiles the same
source file, so there is one implementation rather than two.

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

## Schema Version Awareness (#578)

PrintFarmer runs two OrcaSlicer engine versions side by side (current 2.4.2 and previous 2.3.1). Settings that a user edits on the Slice Job page must match the engine that will actually process the job — fields added in 2.4.x must not appear when the job is pinned to 2.3.1, fields retired in 2.4.x must not appear when the job is pinned to 2.4.2, and fields that were renamed between versions must resolve to the correct key for the pinned engine.

### API surface

Four profile-schema endpoints on `ProfilesController` accept an optional `engineVersion` query parameter:

- `GET /api/slicer/profiles/schemas?engineVersion={version}`
- `GET /api/slicer/profiles/schemas/process?engineVersion={version}`
- `GET /api/slicer/profiles/schemas/machine?engineVersion={version}`
- `GET /api/slicer/profiles/schemas/filament?engineVersion={version}`

The response is cached with `VaryByQueryKeys = ["engineVersion"]` so upstream/CDN caches key by version. Omitting `engineVersion` (or passing an unparsable string) returns the full unfiltered schema — this is the safe fallback for legacy callers and engine-agnostic surfaces such as the Profile Management pages.

### Field-metadata model

`ProfileFieldMetadata` carries four optional version-aware attributes:

| Property | Meaning |
|---|---|
| `minEngineVersion` | Field is included only when the requested version ≥ this value. Used for fields added in a newer engine (e.g. `wallGenerator`, `enableArcFitting` added in 2.4.0). |
| `maxEngineVersion` | Field is included only when the requested version ≤ this value. Used for retired settings (e.g. `legacyPreviewSetting` retired in 2.4.0). |
| `renamedFromKey` + `renamedInVersion` | Field is emitted under `renamedFromKey` when the requested version < `renamedInVersion`, otherwise under `key`. Preserves all other metadata across the rename. |

Version filtering uses `System.Version` semantic comparison. Unparsable requests fall through to unfiltered (safe fallback).

### Runtime data delivery

The 2.3.1 backend plugin ships `NullProfilesProvider` with an empty assets manifest **by design**: the version-correct profile/asset content is delivered at runtime from the version-matched OrcaSlicer worker's `/opt/orcaslicer/resources` tree, not from the plugin binary. This avoids data drift between the plugin and the actual engine and lets the workers upgrade profiles independently of the .NET deployment.

### React consumers

`useProfileSchema(profileType, engineVersion?)` includes `engineVersion` in its TanStack Query `queryKey` (`['profile-schema', profileType, engineVersion ?? null]`), so:

- Switching the pinned engine invalidates the cache and re-fetches.
- Two schema instances for two versions coexist in the cache without cross-contamination.
- Passing `undefined` returns the unfiltered schema (engine-agnostic pages).

The live New Slice Job page inherits this behaviour: profile queries include `selectedEngineVersion` in their query keys, and the `advancedProcessSettings` state is re-seeded from the version-scoped profile JSON when the query returns, so payloads submitted to the slicer never include stale keys from a previously-selected engine.

### Testing

- `Farm.Slicer.Module.Tests/Services/ProfileSchemaProviderTests` — added/removed/renamed field mechanic and edge cases (null, unparsable version).
- `Farm.Slicer.Module.Tests/Controllers/ProfilesControllerSchemaVersionTests` — controller-level pass-through and `VaryByQueryKeys` behaviour.
- `ReactApp/src/features/slicer/components/settings/schema/__tests__/useProfileSchema.test.tsx` — hook contract, per-version queryKey isolation, undefined-version fallback.

### Follow-up

The example version-scoped fields (`wallGenerator`, `enableArcFitting`, `legacyPreviewSetting`, `bedAdhesionOverride`/`firstLayerAdhesion`) illustrate the plumbing but are not an authoritative OrcaSlicer 2.3 → 2.4 delta. The exact field windows should be refined against upstream OrcaSlicer release notes before consumers depend on the specific field set.

---

## Live Slice-Job Editor Version Scoping (#578 Path B)

The schema endpoints above serve the profile-management surface. The **live New Slice Job page** — the primary settings editor for a running slice — historically consumed the static `orcaSettingsMetadata.json` bundle directly, bypassing the versioned schema pipeline. Issue #578 (Path B) closes that gap so the live editor renders and submits the correct field set for the pinned engine.

### Resolver

`src/Web/ReactApp/src/features/slicer/components/settings/orcaSettingsMetadataResolver.ts` exposes two pure functions:

- `getMetadataForVersion(engineVersion?)` returns a version-scoped `{ profileTypes, renameFromNewToThis, renameFromThisToNew, resolvedFor }` view of the static bundle:
  - Fields whose `addedIn` window falls after `engineVersion` are omitted from `settings` and from every tab.
  - Fields whose `renamedIn` window falls after `engineVersion` are emitted under the older key, and the newer key is hidden.
  - Passing `undefined`/`null`/`''` returns the full union bundle unchanged — safe fallback for engine-agnostic surfaces.
- `scrubSettingsForVersion(settings, profileType, engineVersion, deltasOverride?)` returns a new dictionary containing only the keys valid for `engineVersion`, migrating renamed values to the version-correct key and dropping any key that has no version-correct home.

Both functions are driven by `orca-settings-version-delta.ts`, a hand-maintained delta table that pins real 2.4 additions (`precise_z_height`, `alternate_extra_wall`, `interlocking_beam`). The rename mechanic is present but unused in the shipped delta pending confirmation of a real 2.3 → 2.4 rename; injected test deltas exercise it in the test suite.

### Live wiring

`MetadataProfileEditor` (used by `SlicerSettingsPanel` and therefore by `NewSliceJobPage`) accepts an optional `engineVersion?: string` prop and calls `getMetadataForVersion(engineVersion)` inside a `useMemo`. All tabs/sections/settings the user sees come from the returned scoped bundle — so:

- 2.4-added fields disappear from tabs and from the "Other Settings" fallback when the job is pinned to 2.3.1.
- Renamed fields render under the version-correct key (older key on 2.3.1, newer key on 2.4.1) when a rename is declared in the delta.

`NewSliceJobPage` computes `effectiveEngineVersion = selectedEngineVersion ?? latestAvailableForEngine` and passes it to `<SlicerSettingsPanel>`. A dedicated `useEffect` on `effectiveEngineVersion` runs `scrubSettingsForVersion` against **all three** in-flight settings state objects atomically — `advancedProcessSettings` (dynamic dict), `slicerSettings` (typed OrcaProcessSettings the inline editor writes to), and `originalProcessSettings` (baseline snapshot used by `diffProcessOverrides` at submit time). This is what makes the added/removed/renamed guarantee real:

- Keys not valid on the newly selected engine are dropped from every state path.
- Renamed keys migrate atomically (newer-key → older-key on downgrade, and are dropped on upgrade because they no longer exist in the target metadata — the user must re-opt into the new field).
- Identity is preserved when settings are already clean to avoid render loops.

The submit payload constructs `overrides` as `{ ...advancedProcessSettings, ...diffProcessOverrides(slicerSettings, originalProcessSettings) }`. Both source objects are scrubbed by the effect, and the merged result is additionally scrubbed by `scrubSettingsForVersion` at submit time as defense-in-depth so a stale key introduced by any future state path cannot leak into `POST /api/slicer/jobs`. The persisted `slicerEngineVersion` field on the job (Path A) binds the job to that engine.

### Persisted-job reopen

A slice job carries `slicerEngineVersion` in its record. Any future edit/reopen entry that rehydrates `selectedEngineVersion` from that stored value will drive the same resolver + scrub flow, so the reopened editor renders and submits under the job's pinned engine version without cross-version contamination.

### Testing (Path B)

- `orcaSettingsMetadataResolver.test.ts` — verifies 2.4.1 includes real Orca 2.4 additions (`precise_z_height`, `alternate_extra_wall`, `interlocking_beam`); 2.3.1 hides them from both `settings` and tab layouts. Injected test deltas prove the rename mechanic in both directions (2.4→2.3 migrates value onto old key; 2.3→2.4 drops old key). Idempotence and unfiltered fallback are covered.
- Path A tests (`useProfileSchema.test.tsx`, `ProfileSchemaProviderTests`, `ProfilesControllerSchemaVersionTests`) continue to guard the profile-management schema endpoints.

### Deprecation lifecycle

The runtime supports exactly the **current** OrcaSlicer major version and the **immediately previous** major version. When the current engine advances (e.g. 2.5.0 becomes current), operators must complete **Retiring the previous engine safely** before disabling or removing the previous 2.3.1 worker. Only after the pinned-job query is empty are the plugin project, worker Docker layer, and `orca-settings-version-delta.ts` entries for that version removed together; the next release notes call out the removal. The delta file is the single audit point for the frontend — refreshing it to match new upstream release notes is the recommended step whenever a new OrcaSlicer major version is added.

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
