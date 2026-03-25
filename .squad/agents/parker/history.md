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
