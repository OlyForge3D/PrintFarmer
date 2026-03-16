---
description: "Use when working on scripts"
applyTo: "scripts/**"
---

---
description: 'PrintFarmer scripts area: deployment automation, local dev helpers, Docker compose generation, and utility tooling'
applyTo: 'scripts/**'
---

# PrintFarmer Scripts

This directory contains all build, deployment, local development, and maintenance automation for PrintFarmer. Scripts are **not** part of the `src/` build system — they run from the repo root or `scripts/` itself.

## Directory Layout

| Path | Purpose |
|---|---|
| `scripts/common-utils.sh` | **Source-of-truth shared library** — logging helpers, health checks, port utils, wait-for-service loops, admin creation. Source this in every new shell script. |
| `scripts/docker-utils.sh` | Docker-specific helpers (image/container cleanup, audit logging). Source alongside `common-utils.sh`. |
| `scripts/deploy-docker.sh` | Main interactive/non-interactive Docker deployment script. |
| `scripts/pf-dev.sh` | Unified local dev helper (`bootstrap`, `start`, `stop`, `status`, `logs`, `test`, `clean`). Preferred over ad-hoc start scripts. |
| `scripts/lib_verify_worker.sh` | Shared library for worker verification scripts (PrusaSlicer, OrcaSlicer). Define required variables then `source` it. |
| `scripts/bump-version.sh` | Bumps `VERSION` file: `./scripts/bump-version.sh <major\|minor\|patch>`. |
| `scripts/docker/` | Docker tooling sub-tree (see [docker-file-hierarchy.instructions.md](../../../.github/instructions/docker-file-hierarchy.instructions.md)) |
| `scripts/docker/container-versions.conf` | **Single source of truth** for all container image version tags. Always edit here, never inline in Dockerfiles or compose files. |
| `scripts/docker/compose-templates/` | Source compose fragments merged by `compose-generator.sh`. Never edit the root `docker-compose.yml` directly. |
| `scripts/docker/compose-replace-db.py` | Python helper using `ruamel.yaml` to splice database service into generated compose (requires `pip install ruamel.yaml`). |
| `scripts/lint/` | Lightweight lint helpers (e.g., compose version check). |
| `scripts/admin/` | Platform admin scripts (Docker reinstall, etc.). |

## Tech Stack

- **Shell**: Bash with `set -euo pipefail` on every script. Follow [shell.instructions.md](../../../.github/instructions/shell.instructions.md).
- **Python 3**: Used for YAML manipulation (`ruamel.yaml`) and OrcaSlicer profile extraction. Must have `ruamel.yaml` installed — missing it causes silent deploy failures.
- **Node.js (CJS)**: `check-path-casing.js`, `validate-openapi.js`, `restore-orcaslicer-assets.js` — small utility scripts, no separate `package.json`.
- **PowerShell**: `compose-generator.ps1`, `deploy-docker.ps1`, `bootstrap-windows.ps1` for Windows support.
- **C# scripts**: `test_perimeters_regex.csx`, `test_probe.csx` — run with `dotnet script`.
- **`envsubst`**: Used in `compose-generator.sh` to inject `container-versions.conf` values into compose templates.

## Conventions

- **Always source shared libraries** at the top of new shell scripts:
  ```bash
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  source "$SCRIPT_DIR/common-utils.sh"
  ```
- **Logging**: Use `log_info`, `log_success`, `log_warn`, `log_error`, `log_header` from `common-utils.sh` — never raw `echo` for status messages. Aliases `print_info/print_success/print_warning/print_error` also exist for backward compatibility.
- **Version tags**: Always read from `scripts/docker/container-versions.conf` via `source`. Never hardcode image tags.
- **Library-only scripts** (`docker-utils.sh`, `lib_verify_worker.sh`, `common-utils.sh`) must guard against direct execution and exit with an error if run as `$0`.
- **Compose templates** are in `scripts/docker/compose-templates/`. The generated `docker-compose.yml` at the repo root is disposable — always edit the templates.
- **Worker verify scripts** (`verify-*.sh`) must define required variables (`WORKER_NAME`, `ENV_PREFIX`, etc.) before sourcing `lib_verify_worker.sh`.

## Key Commands

```bash
# Local development
./scripts/pf-dev.sh bootstrap   # Restore dotnet + npm deps
./scripts/pf-dev.sh start       # Start API + React in background
./scripts/pf-dev.sh stop        # Stop dev processes
./scripts/pf-dev.sh test        # Run all tests

# Docker deployment
./scripts/deploy-docker.sh                          # Interactive
./scripts/deploy-docker.sh --non-interactive --auto-admin --auto-admin-password=Secret1!

# Compose generation (called by deploy-docker.sh automatically)
./scripts/docker/compose-generator.sh

# Version bump
./scripts/bump-version.sh patch
```
