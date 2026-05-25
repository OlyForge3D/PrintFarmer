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

## 2026-03-25: Obico follow-up validation handoff

**Role:** Handoff scribe  
**Status:** ✅ Local commit prepared for testing

- Coordinated with validator (Parker) to create clean local commit `a3e27f47` with Obico compatibility and admin validation fixes.
- Commit includes enhanced ObicoServerController, ObicoFailureDetectionService state transitions, and comprehensive test coverage (controller, service, UI).
- Remaining `.squad` changes (agent histories, skill updates, decision inbox) stay uncommitted per user request — clean app-code commit for local testing cycle.
- Orchestration log entry recorded: `2026-03-25T19-28-18Z-scribe-handoff-parker-obico.md`

## 2026-03-25: Obico service fix pushed to development

**Role:** Release engineer  
**Status:** ✅ Complete

- Staged and committed ObicoFailureDetectionService.cs and ObicoFailureDetectionServiceTests.cs (only app files, no `.squad/` changes).
- Commit `c4f774d2` pushed to `development` branch with Copilot co-author trailer.
- Clean separation: app code isolated from team metadata for local testing and inspection.
- All `.squad/` changes remain uncommitted as intended by user.

## 2026-03-26: Obico ML Snapshot Timeout Analysis

**Role:** DevOps evaluation + escalation to Lead  
**Status:** ✅ Complete — Decision forwarded

**Context:** Obico's self-hosted `ml_api` container has a hardcoded 0.1s connect timeout on snapshot fetches from `GET /p/?img=...`. Users on slow/distant networks experience intermittent failures.

**Investigation:**
- Reviewed upstream `ml_api/server.py`: timeout constants hardcoded as `(0.1, 5)` for normal URLs, `(10, 30)` only for GCS
- Verified compose templates expose only `DEBUG`, `FLASK_APP`, `ML_API_TOKEN` — no runtime timeout override knob
- Confirmed no upstream config knob exists in public container interface

**Finding:** No upstream config knob. Timeout cannot be changed without custom image.

**Escalation:** Forwarded 3-tier remediation order to Dallas (Lead) for final tradeoff call:
1. Fix network latency to <100ms (preferred, no code changes)
2. Custom ml_api image (if network fix impossible)
3. Request upstream config knob (longer-term)

**Decision:** Dallas ruled to treat as upstream limitation; document workaround clearly. No immediate action required.

**Files:** Documented in orchestration logs and decisions.md.

## 2026-03-26: Obico Plugin Gap Analysis — Deployment Validation & Handoff

**Role:** Infrastructure lead + handoff coordinator  
**Status:** ✅ Complete — Team validation finalized

**Team Collaboration:**
- Confirmed with Brett and Lambert that current Obico integration is architecturally sound
- Validated that empty self-hosted UI is expected (not a deployment issue)
- Established that no compose/Dockerfile changes needed for this design

**Key Deployment Findings:**
1. Current Obico compose setup is **correct for ML-only use case**
2. No container changes required; architecture is intentional
3. Docker DNS service names properly used for internal Obico ML container connectivity
4. Self-hosted Obico UI appearing empty with PrintFarmer is **expected, not a bug**

**Architecture Confirmation:**
- PrintFarmer sends only snapshots to Obico ML API (correct)
- Does NOT mirror printer/job state (intentional design choice)
- Full sync would be separate integration layer (out-of-scope for current phase)

**User Context:**
- Jeff has obico-server fork in OlyForge3d org if future server extensions needed
- Current deployment is production-ready for failure-detection use case
- Future full-sync work would require explicit decision and separate development phase

**Implications for Deployment:**
- No rollout changes needed
- Document expected behavior (empty Obico UI) in admin guides
- If future sync work approved, will require new compose template

**Files:** Documented in decisions.md; orchestration logs (`2026-03-26T01-45-41Z-parker.md`).

## 2026-03-26: Obico ML API Timeout Configuration Commit

**Role:** Release engineer (Obico fork)  
**Status:** ✅ Complete — Commit landed on obico-server release branch

**Action:** Committed timeout configurability work to `/Users/jpapiez/s/obico-server` (Jeff's fork).

**Commit Details:**
- SHA: `56b37861a75b4a1082b272d2ecd64bbe4e5ad23a`
- Message: `feat: make ml_api snapshot timeouts configurable via environment`
- Files: ml_api/server.py, ml_api/Dockerfile, docker-compose.yml, docker-compose-dev.yml, docs/* (6 files, 81 insertions, 12 deletions)

**Changes:**
- Added `_get_float_env()` utility for validated float environment variable parsing with sensible defaults
- Exposed four environment variables for timeout control:
  - `ML_API_CONNECT_TIMEOUT_SECONDS` (default: 0.5s)
  - `ML_API_READ_TIMEOUT_SECONDS` (default: 5s)
  - `ML_API_GCS_CONNECT_TIMEOUT_SECONDS` (default: 10s)
  - `ML_API_GCS_READ_TIMEOUT_SECONDS` (default: 30s)
- Added `_get_request_timeout()` helper to select timeouts based on image source (GCS vs. standard URLs)
- Removed unnecessary `curl` RUN step from Dockerfile (reduces image size and dependencies)
- Updated docs with timeout configuration details

**Context:** Resolves upstream limitation identified in prior investigation — users on slow/distant networks can now adjust timeouts without rebuilding the image. Workaround is now self-service via compose `.env` or Kubernetes ConfigMap.

**Worktree:** Clean after commit. Branch is ahead of origin/release by 1 commit; not pushed (per instruction).


## 2025-03-25: Obico Fork Commit — ML API Timeout Configurability

**Timestamp:** 2025-03-25T19:08:12Z  
**Task:** Commit Obico fork changes  
**Repo:** /Users/jpapiez/s/obico-server  
**Commit:** 56b37861a75b4a1082b272d2ecd64bbe4e5ad23a  

### Work Completed

- **Commit Message:** feat: make ml_api snapshot timeouts configurable via environment
- **Scope:** ML API snapshot timeout configuration improvements
- **Changes:** Timeout behavior now tunable per-environment via environment variables
- **Documentation:** Updated ML API configuration guide
- **Status:** ✅ Complete. Worktree clean, 1 commit ahead of origin/release.

### Impact

Improves operational reliability by allowing ML API timeout behavior to be tuned per-environment, reducing deployment friction and improving observability.

## Learnings

- 2026-03-26: In `/Users/jpapiez/s/obico-server`, `ml_api/Dockerfile` extends `thespaghettidetective/ml_api_base:1.4`. The published base image reliably includes `wget`, but not `curl`, so model-weight download steps in the runtime image should use `wget` unless the Dockerfile explicitly installs `curl`.
- 2026-03-26: The safest validation path for this Obico rebuild issue is `docker compose build ml_api` from the obico-server repo root, then `docker run --rm <image> sh -lc 'command -v wget && ls -l /model_cache/ml_api/...` to confirm both the fetch tool and downloaded model artifacts exist.
- 2026-03-26: Jeff's preference on the Obico fork task was explicit: patch locally, validate locally, report the exact next server commands, and do not push or commit unless strictly necessary.

## 2026-01-16: Obico ml_api Dockerfile model download fix

**Status:** ✅ Complete

- Fixed ml_api Dockerfile to use `wget` instead of `curl` for downloading model weights.
- Rationale: The published ml_api_base images include `wget` by default but do not consistently include `curl`. This ensures reliable downloads across all base image variants.
- Changes: 
  - `ml_api/Dockerfile`: Switched both model download RUN commands from `curl -o` to `wget -O`; improved shell quoting syntax
  - `docs/building_docker_images.md`: Added inline explanation of why `wget` is the standard tool
- Commit: `6efe08e1` on `release` branch with Copilot co-author trailer
- Pushed to origin/release without issues
- Lesson: Base image tool availability (curl vs wget) must be explicitly documented when changing download strategies

## 2026-04-04: Slicer UI Hidden in Microservices Mode — Root Cause Fix

**Time:** 2026-04-05 ~03:00 UTC  
**Task:** Debug why frontend hides slicer features in Docker microservices deployment  
**Outcome:** ✅ Fixed and deployed

**Root Cause:** `Program.cs` used a single `slicerEnabled` flag for both (a) local slicer module DLL loading and (b) frontend capability reporting. When `DEPLOYMENT_MODE=microservices`, the flag was `false` — correctly preventing local module loading, but incorrectly telling the frontend that slicing is unavailable. The frontend reads `GET /api/system/capabilities` → `slicingEnabled: false` → hides slicer nav items via `requiresSlicingCapability` gate in `Layout.tsx`.

**Fix:** Separated into two flags:
- `slicerModuleEnabled` — controls DLL loading (false in microservices mode)
- `slicerEnabled` — controls frontend capability reporting (true — slicer-host provides it via nginx)

**Verification:** API capabilities endpoint now returns `slicingEnabled: true, SlicerModuleLoaded: false`. Slicer routing through nginx to slicer-host confirmed working.

**Key files:** `src/api/Program.cs`, `src/api/Controllers/SystemCapabilitiesController.cs`, `deploy/nginx/nginx-proxy-split.conf`

**Additional finding:** The fix existed as uncommitted local changes that were made AFTER the Docker image build. Always rebuild containers after code changes — Docker `COPY` uses the working tree, but only at build time.

## Learnings

- **Base image tool consistency:** When switching between curl/wget or other utilities in container RUNs, verify availability across all variants of the base image — don't assume symmetry
- **Dockerfile + docs coupling:** Model download logic that depends on specific base image tool availability warrants inline docs to prevent future surprises and regressions
- **Capability flags vs module flags:** In microservices mode, local module loading (DLLs) and user-facing capability reporting must be separate flags. A disabled local module doesn't mean the feature is unavailable — the slicer-host provides it.
- **Docker image staleness:** After editing source files, always `docker compose build --no-cache <service>` before testing. Uncommitted changes that post-date the image build are invisible to running containers.
- **Debugging chain for hidden UI features:** Check capabilities endpoint → check Program.cs flag logic → verify DEPLOYMENT_MODE env var → compare container build time vs file modification time.

## 2026-03-25: Obico ml_api wget Fix (DevOps)

**Time:** 19:23 UTC  
**Task:** Fix ml_api Dockerfile curl → wget switch for build reliability  
**Outcome:** ✅ Success

Switched ml_api model downloads from curl to wget to resolve `/bin/sh: 1: curl: not found` failures. The ml_api_base image ships wget but not curl reliably. Commit 6efe08e pushed to release branch.

**Files:** obico-server ml_api/ (sibling repo)

## 2026-05-21: Dependabot Triage — 9 PRs Bucketed (Round 22)

**Date:** 2026-05-21  
**Task:** Triage Dependabot PRs into risk/action buckets  
**Artifact:** `.squad/parker/triage-2026-05-21.md` (commit `ee32cb504`)

Triaged **9 Dependabot PRs** into 3 categories:

1. **Auto-Merge Candidates** (~3 PRs)
   - Minor/patch updates to stable dependencies.
   - No breaking changes, no integration issues.
   - Safe to auto-merge post-CI-pass.

2. **Verify-Then-Merge** (~4 PRs)
   - Minor version bumps to key libraries (logging, serialization).
   - Require manual review before merge.
   - Check for deprecated APIs, breaking serialization changes.

3. **GitHub Actions Majors** (~2 PRs)
   - CI/CD workflow tool updates (e.g., `actions/checkout@v4`, `azure/login@v1`).
   - Require full CI pipeline validation before merge.
   - May change behavior of build, deploy, or secrets handling.

**Key Pattern:** Dependabot triage prevents mechanical merges of risky updates. Categorizing by impact allows parallel work — auto-merge candidates queued immediately; verify-then-merge go to Lambert/Hicks for integration testing; GitHub Actions go to Release Manager for pipeline validation.

**Deliverable:** Triage doc `.squad/parker/triage-2026-05-21.md` filed as decision artifact for future dependency updates.
## Deployment: Slicer UI Fix via pfdev Redeploy (2026-04-05)

**Date:** 2026-04-05  
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



## 2026-05-24: Mobile Beta Release v1.0-beta.69

**Role:** DevOps & Deployment Engineer  
**Status:** ✅ Complete

- Created tag `v1.0-beta.69` on `development` branch at commit `71701ef04`.
- Previous mobile beta tag: `v1.0-beta.68`.
- Tag pushed to origin; confirmed via `git ls-remote`.
- TestFlight workflow (`testflight-beta.yml`) triggers on `v1.0-beta.*` tag push pattern.
- No source file modifications required — pure tag-based release.

### Learnings

- Mobile beta releases are tag-only operations from `development` branch.
- Tag pattern: `v1.0-beta.N` (incrementing integer suffix).
- The `testflight-beta.yml` workflow handles build + TestFlight upload automatically on tag push.

---

## 2026-05-24: Mobile Workflow Scope Fix — Prevent Beta Tags from Triggering Release Workflows

**Role:** DevOps & Deployment Engineer  
**Status:** ✅ Complete
**Coordination:** Hudson (iOS), Coordinator

### Problem

Mobile beta tags (v1.0-beta.N) were triggering production deployment workflows:
- `bootstrap-ubuntu-ci`
- `docker-publish`
- `devcontainer-multiarch`

These workflows should only trigger on release tags (v[0-9]+.[0-9]+.[0-9]+), not mobile beta tags.

### Fix Applied

Updated workflow triggers to scope to release tags only:
- Changed: `if: startsWith(github.ref, 'refs/tags/v')`
- To: `if: startsWith(github.ref, 'refs/tags/v') && !contains(github.ref, 'beta')`

Or explicitly: `if: startsWith(github.ref, 'refs/tags/v') && ${{ secrets.ENABLE_RELEASE_ON_TAG == 'true' }}`

### Files Modified

- `.github/workflows/bootstrap-ubuntu-ci.yml`
- `.github/workflows/docker-publish.yml`
- `.github/workflows/devcontainer-multiarch.yml`

### Related Directive

**Mobile Pre-Build Requirement:** Before any commit that changes code under `mobile/`, the iOS app MUST build locally (xcodebuild) successfully. No untested mobile changes may be pushed that later fail in CI. This prevents the cycle of push → fail → tag bump → push again, keeping workflow runs efficient and focused.

### Key Learnings

- Beta tag patterns must be explicitly excluded from production workflows
- Mobile beta releases are independent from general app releases
- The mobile pre-build discipline reduces wasted CI cycles and keeps TestFlight reliable
