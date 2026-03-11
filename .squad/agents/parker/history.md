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

