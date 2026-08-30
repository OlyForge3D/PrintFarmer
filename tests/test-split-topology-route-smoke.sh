#!/usr/bin/env bash
# tests/test-split-topology-route-smoke.sh
#
# Issue #2239: live, authenticated route-ownership smoke for the generated
# split (microservices) topology. tests/test-compose-generator.sh already
# proves the generated nginx/compose *text* is correct (right upstream in
# the right location block); it never proves that a live request actually
# gets answered by the service the text says it should. This script closes
# that gap by standing up the real generated split stack (nginx + main API
# + slicer-host :5246 + one OrcaSlicer worker) and issuing real, JWT
# authenticated HTTP requests against it.
#
# For every slicer-owned namespace (/api/workers, /api/slicers,
# /api/slicer, /api/slice, /api/3d-models, /api/artifacts,
# /api/admin/slicer) this asserts BOTH directions:
#   - direct-to-main-API (bypassing nginx): the main API's own
#     SLICER_DISABLED catch-all (src/api/Program.cs) fires - i.e. the main
#     API genuinely does NOT serve this namespace in split/microservices
#     mode. This is the required NEGATIVE assertion.
#   - via nginx: the response does NOT carry the SLICER_DISABLED marker,
#     proving nginx routed the request to slicer-host instead. This is the
#     positive route-ownership proof.
# A single main-API-owned control route is also asserted via nginx to
# prove nginx isn't blanket-routing everything to slicer-host.
#
# It also proves a genuine WebSocket UPGRADE (HTTP/1.1 101 Switching
# Protocols) for both slicer SignalR hubs (/hubs/slicer-registry and
# /hubs/slicers) through nginx, using a raw HTTP handshake with a computed
# Sec-WebSocket-Accept - not merely a 200 on the SignalR negotiate
# endpoint, which would recreate the "text asserts text" gap this issue
# exists to close.
#
# Every HTTP call in this script uses a real JWT obtained through the
# documented setup/login flow (see .github/skills/api-debugging/SKILL.md);
# a 401/403 must never be mistaken for correct routing, so assertions key
# off the SLICER_DISABLED response marker rather than status codes alone.
#
# Docker availability:
#   Requires a reachable Linux Docker/Compose daemon. If docker is not
#   installed or the daemon is unreachable, this prints an explicit SKIP
#   and exits 0 (matching scripts/ci/smoke-daily-validation-stack.sh's
#   convention) rather than substituting a mocked routing check, which
#   would recreate the exact gap #2239 exists to close. Set
#   REQUIRE_LIVE_SMOKE=true (as .github/workflows/deployment-tests.yml
#   does for its dedicated job) to turn a missing prerequisite into a hard
#   FAIL instead - so the one job whose entire purpose is this proof can
#   never go green having silently skipped.
#
# Usage:
#   tests/test-split-topology-route-smoke.sh
#
# All progress/assertion output goes to stdout (log()) and failure
# diagnostics (docker compose logs) go to stdout too, so the caller should
# capture the run with tee, e.g.:
#   bash tests/test-split-topology-route-smoke.sh 2>&1 | tee split-topology-smoke.log
# .github/workflows/deployment-tests.yml does exactly this and uploads the
# resulting log as a build artifact so it can be attached/linked per the
# issue's acceptance criteria.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

log() {
  printf '[split-topology-route-smoke] %s\n' "$1"
}

# parse_curl_response <raw>: splits curl output captured with
# --write-out '\n%{http_code}\nCTYPE:%{content_type}' into globals
# PARSED_BODY/PARSED_STATUS/PARSED_CTYPE. The literal "CTYPE:" prefix is
# required, not cosmetic: curl emits an empty string for %{content_type}
# when a response has no Content-Type header (e.g. some empty-body 401/403
# responses), and command substitution strips trailing newlines, so a bare
# trailing empty field would silently disappear and shift every parsed
# field by one (status read as body, ctype read as status, etc.) instead
# of failing loudly. Prefixing the field with "CTYPE:" guarantees the last
# line is never empty, so the field boundary survives regardless of
# whether Content-Type was present.
parse_curl_response() {
  local raw="$1"
  local ctype_line="${raw##*$'\n'}"
  PARSED_CTYPE="${ctype_line#CTYPE:}"
  local rest="${raw%$'\n'"$ctype_line"}"
  PARSED_STATUS="${rest##*$'\n'}"
  PARSED_BODY="${rest%$'\n'"$PARSED_STATUS"}"
}

# --- Docker-unavailable behavior is explicit (see header). REQUIRE_LIVE_SMOKE=true
# (set by the dedicated CI job) turns a missing prerequisite into a hard FAIL
# instead of a silent SKIP/exit 0, so the job whose entire purpose is this
# proof cannot go green having run nothing.
skip_or_fail() {
  local msg="$1"
  if [[ "${REQUIRE_LIVE_SMOKE:-false}" == "true" ]]; then
    log "FAIL: $msg (REQUIRE_LIVE_SMOKE=true; refusing to silently skip)"
    exit 1
  fi
  log "SKIP: $msg"
  exit 0
}

if ! command -v docker >/dev/null 2>&1; then
  skip_or_fail "docker is not installed in this environment; route smoke cannot run."
fi

if ! docker info >/dev/null 2>&1; then
  skip_or_fail "docker daemon is not reachable in this environment; route smoke cannot run."
fi

if ! docker compose version >/dev/null 2>&1; then
  skip_or_fail "docker compose subcommand is not available in this environment; route smoke cannot run."
fi

for tool in curl jq openssl; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    skip_or_fail "required tool '$tool' is not available in this environment; route smoke cannot run."
  fi
done

# --- Reuse tests/test-compose-generator.sh as the setup step (issue #2239). ---
# Before paying the cost of building/starting a live stack, re-run the
# existing static assertions that the generated split-topology nginx text
# (deploy/nginx/nginx-proxy-split.conf) routes each namespace this smoke
# exercises to the right upstream. test-compose-generator.sh proves the
# *text* is right; this script then proves a *live request* is actually
# answered by the service that text names. Sourcing only defines functions
# (its own BASH_SOURCE guard skips run_all_tests), so this has no side
# effects beyond making the two functions callable.
log "Setup step: re-running tests/test-compose-generator.sh split-topology routing assertions..."
# shellcheck source=./test-compose-generator.sh
source "$SCRIPT_DIR/test-compose-generator.sh"
if ! test_model_thumbnail_replacement_routing; then
  log "FAIL: tests/test-compose-generator.sh's model-thumbnail routing assertion failed - the generated split nginx text is already wrong, so a live stack would only reconfirm a known-bad route table. Not starting Docker."
  exit 1
fi
if ! test_workers_exact_match_routing; then
  log "FAIL: tests/test-compose-generator.sh's workers exact-match routing assertion failed - the generated split nginx text is already wrong, so a live stack would only reconfirm a known-bad route table. Not starting Docker."
  exit 1
fi
if ! test_slice_print_bridge_routing; then
  log "FAIL: tests/test-compose-generator.sh's slice/print-bridge routing assertion failed - the generated split nginx text is already wrong, so a live stack would only reconfirm a known-bad route table. Not starting Docker."
  exit 1
fi
log "Setup step passed: generated split nginx text is correct ($TESTS_PASSED/$TESTS_RUN compose-generator assertions). Proceeding to live stack."

# shellcheck source=../scripts/docker-utils.sh
source "$REPO_ROOT/scripts/docker-utils.sh"
# Base image tags used by the generated compose files' `build.args` (see
# scripts/docker/compose-templates/*.yml): compose has no `:-default` there,
# so an unset var becomes an empty --build-arg that silently overrides the
# Dockerfile's own ARG default with a blank tag. container-versions.conf is
# the single source of truth deploy-docker.sh itself uses for these tags.
# shellcheck source=../scripts/docker/container-versions.conf
source "$REPO_ROOT/scripts/docker/container-versions.conf"

STACK_DIR="$(mktemp -d)"
# GITHUB_RUN_ID+$RANDOM entropy avoids Docker Compose project/network/volume
# name collisions between concurrent runs (parallel CI jobs, or a concurrent
# local run) - a bare bash PID ($$) can wrap and is guessable, so it alone is
# not enough. This does NOT avoid host PORT or container_name collisions:
# API_PORT/HTTP_PORT/HTTPS_PORT/POSTGRES_PORT/SLICER_HOST_PORT below, and the
# container_name values baked into the generated compose file, are fixed
# regardless of PROJECT_NAME, so two runs of this script must not be executed
# concurrently on the same Docker host (this matches how the CI job runs -
# one job per commit, never in parallel with itself).
PROJECT_NAME="printfarmer-route-smoke-${GITHUB_RUN_ID:-$$}-$RANDOM"
FAILURES=0

compose() {
  docker compose --project-name "$PROJECT_NAME" -f "$STACK_DIR/docker-compose.yml" "$@"
}

cleanup() {
  local exit_code="${1:-$?}"
  trap - EXIT INT TERM
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
  exit "$exit_code"
}
# EXIT uses the script's real exit status. INT/TERM force the standard
# 128+signum exit codes explicitly: relying on "$?" at signal-delivery time
# would reflect whatever the last foreground command happened to return
# (often 0), which could make a killed run falsely report success.
trap 'cleanup $?' EXIT
trap 'cleanup 130' INT
trap 'cleanup 143' TERM

# --- Isolated secrets/ports for this run (never reused outside this stack) ---
: "${POSTGRES_PASSWORD:=$(openssl rand -base64 24)}"
: "${POSTGRES_USER:=printfarmer}"
: "${Jwt__Key:=$(openssl rand -base64 48)}"
: "${WORKER_SHARED_API_KEY:=$(openssl rand -hex 32)}"
: "${ConnectionStrings__Default:=Host=database;Port=5432;Database=printfarmer;Username=${POSTGRES_USER};Pwd=${POSTGRES_PASSWORD}}"
: "${API_PORT:=15245}"
: "${SLICER_HOST_PORT:=15246}"
: "${HTTP_PORT:=18080}"
: "${HTTPS_PORT:=18443}"
: "${POSTGRES_PORT:=15432}"
export POSTGRES_PASSWORD POSTGRES_USER Jwt__Key WORKER_SHARED_API_KEY \
  ConnectionStrings__Default API_PORT SLICER_HOST_PORT HTTP_PORT HTTPS_PORT POSTGRES_PORT
export DB_PROVIDER=Postgres
export ENABLE_DISTRIBUTED_SLICING=true
export ENABLE_ORCA_WORKER=yes
export ENABLE_ORCA_WORKER_PREVIOUS=no
export ORCA_WORKER_COUNT=1
# ALLOW_STUB=true is an existing, documented non-production Dockerfile mode
# (scripts/docker/dockerfiles/Dockerfile.multistage's orcaslicer-binaries
# stage) that skips downloading/verifying the real OrcaSlicer AppImage.
# This smoke never exercises real slicing - it only needs the worker
# container present for topology completeness (route ownership does not
# depend on OrcaSlicer actually being able to slice) - so this bounds
# build cost without weakening what this test proves.
export ALLOW_STUB=true
export EXTERNAL_ORCA_WORKER_TEMP="$STACK_DIR/.volumes/printfarmer-orcaslicer-temp"
# The generated compose file's build context/dockerfile default to paths
# relative to the stack dir itself; point them back at this worktree so
# `docker compose up --build` builds from real repo sources (see
# scripts/ci/smoke-daily-validation-stack.sh for the same pattern).
export PRINTFARMER_BUILD_CONTEXT="$REPO_ROOT"
export PRINTFARMER_DOCKERFILE="scripts/docker/dockerfiles/Dockerfile.multistage"

log "Generating split (microservices) topology stack (project=$PROJECT_NAME) in $STACK_DIR"
"$REPO_ROOT/scripts/docker/compose-generator.sh" \
  --architecture microservices \
  --db-provider postgres \
  --enable-orca-worker yes \
  --exclude-monitoring \
  --exclude-telemetry \
  --output-dir "$STACK_DIR"

mkdir -p "$STACK_DIR/deploy"
cp -R "$REPO_ROOT/deploy/nginx" "$STACK_DIR/deploy/nginx"
"$REPO_ROOT/scripts/generate-certs.sh" "$STACK_DIR/deploy/nginx/certs"

# Pre-create the worker's /app/temp bind mount with appuser-writable
# permissions (issue #2174) - see scripts/ci/smoke-daily-validation-stack.sh
# for the same pattern.
if ! prepare_orcaslicer_worker_temp_directories; then
  log "FAIL: could not prepare OrcaSlicer worker temp directories (see output above)"
  exit 1
fi

log "Confirming the generated stack actually switched to the split nginx config"
if ! grep -q 'slicer_upstream' "$STACK_DIR/deploy/nginx/nginx-proxy-split.conf" 2>/dev/null \
  && ! grep -rq 'slicer_upstream' "$STACK_DIR/deploy/nginx" 2>/dev/null; then
  log "FAIL: generated stack does not reference a slicer upstream; split topology was not generated"
  exit 1
fi
if ! grep -q 'slicer-host' "$STACK_DIR/docker-compose.yml"; then
  log "FAIL: generated docker-compose.yml does not include the slicer-host service"
  exit 1
fi
if ! grep -q 'nginx-proxy-split.conf' "$STACK_DIR/docker-compose.yml"; then
  log "FAIL: generated docker-compose.yml does not mount nginx-proxy-split.conf; split nginx config was generated but not wired into the running stack"
  exit 1
fi
log "OK: generated stack includes slicer-host and a split nginx config"

log "Building images locally from the current worktree and starting the stack"
compose up -d --build --scale orcaslicer-worker=1

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

wait_for_health "http://localhost:${API_PORT}/healthz" "main API (direct)"
wait_for_health "http://localhost:${SLICER_HOST_PORT}/healthz" "slicer-host (direct)"
wait_for_health "http://localhost:${HTTP_PORT}/healthz" "nginx"

log "Creating an isolated route-smoke administrator and authenticating through the real API"
smoke_admin_username="route-smoke-admin"
smoke_admin_password="Sm0ke!$(openssl rand -hex 24)Aa1"
setup_status="$(curl --fail --silent --show-error "http://localhost:${API_PORT}/api/setup/status")"
if [[ "$(printf '%s' "$setup_status" | jq -r '.needsSetup')" == "true" ]]; then
  setup_payload="$(jq -n \
    --arg username "$smoke_admin_username" \
    --arg password "$smoke_admin_password" \
    '{
      username: $username,
      password: $password,
      email: "route-smoke-admin@printfarmer.local",
      firstName: "Route",
      lastName: "Smoke"
    }')"
  setup_response="$(curl --fail --silent --show-error \
    -H 'Content-Type: application/json' \
    --data-binary "$setup_payload" \
    "http://localhost:${API_PORT}/api/setup/initial-admin")"
  if [[ "$(printf '%s' "$setup_response" | jq -r '.success')" != "true" ]]; then
    log "FAIL: API rejected the isolated route-smoke administrator"
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
AUTH_TOKEN="$(printf '%s' "$login_response" | jq -r '.token // empty')"
if [[ -z "$AUTH_TOKEN" ]]; then
  log "FAIL: API login did not return an access token"
  exit 1
fi
log "OK: isolated route-smoke administrator authenticated (real JWT obtained)"

# --- Route-ownership assertions --------------------------------------------
#
# assert_slicer_route <path>: hits <path> directly on the main API (expects
# the SLICER_DISABLED marker - the negative assertion), directly on
# slicer-host's own published port (the positive-identification baseline),
# and via nginx (expects nginx's response to share the SAME HTTP status AND
# Content-Type as slicer-host's own direct answer, and to NOT be text/html -
# the positive assertion that slicer-host, not the main API and not some
# other upstream such as the SPA static-file backend or an nginx-generated
# error page (both of which serve text/html), answered). Status+Content-Type
# equality is used instead of full byte-for-byte body equality because
# ASP.NET Core's default ProblemDetails error bodies (and some success
# payloads) embed per-request-varying fields (e.g. traceId), so two separate
# requests to the SAME backend can legitimately return different bytes for
# an identical logical response - byte equality produced false failures
# here in practice. Every request carries the real bearer token obtained
# above; a 401/403 on the slicer-host side is not treated as a failure here
# (some endpoints require additional permissions the seeded admin may or
# may not hold) because status+content-type identity with slicer-host's own
# direct response - not the HTTP status code alone - is what proves
# ownership. This is exactly what makes the assertion immune to "a 401 looks
# like it didn't get routed" AND to "some other 2xx/4xx-returning upstream
# answered instead of slicer-host".
assert_slicer_route() {
  local path="$1"
  local label="${2:-$path}"

  local direct_raw direct_status direct_body
  direct_raw="$(curl --silent --show-error \
    -H "Authorization: Bearer ${AUTH_TOKEN}" \
    --write-out '\n%{http_code}' \
    "http://localhost:${API_PORT}${path}")"
  direct_status="${direct_raw##*$'\n'}"
  direct_body="${direct_raw%$'\n'"$direct_status"}"
  if [[ "$direct_body" != *"SLICER_DISABLED"* ]]; then
    log "FAIL: [$label] expected main API (direct, port $API_PORT) to report SLICER_DISABLED for $path, got (status $direct_status): $direct_body"
    FAILURES=$((FAILURES + 1))
    return
  fi
  log "OK: [$label] main API (direct) does NOT serve $path (SLICER_DISABLED, status $direct_status) - negative assertion holds"

  local nginx_raw
  nginx_raw="$(curl --silent --show-error \
    -H "Authorization: Bearer ${AUTH_TOKEN}" \
    --write-out '\n%{http_code}\nCTYPE:%{content_type}' \
    "http://localhost:${HTTP_PORT}${path}")"
  parse_curl_response "$nginx_raw"
  local nginx_body="$PARSED_BODY" nginx_status="$PARSED_STATUS" nginx_ctype="$PARSED_CTYPE"
  # A 3xx/5xx/000 (or a transport failure) does NOT prove slicer-host
  # answered - "absence of the SLICER_DISABLED marker" is trivially true for
  # an empty body, a gateway error, or a redirect nginx issued itself
  # without ever reaching an upstream. Require a genuine response FROM an
  # upstream (2xx or 4xx - a 401/403 still proves slicer-host was reached,
  # per the comment above assert_slicer_route).
  if [[ ! "$nginx_status" =~ ^(2|4)[0-9][0-9]$ ]]; then
    log "FAIL: [$label] expected nginx (port $HTTP_PORT) to route $path to slicer-host and get a real upstream response, got status '$nginx_status' (body: $nginx_body)"
    FAILURES=$((FAILURES + 1))
    return
  fi
  if [[ "$nginx_body" == *"SLICER_DISABLED"* ]]; then
    log "FAIL: [$label] expected nginx (port $HTTP_PORT) to route $path to slicer-host, but main API's SLICER_DISABLED marker leaked through: $nginx_body"
    FAILURES=$((FAILURES + 1))
    return
  fi

  # Positive identification: absence of the main API's marker only proves
  # "not the main API" - it does not prove slicer-host specifically answered
  # (a misroute to the SPA static-file backend, or an nginx-generated error
  # page, could also be 2xx/4xx with no marker, and both serve text/html).
  # Query slicer-host directly on its own published port and require
  # nginx's response to share its status AND Content-Type, and require
  # neither is text/html.
  local direct_slicer_raw
  direct_slicer_raw="$(curl --silent --show-error \
    -H "Authorization: Bearer ${AUTH_TOKEN}" \
    --write-out '\n%{http_code}\nCTYPE:%{content_type}' \
    -o /dev/null \
    "http://localhost:${SLICER_HOST_PORT}${path}")"
  parse_curl_response "$direct_slicer_raw"
  local direct_slicer_status="$PARSED_STATUS" direct_slicer_ctype="$PARSED_CTYPE"
  if [[ "$nginx_ctype" == text/html* ]]; then
    log "FAIL: [$label] nginx's response for $path via port $HTTP_PORT is text/html (Content-Type: $nginx_ctype) - looks like an SPA/static-file response or nginx's own error page, not slicer-host"
    FAILURES=$((FAILURES + 1))
    return
  fi
  if [[ "$nginx_status" != "$direct_slicer_status" || "$nginx_ctype" != "$direct_slicer_ctype" ]]; then
    log "FAIL: [$label] nginx's response for $path (status $nginx_status, Content-Type $nginx_ctype) does not match slicer-host's own direct response (status $direct_slicer_status, Content-Type $direct_slicer_ctype) - nginx may not actually be routing to slicer-host"
    FAILURES=$((FAILURES + 1))
    return
  fi
  log "OK: [$label] nginx routes $path to slicer-host (status $nginx_status, Content-Type $nginx_ctype matches slicer-host's direct answer) - positive assertion holds"
}

log "Asserting slicer-owned namespace: /api/workers"
# nginx-proxy-split.conf defines both an exact-match location for the bare
# collection-root path (`location = /api/workers`, no trailing slash) and a
# trailing-slash prefix location (`location /api/workers/`) for sub-paths -
# issue #2245 fixed a regression where the bare path fell through to
# nginx's own default redirect instead of reaching slicer-host. Assert both
# the bare path and a real sub-route (WorkersController exposes
# GET /api/workers/{id}) so a regression on either is caught.
assert_slicer_route "/api/workers" "workers (bare, no trailing slash)"
assert_slicer_route "/api/workers/$(uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid)" "workers"

log "Asserting slicer-owned namespace: /api/slicers"
assert_slicer_route "/api/slicers/engines" "slicers"

log "Asserting slicer-owned namespace: /api/slicer"
assert_slicer_route "/api/slicer/profiles/hierarchy" "slicer"

log "Asserting slicer-owned namespace: /api/slice"
assert_slicer_route "/api/slice/circuit-breakers" "slice"

log "Asserting slicer-owned namespace: /api/3d-models"
# The main API pre-stubs GET /api/3d-models (exact), /api/3d-models/folders,
# and POST /api/3d-models/query with 200 + empty-array responses even when
# the slicer module is disabled (src/api/Program.cs), so those exact paths
# are NOT reliable negative-assertion targets. A random GUID under the
# namespace falls through to the real SLICER_DISABLED catch-all instead.
assert_slicer_route "/api/3d-models/$(uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid)" "3d-models"

log "Asserting slicer-owned namespace: /api/artifacts"
assert_slicer_route "/api/artifacts/$(uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid)" "artifacts"

log "Asserting slicer-owned namespace: /api/admin/slicer"
assert_slicer_route "/api/admin/slicer/settings" "admin/slicer"

# --- Control: a main-API-owned route must still resolve normally via nginx,
# proving nginx is NOT blanket-routing everything to slicer-host. Positive
# identification mirrors assert_slicer_route: nginx's response must share
# status + Content-Type with the main API's own direct response (not full
# body bytes, which can legitimately vary per request - see the comment on
# assert_slicer_route above).
control_direct_raw="$(curl --silent --show-error \
  -H "Authorization: Bearer ${AUTH_TOKEN}" \
  --write-out '\n%{http_code}\nCTYPE:%{content_type}' \
  -o /dev/null \
  "http://localhost:${API_PORT}/api/printers")"
parse_curl_response "$control_direct_raw"
control_direct_status="$PARSED_STATUS"
control_direct_ctype="$PARSED_CTYPE"

log "Asserting main-API-owned control route /api/printers is NOT routed to slicer-host"
control_raw="$(curl --silent --show-error \
  -H "Authorization: Bearer ${AUTH_TOKEN}" \
  --write-out '\n%{http_code}\nCTYPE:%{content_type}' \
  "http://localhost:${HTTP_PORT}/api/printers")"
parse_curl_response "$control_raw"
control_body="$PARSED_BODY"
control_status="$PARSED_STATUS"
control_ctype="$PARSED_CTYPE"
if [[ ! "$control_status" =~ ^(2|4)[0-9][0-9]$ ]]; then
  log "FAIL: control route /api/printers via nginx did not get a real upstream response, got status '$control_status' (body: $control_body)"
  FAILURES=$((FAILURES + 1))
elif [[ "$control_body" == *"SLICER_DISABLED"* ]]; then
  log "FAIL: control route /api/printers unexpectedly returned SLICER_DISABLED via nginx"
  FAILURES=$((FAILURES + 1))
elif [[ "$control_status" != "$control_direct_status" || "$control_ctype" != "$control_direct_ctype" ]]; then
  log "FAIL: control route /api/printers via nginx (status $control_status, Content-Type $control_ctype) does not match the main API's own direct response (status $control_direct_status, Content-Type $control_direct_ctype) - nginx may not actually be routing /api/printers to the main API"
  FAILURES=$((FAILURES + 1))
else
  log "OK: control route /api/printers is served normally via nginx (status $control_status, Content-Type $control_ctype matches the main API's direct answer, not slicer-host)"
fi

# --- WebSocket UPGRADE proof -------------------------------------------------
#
# assert_hub_upgrade <hub-path>: negotiates a SignalR connection through
# nginx (Bearer header, matching a real browser client), then performs a
# RAW HTTP Upgrade handshake (not merely re-checking the negotiate
# endpoint's HTTP 200) and asserts a literal "HTTP/1.1 101" response line -
# proving the WebSocket transport actually negotiates end-to-end through
# nginx to slicer-host, not just that the negotiate endpoint answers.
assert_hub_upgrade() {
  local hub_path="$1"
  local label="${2:-$hub_path}"

  local negotiate_response connection_token
  negotiate_response="$(curl --fail --silent --show-error \
    -X POST \
    -H "Authorization: Bearer ${AUTH_TOKEN}" \
    "http://localhost:${HTTP_PORT}${hub_path}/negotiate?negotiateVersion=1")"
  connection_token="$(printf '%s' "$negotiate_response" | jq -r '.connectionToken // .connectionId // empty')"
  if [[ -z "$connection_token" ]]; then
    log "FAIL: [$label] negotiate did not return a connectionToken/connectionId: $negotiate_response"
    FAILURES=$((FAILURES + 1))
    return
  fi
  log "OK: [$label] negotiate succeeded (connectionToken obtained)"

  # SlicerHubAuth resolves the JWT from ?access_token= for /hubs/* paths
  # (native WebSocket clients cannot set an Authorization header on the
  # upgrade request itself).
  local ws_key ws_accept_expected response
  ws_key="$(openssl rand -base64 16)"
  ws_accept_expected="$(printf '%s258EAFA5-E914-47DA-95CA-C5AB0DC85B11' "$ws_key" | openssl dgst -sha1 -binary | openssl base64)"

  # connectionToken is base64 and access_token is a JWT - both routinely
  # contain '+', '/', '=' which are query-string metacharacters ('+' decodes
  # to a literal space server-side), so both must be percent-encoded or the
  # handshake corrupts intermittently.
  local connection_token_enc access_token_enc
  connection_token_enc="$(jq -rn --arg s "$connection_token" '$s|@uri')"
  access_token_enc="$(jq -rn --arg s "$AUTH_TOKEN" '$s|@uri')"

  response="$(printf 'GET %s?id=%s&access_token=%s HTTP/1.1\r\nHost: localhost:%s\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n' \
    "$hub_path" "$connection_token_enc" "$access_token_enc" "$HTTP_PORT" "$ws_key" \
    | timeout 10 bash -c "exec 3<>/dev/tcp/127.0.0.1/${HTTP_PORT}; cat >&3; timeout 5 cat <&3" 2>/dev/null || true)"

  if [[ "$response" != *"HTTP/1.1 101"* ]]; then
    log "FAIL: [$label] expected a genuine HTTP/1.1 101 Switching Protocols upgrade through nginx for $hub_path, got:"
    printf '%s\n' "$response"
    FAILURES=$((FAILURES + 1))
    return
  fi
  log "OK: [$label] real WebSocket UPGRADE (HTTP/1.1 101) negotiated through nginx for $hub_path"

  if [[ "$response" != *"Sec-WebSocket-Accept: ${ws_accept_expected}"* ]]; then
    log "FAIL: [$label] Sec-WebSocket-Accept did not match the value computed from our Sec-WebSocket-Key - not a genuine negotiated handshake:"
    printf '%s\n' "$response"
    FAILURES=$((FAILURES + 1))
    return
  fi
  log "OK: [$label] Sec-WebSocket-Accept matches the value computed from our Sec-WebSocket-Key (genuine negotiated handshake, not a canned response)"
}

log "Asserting genuine WebSocket UPGRADE for /hubs/slicer-registry"
assert_hub_upgrade "/hubs/slicer-registry" "hubs/slicer-registry"

log "Asserting genuine WebSocket UPGRADE for /hubs/slicers"
assert_hub_upgrade "/hubs/slicers" "hubs/slicers"

if [[ "$FAILURES" -gt 0 ]]; then
  log "FAILURE: $FAILURES route-ownership assertion(s) failed"
  compose logs --no-color
  exit 1
fi

log "SUCCESS: all split-topology route-ownership assertions passed (authenticated, real WebSocket upgrade, negative assertion included)"
