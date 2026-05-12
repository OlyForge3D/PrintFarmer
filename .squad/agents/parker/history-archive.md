# Parker History

## Core Context

Parker is the deployment / release / infrastructure specialist. Key retained context:
- Owns GHCR workflows, Dockerfile/compose template changes, installer/deployment profile behavior, and container-oriented troubleshooting.
- Strong operational rule: internal container-to-container traffic must use Docker DNS service names, not hardcoded LAN IPs.
- Frequently coordinates landings after backend/frontend approvals and records final branch or deployment state.
- Important paths: `scripts/docker/dockerfiles/`, `scripts/docker/compose-templates/`, `.github/workflows/`, and runtime `.env` / `.deploy-config` connectivity settings.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history
- 2026-03-10 to 2026-03-13: Delivered GHCR multi-arch publish workflow, monolith deployment mode, install profile selection, and optional Obico ML compose service support.
- 2026-03-25: Landed PendingReady-related squad sync work, documented `nginx-proxy` / `pfdev` usage boundaries, and captured Docker DNS rules from runtime connectivity debugging.

## 2026-03-25: PendingReady landing coordination

**Role:** Orchestrator / landing support  
**Status:** ✅ Complete

- Coordinated the final landing context around commit `e807133d` after frontend, backend, and QA approvals aligned.
- Captured branch-clean / push-complete state and the remaining user follow-up boundary (end-to-end confirmation still pending Jeff's runtime verification).

## 2026-03-25: Monitoring route error / Docker DNS learnings

**Status:** ✅ Documented

- Containerized deployments must use Docker DNS names like `spoolman:8000` and `obico-ml-api:3333` for internal services. Hardcoded LAN IPs caused the same class of `No route to host` failures seen in runtime monitoring.
- Updating `.env` / `.deploy-config` back to DNS-based service names restored internal connectivity for Spoolman and reinforced that similar 3333 errors should be investigated as runtime target-selection or network issues first.

**Role:** Deployment & Infrastructure Engineer  
**Status:** ✅ COMPLETED

### Deployment Action
Executed `./scripts/pfdev redeploy api` to deploy backend fix for slicer UI visibility in microservices mode.

**Rationale:** Used targeted `pfdev` script per user directive (Jeff Papiez preference for canonical script name) rather than full `deploy-docker.sh`:
- Fast iteration (5 min vs full-stack redeploy)
- Minimal disruption to other services
- Appropriate for single-service code change during active development

### Validation
- ✅ API container rebuilt and redeployed
- ✅ `/api/system/capabilities` returns `slicingEnabled=true`
- ✅ Slicer routing working (`/api/slicer/profiles` → 200 OK)
- ✅ All containers healthy (API, slicer-host, nginx-proxy)

### User Directive Captured
Documented Jeff Papiez preference: use `pfdev` (canonical), not `pf-dev` or `pf-dev.sh`. Decision record created for team reference.

### Key Lesson
In microservices deployments, module-loading logic and capability reporting need independent detection paths. Conflating them causes false-negative capability reports when services run as separate containers.


## 2026-04-05: 3D Model File Storage Path Bug Fix

**Role:** DevOps & Deployment Engineer  
**Status:** ✅ Complete — Code fixed, deployment validated

### Problem Investigation

User reported 404 errors when accessing uploaded 3D models via `/api/3d-models/file/{id}`. Investigation revealed:

1. **Database records exist** with `FilePath = "/"` and GUID-based filenames
2. **Physical files missing** from container's `/app/models` directory (mounted volume)
3. **Root cause**: `Model3DFileService.GetModelFilePathAsync()` was constructing **relative** paths instead of **absolute** paths
   - Controller expected: `/app/models/c403db80-8b5e-4346-ab6b-b454cb5799e9.stl` (absolute)
   - Service returned: `c403db80-8b5e-4346-ab6b-b454cb5799e9.stl` (relative)
   - File existence check failed: `File.Exists(relative_path)` → false → 404

### Technical Details

**Broken logic** in `Model3DFileService.cs` line 264:
```csharp
// BEFORE: Incorrectly stripped base path and returned relative
return Path.Combine(model.FilePath, model.FileName)
    .Replace(_modelsPath, string.Empty)
    .TrimStart(Path.DirectorySeparatorChar, '/');
```

When `model.FilePath = "/"`, `_modelsPath = "/app/models"`, and `model.FileName = "guid.stl"`:
- `Path.Combine("/", "guid.stl")` → `/guid.stl`
- `.Replace("/app/models", "")` → `/guid.stl` (no match)
- `.TrimStart(...)` → `guid.stl` (relative path)
- Controller `File.Exists("guid.stl")` → false (not an absolute path)

**Fix** in `Model3DFileService.cs`:
```csharp
// AFTER: Return absolute path directly
return Path.Combine(_modelsPath, model.FileName);
// Returns: /app/models/c403db80-8b5e-4346-ab6b-b454cb5799e9.stl
```

Also fixed `GetModelThumbnailPathAsync()` with same pattern.

### Deployment & Validation

1. **Built solution** in Release mode: 0 errors, 1 minor warning (SA1515 blank line)
2. **Rebuilt API container** with `docker compose build --no-cache api`
3. **Restarted API service**: `docker compose up -d api`
4. **Verified volume mount**: `/home/pi/.printfarmer/models` → `/app/models` (correct)
5. **Confirmed environment variable**: `MODEL_UPLOAD_PATH=/app/models` (correct)

### Data Loss Discovery

Existing model records (4 files uploaded Apr 5 04:12-04:13 UTC) have **no physical files**:
- Files were uploaded to a previous container before proper volume persistence
- Container was deleted during rebuild, files lost
- Database records remain orphaned

**Resolution**: Users must re-upload models. Fix prevents future data loss.

### Key Files Modified

- `src/slicer/Farm.Slicer.Module/Services/Model3DFileService.cs` (GetModelFilePathAsync, GetModelThumbnailPathAsync)

### Deployment Configuration Verified

Docker compose configuration (correct):
- Environment: `MODEL_UPLOAD_PATH=/app/models`
- Volume mount: `${EXTERNAL_MODELS_PATH:-.volumes/printfarmer-model-storage}:/app/models`
- `.env` override: `EXTERNAL_MODELS_PATH=/home/pi/.printfarmer/models`
- Actual mount: `/home/pi/.printfarmer/models` → `/app/models` ✅

### Learnings

- In Docker microservices, **volume mounts must be validated** before users upload data
- DB records without physical files indicate **storage misconfiguration or data loss**
- Service methods must return **absolute paths** when callers use `File.Exists()` checks
- The `FilePath` column storing `"/"` was a red herring — the real issue was path construction logic

## 2026-04-05: Slicer-Host Deployment Gap — Stale Container + Missing Volume Mount

**Role:** DevOps & Deployment Engineer  
**Status:** ✅ Complete — Template fixed, code patched, redeployed, verified

### Problem

User redeployed API + frontend after commit `826e98ae` (slicer upload/retrieval fixes), but 3D model uploads still appeared "immediately successful" with nothing showing on the Models page.

### Root Causes Found (Two)

1. **Stale slicer-host container** — User rebuilt `printfarmer-api` and `printfarmer-frontend` but NOT `printfarmer-slicer-host`. Since nginx routes `/api/3d-models/` to slicer-host (port 5246), the slicer-host was still running DLLs from 02:11 UTC while API had 03:12 UTC code. The slicer module code changes never reached the running slicer-host.

2. **Missing volume mount + env var on slicer-host** — The compose template for slicer-host had NO `MODEL_UPLOAD_PATH` environment variable and NO volume mount for model storage. The slicer-host defaulted to `/app/uploads` (container-internal writable layer), meaning:
   - Every container rebuild would **destroy all uploaded model files**
   - The worker couldn't access uploaded models (it mounts `/app/models` read-only)
   - Path inconsistency between API (`/app/models`) and slicer-host (`/app/uploads`)

3. **Application code bug (still present)** — `GetModelFilePathAsync` in `Model3DFileService.cs` stripped `_modelsPath` and returned a relative path. The controller calls `File.Exists(relativePath)` which resolves against CWD `/app`, not the models directory. File existed at `/app/models/guid.stl` but check looked at `/app/guid.stl`. Fixed to return `Path.Combine(_modelsPath, model.FileName)`.

### Fixes Applied

**Template fix** (`scripts/docker/compose-templates/docker-compose.slicer-host.yml`):
- Added `MODEL_UPLOAD_PATH=/app/models` to environment
- Added `${EXTERNAL_MODELS_PATH:-.volumes/printfarmer-model-storage}:/app/models` volume mount
- Both changes also applied to runtime `docker-compose.yml`

**Code fix** (`src/slicer/Farm.Slicer.Module/Services/Model3DFileService.cs`):
- `GetModelFilePathAsync`: Changed from relative path to `Path.Combine(_modelsPath, model.FileName)`
- `GetModelThumbnailPathAsync`: Changed from `Path.Combine(model.FilePath, ...)` to `Path.Combine(_modelsPath, ...)`

**Data rescue**: Copied 6 files from container's `/app/uploads/` to host volume `/home/pi/.printfarmer/models/` before rebuild.

### Verification

- Slicer-host rebuilt and healthy
- `MODEL_UPLOAD_PATH=/app/models` confirmed in container env
- Volume mount verified: 6 model files visible at `/app/models/`
- File download endpoint: HTTP 200 (was 404)
- All three access paths verified: container-internal, host port 5246, nginx proxy port 80

### Learnings

- **Redeploying "api + frontend" does NOT cover slicer-host** — the slicer module routes (`/api/3d-models/`, `/api/slice/`, etc.) run in a separate container. This is the most common deployment gap in microservices mode.
- **Every service that handles file uploads MUST have a volume mount** — ephemeral container storage means data loss on rebuild. The slicer-host template was missing this critical mount.
- **`MODEL_UPLOAD_PATH` must be set on EVERY service that reads/writes model files** — without it, services fall back to different default paths and can't share storage.
- **Path construction must return absolute paths** when downstream code uses `File.Exists()` — relative paths depend on CWD which varies between dev and container environments.



**[Older entries archived on 2026-05-12 — see history.md for recent updates]**
