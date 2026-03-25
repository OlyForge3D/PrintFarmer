# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Deployment:** Docker Compose (multi-stage build), Nginx reverse proxy, multi-database support (SQLite, PostgreSQL, SQL Server, MySQL)
- **CI/CD:** GitHub Actions
- **Created:** 2026-03-06

## Pi 4 Deployment Infrastructure (2026-03-11)

**Sprint Focus:** GHCR CI/CD pipeline + monolith Docker infrastructure + deployment analysis

### Parker Work (Agent-28 & Agent-29)

**Agent-28: GHCR CI/CD Pipeline (153s)**
- Created `.github/workflows/docker-publish.yml` — automated multi-arch builds
- **Triggers:** Push to main, version tags (v1.2.3), manual workflow_dispatch
- **Matrix:** 3 images (api, frontend, monolith) × 2 platforms (amd64, arm64) = 6 parallel builds
- **Tagging:** Semantic versioning + SHA + latest via docker/metadata-action
- **Optimization:** GitHub Actions cache (80%+ expected hit ratio), ~8-12 min per cycle
- **Security:** Least-privilege permissions (contents:read, packages:write), no hardcoded secrets
- **Registry:** GitHub Container Registry (ghcr.io/jpapiez/printfarmer-*)

**Agent-29: Monolith Docker Infrastructure (197s)**
- **Dockerfile Stage:** New `monolith-runtime` in Dockerfile.multistage (inherits api-runtime, copies frontend build)
- **Compose Template:** docker-compose.monolith.yml (single service, SQLite default, 6 volumes)
- **CI/CD Integration:** Enabled monolith-runtime target in docker-publish.yml matrix
- **Consequence:** ~500MB memory savings, zero database configuration needed

### Related Decisions Finalized
- **Decision 1:** GHCR CI/CD Pipeline — automated releases, multi-arch support, semantic versioning
- **Decision 2:** Monolith Deployment Mode Infrastructure — Docker stage + compose + CI/CD
- **Decision 3:** Pi 4 Deployment Analysis — comprehensive resource study, 3 deployment tiers

### Key Learnings for Parker
- **Multi-arch strategy:** QEMU + Docker Buildx for ARM64 builds, Pi 4 4GB is sweet spot
- **File hierarchy:** Dockerfile source of truth in `scripts/docker/dockerfiles/Dockerfile.multistage`
- **Tagging strategy:** Multiple tags per build (semver + SHA + main) for flexibility
- **Service resources:** API 200-400MB, PostgreSQL 100-200MB, Prometheus 100-150MB, OrcaSlicer 100-800MB
- **Storage critical:** USB 3 SSD mandatory (not MicroSD), SD card I/O is bottleneck for database
- **Recommended tier:** Pi 4 4GB for 1-5 printers, Pi 4 8GB for all services including slicer
- **Deployment profiles:** Lite (monolith), Standard (microservices + lite monitoring), Full (all services)
- Dockerfile source of truth: `scripts/docker/dockerfiles/Dockerfile.multistage`
- Compose templates: `scripts/docker/compose-templates/`
- Root docker-compose.yml and Dockerfile.multistage are gitignored generated artifacts
- Installer (`install.sh`) is self-contained — generates compose + .env + nginx + management script for end users who don't clone the repo
- macOS bash is 3.2 — never use `${var,,}`, associative arrays, or `grep -oP`; use `tr`, indexed arrays, and `sed` instead
- Installer defaults to SQLite (zero config) with `--db postgres` opt-in for power users
- `printfarmer.sh` management helper is generated alongside the compose file for beginner-friendly lifecycle commands
- Container image registry: `ghcr.io/jpapiez/printfarmer-{api,frontend}:TAG`
- LAN IP detection: `hostname -I` on Linux, `ifconfig` on macOS, `ip route` as fallback

## Pi 4 Deployment Analysis (2026-03-10)
- Conducted full service inventory across all compose templates (14 compose files identified)
- Analyzed resource requirements: API 200-400MB, PostgreSQL 100-200MB, Prometheus 100-150MB, Grafana 200-250MB, OrcaSlicer 100-150MB idle / 400-800MB during slicing
- .NET 10 ARM64 support confirmed as fully supported on Raspberry Pi 4
- Mapped three deployment tiers: 2GB (not recommended), 4GB (standard, recommended), 8GB (full features)
- Key finding: Pi 4 4GB is the **sweet spot** for PrintFarmer — supports API, discovery, lite monitoring without slicing
- Critical infrastructure: USB 3 SSD is **mandatory** (not MicroSD) — SD card I/O is bottleneck for database
- Identified three practical architectures: (A) shared Klipper/PrintFarmer, (B) separate Klipper + PrintFarmer Pi (recommended), (C) PrintFarmer + desktop slicing station
- Services to avoid on Pi 4: Elasticsearch/full ELK stack (1GB alone), multiple OrcaSlicer workers, OrcaSlicer on 2-4GB machines
- Printer discovery service: 70MB, safe to enable on 4GB, discovers Moonraker/PrusaLink/OctoPrint via TCP scan
- Monitoring recommendation: Lite stack (Prometheus + Grafana, 300MB total) not full ELK (1.2GB+)
- Networking: Gigabit Ethernet mandatory, WiFi unreliable for discovery scans
- Output: `.squad/decisions/inbox/parker-pi-deployment-analysis.md` — comprehensive 10-section analysis with checklist, summary table, and per-user recommendations


## GitHub Actions CI/CD Pipeline (2026-03-10)
- Created `.github/workflows/docker-publish.yml` for release pipeline to GHCR
- Multi-arch support: linux/amd64 and linux/arm64 for API and frontend
- Three image targets: printfarmer-api (api-runtime), printfarmer-frontend (frontend-runtime), printfarmer-monolith (TODO: awaiting Lambert)
- Tagging strategy: semantic versioning (v1.2.3 → v1.2.3, v1.2, v1, latest), SHA tags for main (sha-{short}), manual tags (manual-{sha})
- Build optimizations: QEMU for cross-platform, Docker buildx, GitHub Actions cache for layers
- Triggers: push to main, version tags (v*), manual workflow_dispatch
- Labels follow OCI spec: org.opencontainers.image.* metadata for registry
- Separate from existing containers.yml (scheduled daily builds with native compilation)
- Images pushed to: ghcr.io/{owner}/printfarmer-{api,frontend}:tag
- ARM64 support critical for Raspberry Pi 4 deployments (4GB/8GB models)
- Build args: BUILD_VERBOSITY=quiet, ASPNET_TAG=10.0-noble, NODE_TAG=24-alpine
- Summary job provides pull commands and build status in GitHub UI

## Monolith Deployment Mode (2026-03-11)
- Added `monolith-runtime` stage to `Dockerfile.multistage` after `frontend-runtime` stage
- Monolith stage inherits from `api-runtime` and adds React build from `frontend-build` stage
- COPY frontend dist to `/app/wwwroot/` for ASP.NET Core static file serving
- Sets `DEPLOYMENT_MODE=monolith` environment variable to trigger Lambert's conditional middleware
- Created `docker-compose.monolith.yml` template for single-container deployments
- Monolith compose: Single `printfarmer` service on port 80→5000, SQLite default, 6 volumes (data, gcode, models, profiles, uploads, keys)
- Updated `.github/workflows/docker-publish.yml` — enabled monolith image build target
- Monolith ideal for: Raspberry Pi 4/5, low-resource environments, simple home deployments
- Memory savings: ~500MB vs microservices (no nginx, no separate frontend container)
- Synced Dockerfile copies: source → dockerfiles/ → repo root (per Docker file hierarchy rules)
- Monolith serves both API and SPA from single process, no reverse proxy needed
- Image registry: `ghcr.io/{owner}/printfarmer-monolith:tag`

## Deployment Profile Selection (2026-03-12)

### What was built
- Added `--profile lite|standard|full` flag to `install.sh`
- Interactive profile menu with numbered selection (1/2/3) when no flag passed
- ARM auto-detection defaults to `lite` in both interactive and non-interactive modes
- Profile validation rejects invalid values with clear error message

### Profile → Infrastructure mapping
| Profile | Containers | DB Default | Compose template |
|---------|-----------|------------|-----------------|
| lite | 1 (monolith) | SQLite (locked) | Inline monolith |
| standard | 3 (api + frontend + nginx) | SQLite (chooseable) | Inline microservices |
| full | 7 (standard + pg + discovery + prometheus + grafana) | PostgreSQL (default) | Inline microservices + extras |

### Key design decisions
- `DB_EXPLICIT` flag tracks whether user passed `--db` on CLI, prevents profile from overriding explicit user choice
- Lite profile skips nginx config generation entirely (monolith serves directly on port 5000)
- Full profile generates `monitoring/prometheus/prometheus.yml` alongside compose file
- `DEPLOY_PROFILE` is stored in `.env` so upgrades know the active profile
- Interactive DB prompt is skipped for lite (locked to SQLite) and full (defaults to postgres)
- Health check wait uses correct container name per profile (printfarmer-monolith vs printfarmer-api)
- Backward compat: no `--profile` in non-interactive mode → standard (non-ARM) or lite (ARM)
- All compose generation is inline in install.sh (self-contained installer — no repo clone needed)

### Env vars and flags added
- `--profile lite|standard|full` CLI flag
- `PRINTFARMER_PROFILE` environment variable
- `DEPLOY_PROFILE` in generated `.env` file


## Agent-32: Deployment Profile Selection (2026-03-11, 395s)

**Work:** Added flexible deployment profile system to install.sh — three tiers (lite/standard/full) for heterogeneous deployments.

**Key features:**
- `--profile lite|standard|full` CLI flag + interactive menu
- ARM auto-detection defaults to lite on Raspberry Pi 4/5
- Profile stored in `.env` for upgrade awareness
- Inline compose generation (self-contained installer)
- Backward compatible (defaults to standard on non-ARM)

**Profile mapping:**
| Profile | Containers | DB | Notes |
|---------|-----------|-----|-------|
| lite | 1 (monolith) | SQLite (locked) | Monolith serves on port 5000 |
| standard | 3 (api+frontend+nginx) | SQLite (chooseable) | Microservices, port 80 |
| full | 7 (+ postgres + discovery + monitoring) | PostgreSQL | All services, dedicated database |

**Design decisions:**
1. Lite forces SQLite (no db container, no nginx) — ideal for Pi
2. Full defaults to PostgreSQL but respects `--db sqlite` override
3. ARM auto-defaults to lite in both interactive and non-interactive modes
4. Profile stored in .env to preserve on upgrades
5. All compose templates generated inline (installer is self-contained)
6. Backward compatible: no flag defaults to standard on non-ARM

**Team impact:**
- Lambert: DEPLOYMENT_MODE=monolith env var already wired; no API changes needed
- Quinn: Profile is infrastructure-only; no frontend changes
- Dallas: Full profile includes discovery + monitoring aligned with 3-tier architecture

**Integration:** Builds on monolith Docker infrastructure (Agent-29) and GHCR CI/CD (Agent-28).


## Obico ML API Integration (2026-03-13)

### Agent Work (Requested by Jeff Papiez)

**Task:** Set up Obico ML API as optional Docker Compose service for Feature #1 (AI Print Failure Detection).

**Background:** Obico ML API is an open-source Flask service that analyzes 3D printer camera images and detects print failures (spaghetti, adhesion loss, etc.). It exposes `POST /v1/detect` endpoint for failure detection with confidence scores.

**Investigation:**
- Researched Obico ML API Docker image: `thespaghettidetective/ml_api:base-1.4` (latest stable)
- Reviewed existing compose template patterns (discovery, spoolman, orcaslicer-worker)
- Studied compose-generator.sh merging logic and optional service inclusion
- Verified health check endpoint: `http://localhost:3333/hc/`

**What was built:**

1. **New compose template** — `scripts/docker/compose-templates/docker-compose.obico-ml.yml`
   - Service name: `obico-ml-api`
   - Image: `thespaghettidetective/ml_api:base-1.4`
   - Internal-only service (no host port mapping by default)
   - Health check: `curl -f http://localhost:3333/hc/`
   - Environment variables: `DEBUG`, `FLASK_APP`, optional `ML_API_TOKEN`
   - Resource limits: 2GB max memory, 2 CPUs (tunable via env vars)
   - Persistent volume: `obico-ml-model-cache` for ML model storage
   - Security: `cap_drop: ALL`, `cap_add: NET_BIND_SERVICE`, tmpfs for temp files
   - GPU support: Commented out NVIDIA runtime config for CPU-only default

2. **Compose generator updates** — `scripts/docker/compose-generator.sh`
   - Added `--include-obico-ml` / `--enable-obico-ml` flag
   - Added `INCLUDE_OBICO_ML="false"` default in parse_args()
   - Integrated merge logic after Spoolman (lines 781-791)
   - Updated usage help text

3. **Container versions** — `scripts/docker/container-versions.conf`
   - Added `OBICO_ML_IMAGE` with default `thespaghettidetective/ml_api:base-1.4`

**Environment Variables for API Integration:**
- `OBICO_ML_API_URL` — URL for PrintFarmer API to reach ML service (default: `http://obico-ml-api:3333`)
- `OBICO_ML_CONFIDENCE_THRESHOLD` — Detection threshold 0.0-1.0 (default: 0.7)
- `OBICO_ML_SCAN_INTERVAL` — Seconds between scans (default: 30)
- `OBICO_ML_DEBUG` — Enable Flask debug logging (default: False)
- `OBICO_ML_CPU_LIMIT` / `OBICO_ML_MEMORY_LIMIT` — Resource caps (default: 2 CPUs, 2GB)

**Testing:**
- Verified compose generator dry-run with `--include-obico-ml` flag
- Generated test compose to `/tmp/test-obico` and confirmed service inclusion
- Validated service definition, health check, and resource limits in generated YAML

**Key Design Decisions:**
1. **Internal-only service** — No host port by default (API connects via Docker DNS)
2. **CPU-only default** — GPU support commented out, opt-in for users with NVIDIA runtime
3. **Model cache volume** — Persistent storage for ML models (downloaded on first run)
4. **2GB memory default** — ML inference is memory-intensive, generous default prevents OOM
5. **Optional flag** — `--include-obico-ml` makes it fully opt-in, PrintFarmer works without it
6. **Follows existing patterns** — Matches spoolman/discovery service structure exactly

**Integration Points for Lambert (API Team):**
- Connect to `http://obico-ml-api:3333` from API service
- POST images to `/v1/detect` endpoint
- Handle JSON responses with failure detection results and confidence scores
- Respect `OBICO_ML_CONFIDENCE_THRESHOLD` environment variable
- Use `OBICO_ML_SCAN_INTERVAL` for monitoring loop timing

**Status:** ✅ Complete — Obico ML API service is ready for use. API integration work remains for Feature #1.

**Next Steps for Lambert:**
1. Add Obico ML client to API service dependencies
2. Implement camera image capture from printer backends
3. Send images to Obico ML API via HTTP POST
4. Process failure detection results and trigger alerts
5. Store detection history in database for analysis

## Learnings

### `pfdev` Service Aliases Must Resolve Through Compose Map (2026-03-25)

If `scripts/pfdev` exposes user-friendly aliases like `nginx`, the Docker commands inside `build`, `deploy`, and `redeploy` must use the mapped Compose service from `SERVICES`, not the raw user argument. In this repo that means `nginx` stays the public alias while the internal Compose target is `nginx-proxy`.

### Unified Docker Workflow (2026-03-17)

Merged `docker-publish.yml` (release pipeline, multistage Dockerfile) and `containers.yml` (native-build pipeline, daily schedule) into a single `docker-publish.yml`.

**Architecture of the unified pipeline:**
- **Native build path** (api, frontend, printer-discovery, orcaslicer-worker): .NET and React build natively on the runner via `build-dotnet` and `build-frontend` jobs, then artifacts are COPY'd into minimal containers. Faster builds, better caching, smaller images.
- **Multistage path** (monolith only): Uses `Dockerfile.multistage` with `monolith-runtime` target. Runs in parallel with native builds since it's self-contained (combines API + frontend in one image).
- **Triggers:** push to main/release, version tags (v*), daily schedule (midnight UTC), manual dispatch with optional tag suffix.
- **Tagging:** Comprehensive — semver (v1.2.3 → v1.2.3, v1.2, v1), branch names (main, release), SHA prefixes, manual tags, nightly schedule tags.
- **5 images total:** api, frontend, printer-discovery, orcaslicer-worker (amd64 only), monolith — all with ARM64 except orca.
- **ARM64 smoke test** retained from containers.yml — validates api, frontend, and discovery start on arm64.
- **OrcaSlicer base image** ensure job retained — triggers base image build if missing.

**Key decision:** Monolith can't use native build because it combines API + frontend in a single Docker stage. All other images benefit from native compilation speed.

**Deleted:** `.github/workflows/containers.yml` (fully superseded).

---

## Wave 1 Completion — Cross-Agent Updates

**2026-03-16 — POST-WAVE-1 INTEGRATION NOTES**

### From Lambert (Backend)
- ✅ Job Cost Calculation system complete
- 6 new cost API endpoints deployed
- **Action for DevOps:** Cost data is now available via REST API for monitoring/reporting integration

### From Ripley (Frontend)
- ✅ Notification Center UI complete
- Obico failures will surface as notifications when Feature #1 launches
- PWA install prompt integrated

### From Dallas (Lead)
- ✅ Five-Feature Workplan approved and sequenced
- Feature #1 (Obico Failure Detection) — primary backend task
- **Dependency:** Your Obico ML Docker service is foundation for Feature #1

**Impact:** Your Obico compose work enables Lambert's Feature #1 implementation
**Status:** Wave 1 infrastructure complete; Feature #1 backend work begins Wave 2

## Phase 1 Spaghetti Detection Delivery (2026-03-24)

**Status:** ✅ Implemented, tested, and pushed to development  
**Commit:** `53a2284f` — feat: spaghetti detection phase 1 delivery  
**Bead:** PFarm1-0xa (closed)

### Deliverables

**Team Coordination:**
- Consolidated phase 1 scope across Lambert (backend), Ripley (frontend), Kane (validation), Dallas (lead)
- Merged decision docs and skill extractions from all agents
- All 30+ files staged in single atomic commit with bead closure

**Backend Work (Lambert):**
- Auto-pause wired through `IBackendClientFactory` in `PrintFailureMonitorService`
- `FailureDetectionDto` enriched with `SnapshotUrl` from camera snapshot
- `FailureDetectionMonitorStatus` tracks real-time monitoring state (counts, last scan)
- Status endpoint now returns actual data instead of placeholder
- Extended controller tests for pause execution and status validation

**Frontend Work (Ripley):**
- `FailureDetectionEvent` type updated with `snapshotUrl` field
- Toast notification improved with confidence % and view-snapshot action
- Transient alert badges on both Compact and Detailed printer cards
- `FailureDetectionStatusCard` added to Settings → Monitoring section
- `useFailureDetectionAlert` hook manages alert state with 60s timeout
- Focused tests for status card and alerts (both passing)

**Validation (Kane):**
- All 1709 API tests passing (including new auto-pause tests)
- All 365 React tests passing (including new alert/status tests)
- No new lint/formatting issues
- SignalR end-to-end verified
- Auto-pause tested with all backend types

### Root Cause: `pfdev redeploy nginx` Error

**Diagnosis:** User tried to run `pfdev redeploy nginx` and got `no such service: nginx`.

**Root causes (3-part problem):**
1. **`pfdev` is not installed** — No alias or symlink; user needs `scripts/pf-dev.sh` directly or create alias
2. **`pf-dev.sh` has no `redeploy` command** — Only supports: bootstrap, start, stop, status, logs, test, clean
3. **Service name is `nginx-proxy`, not `nginx`** — Docker Compose service defined as `nginx-proxy` with container name `printfarmer-nginx-proxy`

**Context confusion:**
- `pf-dev.sh` is a local development helper (for native .NET + React dev servers)
- `scripts/deploy-docker.sh` is what has the `--redeploy` flag (for Docker Compose orchestration)
- If redeploying, user should use: `./scripts/deploy-docker.sh --redeploy`
- If restarting just nginx via compose: `docker-compose restart nginx-proxy`

**Not a codebase bug** — User was trying to use a non-existent command on a different tool. The script and services work correctly.

## Diagnosis: `scripts/pfdev` Service Name Mismatch (2026-03-25)

**Issue:** `pfdev redeploy nginx` fails with `no such service: nginx`

**Root Cause (Simple):**
- `scripts/pfdev` hardcodes `[nginx]="nginx"` in the SERVICES array (line 50)
- The actual Docker Compose file defines the service as `nginx-proxy` (lines 188–202 in `docker-compose.yml`)
- When `pfdev` calls `docker compose up -d nginx`, Docker correctly fails: service doesn't exist

**Evidence Chain:**
1. `scripts/pfdev` line 50: `[nginx]="nginx"` — expects service named `nginx`
2. `scripts/pfdev` line 113: `docker compose up -d --remove-orphans "$service"` — passes `nginx` to Docker
3. `docker-compose.yml` line 188: `nginx-proxy:` — actual service is named `nginx-proxy`
4. Docker error: `no such service: nginx` ✅ matches observed failure

**Why It Exists:**
- `pfdev` was written as a development helper for rebuilding individual services
- The SERVICES array is generic and was never synchronized with actual Compose file
- Both `docker-compose.yml` and the template `scripts/docker/compose-templates/docker-compose.yml` define service as `nginx-proxy` — this is the correct, deployed reality

**Discrepancy Detail:**
- `pfdev` SERVICES array expects: `api`, `frontend`, `orcaslicer-worker`, `printer-discovery`, `nginx`
- Actual `docker-compose.yml` provides: `database`, `api`, `frontend`, `nginx-proxy`, `printer-discovery`

**Observation:** The `pfdev` script appears outdated relative to the current Compose file structure. It's a development-only tool (for Docker Compose rebuilds), not the primary deployment mechanism. Users should use `deploy-docker.sh` for orchestrated deployments or `docker compose restart nginx-proxy` for quick restarts.

## Learnings

### UI Component Integration Pattern (2026-01-11)

**Context:** Ripley (Frontend) completed discoverability improvements for spaghetti detection monitoring.

**Components Shipped:**
- `FailureDetectionMonitoringBadge` — transient alert badge for card surfaces
- `FailureDetectionMonitoringOverlay` — modal-style failure context viewer with camera snapshot
- `PrinterCameraPreview` — snapshot widget with fallback to static URL (reliability improvement)
- Updated card integration points on both Compact and Detailed printer cards
- `usePrinterFailureDetectionStatus` hook for real-time state management

**Key Pattern Observed:**
- **Snapshot reliability:** Component now gracefully falls back from live camera to snapshot URL when live stream unavailable
- **Overlay composition:** Overlay handles both quick-view (badge click) and detailed inspection flows
- **Card integration:** Minimal invasiveness — badge slots into existing card layouts without restructuring

**Tests:** All 365 React tests passing after integration; no new linting issues after path casing and ESLint checks

**Commit:** `989b8f61` pushed to origin/development  
**Bead Status:** Work complete; separate nginx alias fix (pfdev → docker-compose service name) remains pending

**Decision for next phase:** Camera snapshot fallback strategy may be useful pattern for other printer backends (OctoPrint, PrusaLink, etc.) if they adopt similar monitoring visualizations.


## 2026-03-25: Spaghetti Detection & Camera Preview Reliability Push

**Date:** 2026-03-25T05:56:29Z  
**Mode:** Background (Orchestration)  
**Branch:** development  
**Commit:** 989b8f61  

### Summary
Successfully committed and pushed spaghetti detection discoverability improvements and camera preview reliability fixes to the development branch. All changes validated and staged for merge to main.

### Changes Completed
- Spaghetti detection backend integration with auto-pause wiring
- Camera preview URL enrichment in failure detection DTO
- Status endpoint return real monitoring state (monitored count, scan interval, threshold)
- Frontend toast improvements with snapshot preview
- Printer card warning badge for active detections
- Settings monitoring status card with live metrics

### Status
✅ **Complete & Ready for Review**
- All tests passing
- Code reviewed and staged
- Documentation updated
- Pending: nginx alias fix in scripts/pf-dev.sh (separate PR)

### Notes
- Separate PR pending for pf-dev.sh nginx alias enhancement
- Development branch fully validated
- Ready for merge to main after final review


## Learnings

### pfdev Should Not Generate docker-compose.yml (2026-03-14)
- **Problem:** `pfdev` was calling `ensure_generated_stack()` which regenerated `docker-compose.yml` on every build/deploy operation
- **Root cause:** Leftover logic from early development when pfdev had more responsibilities
- **Solution:** Replaced `ensure_generated_stack()` with `check_required_files()` that fails loudly with a clear error message pointing users to `./scripts/deploy-docker.sh`
- **Source of truth:** Only `deploy-docker.sh` should generate `docker-compose.yml`, `Dockerfile.multistage`, and `docker-entrypoint-config.sh`
- **Error message pattern:** When required generated files are missing, fail immediately with:
  - Clear list of missing files
  - Instruction to run deploy-docker.sh
  - Exit code 1
- **Benefits:**
  - Prevents pfdev from overwriting user's deploy-docker.sh configuration
  - Makes the deployment workflow more predictable
  - Reduces confusion about which script does what
- **Preserved functionality:** TLS certificate refresh logic for nginx/frontend (kept in `ensure_tls_certificates()`)
- **nginx alias fix preserved:** Service mapping `nginx:nginx-proxy` maintained in SERVICES array

### Shield Badge Refinement Landing (2026-03-25)
- **Scope:** Icon-only failure-detection badge refactor + overlay removal
- **Changes:**
  - `FailureDetectionMonitoringBadge.tsx` — removed pill border, inline label; now renders icon-only with tooltip
  - `CompactPrinterCard.tsx` / `DetailedPrinterCard.tsx` — removed `FailureDetectionMonitoringOverlay` (single source of truth)
  - Test coverage added: 6 focused tests, 3 integration tests
  - New skill file: `.squad/skills/icon-only-badges/SKILL.md` (pattern for header-only badges)
- **Squad State:** Updated `.squad/decisions.md` with two approval records (badge refinement + overlay migration), agent histories (Kane, Ripley) with assessment notes
- **Push Workflow:**
  - Staged all changes (component, tests, squad files, skill)
  - Commit: `7269ca5b` with detailed message covering color mapping, tooltip strategy, test coverage
  - Remote had new work; rebased cleanly with no conflicts
  - Final state: `development` branch up-to-date with `origin/development`
- **Key Insight:** Squad file updates (decisions.md, agent histories, skills) should travel with the feature commits they document. These provide context for future readers and trace back to implementation choices.
- **Pattern Verified:** Icon-only designs need strong tooltip + aria-label to avoid accessibility loss. Tooltip is NOT optional; it becomes the information source, not a convenience feature.
