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

