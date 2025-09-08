#!/usr/bin/env bash
set -euo pipefail

# Enhanced verification script for the orcaslicer-worker container.
#
# Usage:
#   scripts/verify-orcaslicer-worker.sh [mode] [image] [container]
#   scripts/verify-orcaslicer-worker.sh --help
#
# Modes:
#   allow-stub     (default) Accept a stub binary; skips readiness enforcement
#   require-real   Enforce real binary (size > 2KB AND --help succeeds) and readiness OK with orca_binary Healthy
#
# Arguments precedence:
#   1) Explicit CLI args override
#   2) Environment variables (ORCA_IMAGE, ORCA_CONTAINER, ORCA_MODE)
#   3) Built-in defaults (image=orcaslicer-worker, container=pfarm_verify_orca, mode=allow-stub)
#
# Environment variables:
#   ORCA_IMAGE       Image name/tag to use (e.g. orcaslicer-worker:dev)
#   ORCA_CONTAINER   Container name override
#   ORCA_MODE        Mode (allow-stub | require-real)
#
# Exit codes: (shared via lib_verify_worker.sh)
#   0  success
#   2  docker missing
#   3  container start failed
#   4  liveness timeout
#   5  binary missing
#   6  stub not allowed in require-real
#   7  --help failed in require-real
#   8  readiness non-200
#   9  orca_binary check missing
#   10 orca_binary not healthy
#   11 readiness is relaxed while require-real requested
#   12 jq required for strict JSON parsing but not available (reserved)
#
# Notes:
#   - In allow-stub mode we tolerate stub & skip readiness.
#   - In require-real mode we parse readiness JSON (jq) if available for robust validation; fall back to legacy grep if jq absent.
#   - If WORKER_RELAXED_READINESS=true inside the container and require-real is chosen, the script will fail (code 11).

if [[ ${1-} == "--help" || ${1-} == "-h" ]]; then
  grep '^#' "$0" | sed 's/^# \{0,1\}//'
  exit 0
fi

WORKER_NAME="orcaslicer-worker"
ENV_PREFIX="ORCA"
DEFAULT_IMAGE="orcaslicer-worker"
DEFAULT_CONTAINER="pfarm_verify_orca"
DEFAULT_MODE="allow-stub"
BINARY_PATH="/usr/local/bin/orcaslicer"
HEALTH_KEY="orca_binary"
LOG_PREFIX="orca"
SIZE_THRESHOLD=2048

source "$(dirname "$0")/lib_verify_worker.sh"

vw_require_docker
vw_parse_args "$@"
vw_start_container
vw_wait_liveness
vw_assess_binary
vw_invoke_help
if [ "$MODE" = "require-real" ]; then
  vw_check_readiness
else
  vw_log "Skipping readiness enforcement in allow-stub mode"
fi
vw_log "SUCCESS: ${WORKER_NAME} passes verification (mode=${MODE})."
