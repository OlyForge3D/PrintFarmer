# Parker History

## Core Context

Parker is the deployment, release, and infrastructure specialist. Key retained context:

- Owns GHCR workflows, Dockerfile and compose template changes, installer/deployment profile behavior, repo syncs, and container-oriented troubleshooting.
- Strong operational rule: internal container-to-container traffic must use Docker DNS service names, not hardcoded LAN IPs.
- Frequently coordinates landings after backend/frontend approvals and records final branch, PR, deployment, or CI state.
- Important paths: `scripts/docker/dockerfiles/`, `scripts/docker/compose-templates/`, `.github/workflows/`, and runtime `.env` / `.deploy-config` connectivity settings.

Early detailed entries were summarized on 2026-03-25. Detailed April/May entries were summarized on 2026-05-31T03:18:29Z for maintainability; see decisions, orchestration logs, and session logs for source detail.

## Summarized History

- 2026-03-10 to 2026-03-13: Delivered GHCR multi-arch publish workflow, monolith deployment mode, install profile selection, and optional Obico ML compose service support.
- 2026-03-25: Landed PendingReady-related squad sync work, documented `nginx-proxy` / `pfdev` usage boundaries, and captured Docker DNS rules from runtime connectivity debugging.
- 2026-04-05: Fixed 3D model file serving by returning absolute `/app/models/<file>` paths from `Model3DFileService` for model and thumbnail lookup. Rebuilt/restarted API container and confirmed model volume/env wiring. Existing missing files were determined to be orphaned from earlier non-persistent uploads; users needed to re-upload.
- 2026-05-21: Recorded Dependabot triage pattern: test-only dependency bumps can be auto-merged when green, runtime patch bumps need local build/test verification, and GitHub Actions major updates need manual changelog review.
- 2026-05-29: Performed dev→main sync work for accumulated development changes. Local commit `d4d8b4a1e` was blocked from push because token lacked `workflow` scope for `.github/workflows/` changes. Durable rule: any push touching workflows requires `gh auth refresh --scopes workflow`; dev→main syncs must strip `.squad/` before committing.
- 2026-05-30: Redid broken main→development sync after PR #321 would have deleted `.squad/` state. Created `sync/main-to-dev-2026-05-30`, preserved all `.squad/` files, removed spurious `.github` directory-rename artifacts, verified zero `.squad/` diff, pushed, and opened PR #329. Durable recipe: `-X ours` handles text conflicts only; explicitly restore/stage `.squad/` modify/delete conflicts and verify zero `.squad/` diff.
- 2026-05-30: PR #329's iOS unit-test fix is pushed in PFarm1 commit `06c436263`, but GitHub macOS CI jobs are blocked by the account billing/spending limit. Treat this as an external CI billing blocker pending Brady's billing action, not as a code failure.

## Durable Release / Sync Rules

- **main→development sync:** Preserve all `.squad/` state from development, accept appropriate main workflow/manifest updates, remove spurious `.github/*` files caused by directory-rename heuristics, and verify zero `.squad/` diff before push.
- **development→main sync:** Strip all `.squad/`, `.ai-team/`, `.ai-team-templates/`, `team-docs/`, and proposal-only artifacts before committing. Accept development's application code as source of truth.
- **Workflow scope:** Any push touching `.github/workflows/` requires a GitHub token with `workflow` scope; otherwise GitHub rejects the entire push.
- **Container networking:** Use Docker DNS service names for internal service-to-service traffic. Avoid hardcoded LAN IPs inside containers.
