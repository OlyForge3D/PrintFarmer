#!/bin/bash
# Regenerate an existing deployment, recreate discovery, then check bridge paths.
# Usage: fix-discovery-heartbeat.sh [deployment-directory [env-file [config-file]]]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../common-utils.sh"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DEPLOYMENT_DIR="$(cd "${1:-$REPO_ROOT}" && pwd)"
ENV_FILE="${2:-$DEPLOYMENT_DIR/.env}"
CONFIG_FILE="${3:-$DEPLOYMENT_DIR/.deploy-config}"
cd "$REPO_ROOT"

if [[ ! -f "$CONFIG_FILE" || ! -f "$ENV_FILE" ]]; then
    log_error "Existing saved configuration and environment files are required; run deploy-docker.sh interactively for a new deployment."
    exit 1
fi

log_info "Regenerating with saved deployment settings; this can update other services."
if ! bash "$REPO_ROOT/scripts/deploy-docker.sh" --non-interactive --include-discovery \
    --config-file "$CONFIG_FILE" --env-file "$ENV_FILE" --output-dir "$DEPLOYMENT_DIR"; then
    log_error "Deployment regeneration failed; discovery repair is incomplete."
    exit 1
fi

log_info "Recreating discovery from generated Compose (a restart cannot change isolation)."
if ! docker compose --env-file "$ENV_FILE" -f "$DEPLOYMENT_DIR/docker-compose.yml" \
    up -d --no-deps --force-recreate --wait --wait-timeout 120 printer-discovery; then
    log_error "Discovery did not become healthy within 120 seconds. Check API readiness and local service logs; redact credentials before sharing."
    exit 1
fi
bash "$SCRIPT_DIR/verify-discovery-service.sh" "$DEPLOYMENT_DIR" "$ENV_FILE"
