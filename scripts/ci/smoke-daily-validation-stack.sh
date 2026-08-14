#!/usr/bin/env bash
# scripts/ci/smoke-daily-validation-stack.sh
#
# Boots the six-image daily validation stack (PostgreSQL, API, frontend,
# slicer-host, printer-discovery, exactly one OrcaSlicer worker, and four
# isolated instances of the repository-built Moonraker protocol emulator
# image — moonraker-ready, moonraker-printing, moonraker-paused, and
# moonraker-shutdown) and asserts that:
#   - every core service becomes healthy;
#   - all four Moonraker emulator instances are healthy;
#   - the API reports printers whose backend is the real "Moonraker" plugin
#     (proof that seeding used the emulator, not the in-process TestEmulator
#     plugin), including the "Moonraker Offline" printer, which is seeded
#     against a hostname with no running listener and must therefore report
#     online == false;
#   - a printer-discovery scan (autoRegister=false) proves the deterministic
#     discovery contract: it finds the Voron and Prusa fixture entries with
#     the expected hostname/backend fields. The scan itself does not contact
#     moonraker-discovery-voron/-prusa or perform any Moonraker handshake —
#     those hostnames are network aliases of moonraker-ready so that a
#     printer subsequently added from a discovered candidate connects for
#     real via the unchanged backend plugin (covered by UI add/card E2E, not
#     this script);
#   - exactly one OrcaSlicer worker container is running (the emulator
#     instances are intentional replicas of one image and are not subject to
#     this "exactly one" rule).
#
# Docker availability:
#   This script requires a reachable Docker daemon. If `docker` is not
#   installed, or the daemon is not reachable, it prints an explicit SKIP
#   message and exits 0 so it can be wired into local unit test suites
#   without failing Docker-less environments (for example CI runners that
#   validate this repository's shell/Node tests without a Docker host).
#   Once the stack has booted, every assertion below is fatal: a failure
#   prints diagnostic container logs and exits non-zero.
#
# Image selection:
#   Export the six PRINTFARMER_*_IMAGE variables described in
#   docs/DAILY_DEVELOPMENT_IMAGES.md to smoke test an exact digest-pinned
#   daily image set pulled from GHCR. If any of the six are unset, the
#   script instead builds every image locally from the current worktree
#   (useful during development of this deployment integration itself).
#
# Usage:
#   scripts/ci/smoke-daily-validation-stack.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEMPLATES_DIR="$REPO_ROOT/scripts/docker/compose-templates"
source "$REPO_ROOT/scripts/docker/container-versions.conf"
export PRINTFARMER_BUILD_CONTEXT="${PRINTFARMER_BUILD_CONTEXT:-$REPO_ROOT}"

log() {
  printf '[smoke-daily-validation-stack] %s\n' "$1"
}

# --- Docker-unavailable behavior is explicit and non-fatal (see header). ---
if ! command -v docker >/dev/null 2>&1; then
  log "SKIP: docker is not installed in this environment; smoke validation cannot run."
  exit 0
fi

if ! docker info >/dev/null 2>&1; then
  log "SKIP: docker daemon is not reachable in this environment; smoke validation cannot run."
  exit 0
fi

STACK_DIR="$(mktemp -d)"
PROJECT_NAME="printfarmer-smoke-$$"

USE_REGISTRY="false"
if [[ -n "${PRINTFARMER_API_IMAGE:-}" && -n "${PRINTFARMER_FRONTEND_IMAGE:-}" \
      && -n "${PRINTFARMER_SLICER_HOST_IMAGE:-}" && -n "${PRINTFARMER_PRINTER_DISCOVERY_IMAGE:-}" \
      && -n "${PRINTFARMER_ORCASLICER_WORKER_IMAGE:-}" \
      && -n "${PRINTFARMER_MOONRAKER_EMULATOR_IMAGE:-}" ]]; then
  USE_REGISTRY="true"
fi

COMPOSE_FILES=(-f "$STACK_DIR/docker-compose.yml")
if [[ "$USE_REGISTRY" == "true" ]]; then
  COMPOSE_FILES+=(-f "$TEMPLATES_DIR/docker-compose.daily-registry.yml")
fi
COMPOSE_FILES+=(-f "$TEMPLATES_DIR/docker-compose.daily-validation.yml")

compose() {
  docker compose --project-name "$PROJECT_NAME" "${COMPOSE_FILES[@]}" "$@"
}

cleanup() {
  local cleanup_image
  cleanup_image="$(compose images -q api 2>/dev/null | head -n 1 || true)"
  compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  if [[ -n "$cleanup_image" && -d "$STACK_DIR/.volumes" ]]; then
    docker run --rm --network none --user 0:0 \
      -v "$STACK_DIR:/cleanup" \
      --entrypoint /bin/sh \
      "$cleanup_image" \
      -c 'rm -rf /cleanup/.volumes' >/dev/null 2>&1 || true
  fi
  rm -rf "$STACK_DIR" || true
}
trap cleanup EXIT

: "${POSTGRES_PASSWORD:=$(openssl rand -base64 24)}"
: "${POSTGRES_USER:=printfarmer}"
: "${Jwt__Key:=$(openssl rand -base64 48)}"
: "${WORKER_SHARED_API_KEY:=$(openssl rand -hex 32)}"
: "${DISCOVERY_SHARED_API_KEY:=$(openssl rand -hex 32)}"
: "${ConnectionStrings__Default:=Host=database;Port=5432;Database=printfarmer;Username=printfarmer;Password=$POSTGRES_PASSWORD}"
: "${API_PORT:=15245}"
: "${SLICER_HOST_PORT:=15246}"
: "${HTTP_PORT:=18080}"
: "${HTTPS_PORT:=18443}"
: "${POSTGRES_PORT:=15432}"
: "${MOONRAKER_EMULATOR_PORT:=17125}"
: "${MOONRAKER_EMULATOR_PRINTING_PORT:=17126}"
: "${MOONRAKER_EMULATOR_PAUSED_PORT:=17127}"
: "${MOONRAKER_EMULATOR_SHUTDOWN_PORT:=17128}"
export POSTGRES_PASSWORD POSTGRES_USER Jwt__Key WORKER_SHARED_API_KEY DISCOVERY_SHARED_API_KEY \
  ConnectionStrings__Default API_PORT SLICER_HOST_PORT HTTP_PORT HTTPS_PORT POSTGRES_PORT \
  MOONRAKER_EMULATOR_PORT MOONRAKER_EMULATOR_PRINTING_PORT MOONRAKER_EMULATOR_PAUSED_PORT \
  MOONRAKER_EMULATOR_SHUTDOWN_PORT
export DB_PROVIDER=Postgres
export ENABLE_DISTRIBUTED_SLICING=true
export ENABLE_ORCA_WORKER=yes
export ORCA_WORKER_COUNT=1

log "Generating microservices stack (registry=$USE_REGISTRY, project=$PROJECT_NAME) in $STACK_DIR"
"$REPO_ROOT/scripts/docker/compose-generator.sh" \
  --architecture microservices \
  --db-provider postgres \
  --enable-orca-worker yes \
  --include-discovery \
  --include-moonraker-emulator \
  --exclude-monitoring \
  --exclude-telemetry \
  --output-dir "$STACK_DIR"

mkdir -p "$STACK_DIR/deploy"
cp -R "$REPO_ROOT/deploy/nginx" "$STACK_DIR/deploy/nginx"
"$REPO_ROOT/scripts/generate-certs.sh" "$STACK_DIR/deploy/nginx/certs"

if [[ "$USE_REGISTRY" == "true" ]]; then
  log "Pulling the exact digest-pinned daily image set"
  compose pull
  compose up -d --scale orcaslicer-worker=1
else
  log "Building images locally from the current worktree (no PRINTFARMER_*_IMAGE set)"
  compose up -d --build --scale orcaslicer-worker=1
fi

wait_for_health() {
  local url="$1" label="$2" attempts="${3:-90}"
  local i
  for ((i = 0; i < attempts; i++)); do
    if curl --fail --silent --show-error "$url" >/dev/null 2>&1; then
      log "OK: $label is healthy ($url)"
      return 0
    fi
    sleep 5
  done
  log "FAIL: $label did not become healthy at $url"
  compose logs --no-color
  return 1
}

wait_for_health "http://localhost:${API_PORT}/healthz" "API"
wait_for_health "http://localhost:${MOONRAKER_EMULATOR_PORT}/healthz" "Moonraker emulator (ready)"
wait_for_health "http://localhost:${MOONRAKER_EMULATOR_PRINTING_PORT}/healthz" "Moonraker emulator (printing)"
wait_for_health "http://localhost:${MOONRAKER_EMULATOR_PAUSED_PORT}/healthz" "Moonraker emulator (paused)"
wait_for_health "http://localhost:${MOONRAKER_EMULATOR_SHUTDOWN_PORT}/healthz" "Moonraker emulator (shutdown)"
wait_for_health "http://localhost:${HTTP_PORT}/" "nginx-proxy/frontend"

log "Creating an isolated validation administrator and authenticating through the real API"
smoke_admin_username="daily-smoke-admin"
smoke_admin_password="Sm0ke!$(openssl rand -hex 24)Aa1"
setup_status="$(curl --fail --silent --show-error "http://localhost:${API_PORT}/api/setup/status")"
if [[ "$(printf '%s' "$setup_status" | jq -r '.needsSetup')" == "true" ]]; then
  setup_payload="$(jq -n \
    --arg username "$smoke_admin_username" \
    --arg password "$smoke_admin_password" \
    '{
      username: $username,
      password: $password,
      email: "daily-smoke-admin@printfarmer.local",
      firstName: "Daily",
      lastName: "Validation"
    }')"
  setup_response="$(curl --fail --silent --show-error \
    -H 'Content-Type: application/json' \
    --data-binary "$setup_payload" \
    "http://localhost:${API_PORT}/api/setup/initial-admin")"
  if [[ "$(printf '%s' "$setup_response" | jq -r '.success')" != "true" ]]; then
    log "FAIL: API rejected the isolated validation administrator"
    printf '%s\n' "$setup_response"
    exit 1
  fi
fi

login_payload="$(jq -n \
  --arg username "$smoke_admin_username" \
  --arg password "$smoke_admin_password" \
  '{ usernameOrEmail: $username, password: $password }')"
login_response="$(curl --fail --silent --show-error \
  -H 'Content-Type: application/json' \
  --data-binary "$login_payload" \
  "http://localhost:${API_PORT}/api/auth/login")"
smoke_auth_token="$(printf '%s' "$login_response" | jq -r '.token // empty')"
if [[ -z "$smoke_auth_token" ]]; then
  log "FAIL: API login did not return an access token"
  exit 1
fi
log "OK: isolated validation administrator authenticated"

log "Verifying seeded printers use the real Moonraker backend (not TestEmulator)"
printers_json="[]"
moonraker_count=0
for _ in {1..30}; do
  printers_json="$(curl --fail --silent --show-error \
    -H "Authorization: Bearer $smoke_auth_token" \
    "http://localhost:${API_PORT}/api/printers")"
  moonraker_count="$(printf '%s' "$printers_json" | jq '[.[] | select(.backend == "Moonraker")] | length')"
  if [[ "$moonraker_count" -ge 4 ]]; then
    break
  fi
  sleep 2
done

# Five deterministic printers are seeded by default (Ready/Printing/Paused/
# Shutdown/Offline). Only the first four have a running emulator instance;
# "Offline" is seeded against a hostname with no listener on purpose, so it
# must report online == false rather than being absent from this backend
# count.
if [[ "$moonraker_count" -lt 4 ]]; then
  log "FAIL: expected at least four printers with backend == \"Moonraker\", found $moonraker_count"
  printf '%s\n' "$printers_json"
  compose logs api --no-color
  exit 1
fi
log "OK: found $moonraker_count printer(s) backed by the Moonraker plugin"

test_emulator_count="$(printf '%s' "$printers_json" | jq '[.[] | select(.backend == "TestEmulator")] | length')"
if [[ "$test_emulator_count" -ne 0 ]]; then
  log "FAIL: expected zero TestEmulator-backed printers in daily validation, found $test_emulator_count"
  printf '%s\n' "$printers_json"
  exit 1
fi
log "OK: no TestEmulator-backed printers present"

log "Verifying the seeded offline printer reports online == false (no listener behind moonraker-offline)"
offline_is_online="true"
for _ in {1..30}; do
  printers_json="$(curl --fail --silent --show-error \
    -H "Authorization: Bearer $smoke_auth_token" \
    "http://localhost:${API_PORT}/api/printers")"
  offline_is_online="$(printf '%s' "$printers_json" | jq -r '
    [.[] | select(.name == "Moonraker Offline")]
    | if length == 0 then "missing" else (.[0].isOnline | tostring) end
  ')"
  if [[ "$offline_is_online" == "false" ]]; then
    break
  fi
  sleep 2
done
if [[ "$offline_is_online" != "false" ]]; then
  log "FAIL: expected the seeded \"Moonraker Offline\" printer to report isOnline == false, found: $offline_is_online"
  printf '%s\n' "$printers_json"
  exit 1
fi
log "OK: \"Moonraker Offline\" printer correctly reports isOnline == false"

log "Verifying the deterministic discovery contract (Voron/Prusa fixtures); the scan itself does not contact the emulator"
discovery_json=""
for _ in {1..30}; do
  if discovery_json="$(compose exec -T printer-discovery curl --fail --silent --show-error -X POST 'http://localhost:5247/api/discovery/scan?autoRegister=false' 2>/dev/null)"; then
    break
  fi
  sleep 2
done
if [[ -z "$discovery_json" ]]; then
  log "FAIL: printer-discovery scan did not respond"
  compose logs printer-discovery --no-color
  exit 1
fi

voron_found="$(printf '%s' "$discovery_json" | jq '[.[] | select(.hostname == "Discovered Voron V2.4" and .printerBackend == "moonraker")] | length')"
prusa_found="$(printf '%s' "$discovery_json" | jq '[.[] | select(.hostname == "Discovered Prusa MK4S" and .printerBackend == "moonraker")] | length')"
if [[ "$voron_found" -lt 1 || "$prusa_found" -lt 1 ]]; then
  log "FAIL: expected discovery scan to find both Voron and Prusa Moonraker fixtures (voron=$voron_found, prusa=$prusa_found)"
  printf '%s\n' "$discovery_json"
  exit 1
fi
log "OK: discovery scan returned both deterministic fixture entries (Voron, Prusa); this proves the discovery contract, not a live connection to the emulator"

log "Verifying exactly one OrcaSlicer worker is running"
worker_count="$(compose ps --status running --format '{{.Service}}' orcaslicer-worker | grep -c '^orcaslicer-worker$' || true)"
if [[ "$worker_count" -ne 1 ]]; then
  log "FAIL: expected exactly one running orcaslicer-worker container, found $worker_count"
  compose ps
  exit 1
fi
log "OK: exactly one OrcaSlicer worker is running"

log "SUCCESS: daily validation stack smoke checks passed"
