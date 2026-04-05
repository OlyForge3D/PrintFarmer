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
- This is an operational configuration rule, not a controller-route bug.

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

## Learnings

- **Base image tool consistency:** When switching between curl/wget or other utilities in container RUNs, verify availability across all variants of the base image — don't assume symmetry
- **Dockerfile + docs coupling:** Model download logic that depends on specific base image tool availability warrants inline docs to prevent future surprises and regressions

## 2026-03-25: Obico ml_api wget Fix (DevOps)

**Time:** 19:23 UTC  
**Task:** Fix ml_api Dockerfile curl → wget switch for build reliability  
**Outcome:** ✅ Success

Switched ml_api model downloads from curl to wget to resolve `/bin/sh: 1: curl: not found` failures. The ml_api_base image ships wget but not curl reliably. Commit 6efe08e pushed to release branch.

**Files:** obico-server ml_api/ (sibling repo)

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

