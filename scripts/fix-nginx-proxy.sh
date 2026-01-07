#!/usr/bin/env bash
# fix-nginx-proxy.sh - frees host HTTP port and launches nginx proxy using host-gateway mapping
# Usage: ./scripts/fix-nginx-proxy.sh [HTTP_PORT] [NON_INTERACTIVE]

set -euo pipefail

HTTP_PORT="${1:-${HTTP_PORT:-8080}}"
NON_INTERACTIVE="${2:-${NON_INTERACTIVE:-false}}"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

log() { printf '[%s] %s\n' "$1" "$2" >&2; }
info() { log INFO "$*"; }
ok() { log OK "$*"; }
warn() { log WARN "$*"; }
err() { log ERROR "$*"; }

prompt_yes() {
  if [[ "$NON_INTERACTIVE" == "true" ]]; then
    return 1
  fi
  read -r -p "$1 [y/N]: " ans || true
  case "$ans" in
    [yY]|[yY][eE][sS]) return 0 ;;
    *) return 1 ;;
  esac
}

info "Checking containers and host port ${HTTP_PORT}"
docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'

# determine if any container publishes the desired host port
occupier="$(docker ps --format '{{.Names}}\t{{.Ports}}' | awk -v p=":${HTTP_PORT}->" -F"\t" '$2 ~ p {print $1; exit}' || true)"

if [[ -n "$occupier" ]]; then
  warn "Container '$occupier' currently maps host port ${HTTP_PORT}"
  if [[ "$NON_INTERACTIVE" == "true" ]]; then
    info "Stopping and removing $occupier"
    docker stop "$occupier" >/dev/null 2>&1 || true
    docker rm -f "$occupier" >/dev/null 2>&1 || true
  else
    if prompt_yes "Stop and remove $occupier so nginx can bind ${HTTP_PORT}?"; then
      info "Stopping $occupier"
      docker stop "$occupier" >/dev/null 2>&1 || true
      docker rm -f "$occupier" >/dev/null 2>&1 || true
    else
      err "User declined to free port ${HTTP_PORT}; aborting"
      exit 1
    fi
  fi
else
  info "No docker container maps port ${HTTP_PORT}; checking for other processes"
  if ss -ltnp 2>/dev/null | egrep ":${HTTP_PORT}\\b" >/dev/null; then
    err "Host port ${HTTP_PORT} is bound by a non-container process; free it and re-run"
    ss -ltnp 2>/dev/null | egrep ":${HTTP_PORT}\\b" || true
    exit 1
  fi
fi

if docker ps -a --format '{{.Names}}' | grep -q '^printfarmer-nginx-proxy$'; then
  info "Removing existing printfarmer-nginx-proxy container"
  docker rm -f printfarmer-nginx-proxy >/dev/null 2>&1 || true
fi

info "Starting nginx proxy (bridge) on host port ${HTTP_PORT}"
docker run -d --name printfarmer-nginx-proxy \
  --add-host=host.docker.internal:host-gateway \
  -p "${HTTP_PORT}:80" \
  -v "${ROOT_DIR}/deploy/nginx/conf.d:/etc/nginx/conf.d:ro" \
  -v "${ROOT_DIR}/deploy/nginx/nginx-frontend.conf:/etc/nginx/nginx.conf:ro" \
  nginx:alpine >/dev/null

sleep 2

info "Validating proxy via http://localhost:${HTTP_PORT}/healthz"
if curl -sS --max-time 5 "http://localhost:${HTTP_PORT}/healthz" >/dev/null 2>&1; then
  ok "Proxy validated"
  exit 0
fi

err "Proxy validation failed; gathering diagnostics"
echo "--- nginx logs (last 200 lines) ---"
docker logs printfarmer-nginx-proxy --tail 200 || true
if docker ps --format '{{.Names}}' | grep -q '^printfarmer-nginx-proxy$'; then
  echo "--- nginx config (truncated) ---"
  docker exec printfarmer-nginx-proxy nginx -T 2>/dev/null | sed -n '1,240p' || true
fi
echo "--- docker ps snapshot ---"
docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}' || true

exit 2
