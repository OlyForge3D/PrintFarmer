#!/usr/bin/env bash
set -euo pipefail

# PrintFarmer Dev Stack (SQLite + API + Frontend + OrcaSlicer Worker)
# Avoids nginx proxy by pointing frontend directly at API (port 5001).
#
# Usage:
#   ./scripts/dev-stack-orca.sh up        # build (if needed) and start services
#   ./scripts/dev-stack-orca.sh build     # build images only
#   ./scripts/dev-stack-orca.sh down      # stop and remove containers (keeps volumes)
#   ./scripts/dev-stack-orca.sh destroy   # stop and remove containers + volumes
#   ./scripts/dev-stack-orca.sh logs [svc]# tail logs for all or a specific service
#   ./scripts/dev-stack-orca.sh restart   # restart running services
#   ./scripts/dev-stack-orca.sh ps        # list stack containers
#
# Services started:
#   redis, api, frontend, orcaslicer-worker
# Profiles: enables only 'orca'

COMPOSE_PROFILES="orca"
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")"/.. && pwd)"
COMPOSE_FILE="${PROJECT_ROOT}/docker-compose.yml"

SERVICES=(redis api frontend orcaslicer-worker)

banner() { echo "[dev-stack-orca] $*"; }

cmd=${1:-help}
shift || true

compose() {
  (cd "$PROJECT_ROOT" && COMPOSE_PROFILES="$COMPOSE_PROFILES" docker compose "$@")
}

ensure_compose_file() {
  if ! grep -q 'VITE_API_BASE_URL=http://localhost:5001/api' "$COMPOSE_FILE"; then
    banner "WARNING: Frontend still points to proxy (8080). Update compose or rebuild frontend." >&2
  fi
}

case "$cmd" in
  up)
    ensure_compose_file
  banner "Building base slicer image"
  compose build slicer-base
  banner "Building (if required) and starting services: ${SERVICES[*]}"
  compose up -d slicer-base "${SERVICES[@]}"
    banner "Stack started. Health checks:" \
      && echo "  API:      http://localhost:5001/healthz" \
      && echo "  Frontend: http://localhost:3000/" \
      && echo "  Orca:     http://localhost:8081/healthz"
    ;;
  build)
    ensure_compose_file
  banner "Building slicer-base first (profile: $COMPOSE_PROFILES)"
  compose build slicer-base
  banner "Building service images (profile: $COMPOSE_PROFILES)"
  compose build "${SERVICES[@]}"
    ;;
  down)
    banner "Stopping stack (keeping volumes)"
    compose down
    ;;
  destroy)
    banner "Destroying stack and volumes"
    compose down -v
    ;;
  logs)
    if [ $# -gt 0 ]; then
      compose logs -f "$1"
    else
      compose logs -f
    fi
    ;;
  restart)
    banner "Restarting services"
    compose restart "${SERVICES[@]}"
    ;;
  ps)
    compose ps
    ;;
  help|--help|-h)
    sed -n '1,40p' "$0" | sed 's/^# \{0,1\}//'
    ;;
  *)
    echo "Unknown command: $cmd" >&2
    exit 1
    ;;
esac
