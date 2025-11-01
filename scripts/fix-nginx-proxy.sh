#!/usr/bin/env zsh
# One-shot helper to fix nginx proxy port conflicts and start a bridge-mode nginx
# Usage: ./scripts/fix-nginx-proxy.sh [--http-port PORT] [--non-interactive]

set -euo pipefail

HTTP_PORT=${1:-${HTTP_PORT:-8080}}
NON_INTERACTIVE=${2:-${NON_INTERACTIVE:-false}}

PWD_ROOT="$(cd "$(dirname "${0}")/.." && pwd)"
cd "$PWD_ROOT"

info(){ echo "[INFO] $*" >&2 }
success(){ echo "[OK] $*" >&2 }
warn(){ echo "[WARN] $*" >&2 }
err(){ echo "[ERROR] $*" >&2 }

confirm() {
  if [ "$NON_INTERACTIVE" = "true" ]; then
    return 1
  fi
  read -r "REPLY?${1:-Proceed? [y/N]} " || true
  case "$REPLY" in
    [yY]|[yY][eE][sS]) return 0 ;;
    *) return 1 ;;
  esac
}

info "Fixing nginx proxy (HTTP port: ${HTTP_PORT})"

info "Current docker ps (names / ports):"
docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'

# Find container occupying the HTTP_PORT mapping
occupier=$(docker ps --format '{{.Names}}\t{{.Ports}}' | grep ":${HTTP_PORT}->" | awk -F"\t" '{print $1}' | head -1 || true)
if [ -n "$occupier" ]; then
  warn "Found container '$occupier' that maps host port ${HTTP_PORT}."
  if [ "$NON_INTERACTIVE" = "true" ]; then
    info "Non-interactive: stopping and removing $occupier"
    docker stop "$occupier" || true
    docker rm -f "$occupier" || true
  else
    if confirm "Stop and remove container $occupier so nginx can bind ${HTTP_PORT}? [y/N]"; then
      info "Stopping $occupier"
      docker stop "$occupier" || true
      docker rm -f "$occupier" || true
    else
      err "User declined to stop $occupier. Aborting."
      exit 1
    fi
  fi
else
  info "No docker container mapping host port ${HTTP_PORT} detected. Checking for other processes..."
  if ss -ltnp | egrep ":${HTTP_PORT}\\b" >/dev/null 2>&1; then
    err "Host port ${HTTP_PORT} appears bound by a non-container process. Free it and re-run."
    ss -ltnp | egrep ":${HTTP_PORT}\\b" || true
    exit 1
  fi
fi

# Remove any stale proxy container
if docker ps -a --format '{{.Names}}' | grep -q '^printfarmer-nginx-proxy$'; then
  info "Removing existing printfarmer-nginx-proxy container"
  docker rm -f printfarmer-nginx-proxy || true
fi

info "Starting nginx proxy (bridge mode) with host-gateway mapping..."
docker run -d --name printfarmer-nginx-proxy \
  --add-host=host.docker.internal:host-gateway \
  -p "${HTTP_PORT}:80" \
  -v "${PWD_ROOT}/deploy/nginx/conf.d.host:/etc/nginx/conf.d:ro" \
  -v "${PWD_ROOT}/deploy/nginx/nginx-frontend.conf:/etc/nginx/nginx.conf:ro" \
  nginx:alpine >/dev/null

sleep 2

info "Validating proxy by querying /healthz through proxy..."
if curl -sS --max-time 5 "http://localhost:${HTTP_PORT}/healthz" >/dev/null 2>&1; then
  success "Proxy validated: /healthz returned 200 via proxy"
  exit 0
else
  err "Proxy validation failed. Collecting diagnostics..."
  echo "--- nginx logs (last 200 lines) ---"
  docker logs printfarmer-nginx-proxy --tail 200 || true
  echo "--- nginx config (if container running) ---"
  if docker ps --format '{{.Names}}' | grep -q '^printfarmer-nginx-proxy$'; then
    docker exec printfarmer-nginx-proxy nginx -T 2>/dev/null | sed -n '1,240p' || true
  fi
  echo "--- docker ps snapshot ---"
  docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}' || true
  exit 2
fi
