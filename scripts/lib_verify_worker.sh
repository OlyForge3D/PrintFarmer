#!/usr/bin/env bash
# Shared library for worker verification scripts (Prusa / Orca / future workers)
#
# Exposed variables expected to be defined by the calling script BEFORE sourcing:
#   WORKER_NAME        Human readable or image-identical name (e.g. prusaslicer-worker)
#   ENV_PREFIX         Upper-case prefix for env vars (e.g. PRUSA, ORCA)
#   DEFAULT_IMAGE      Default image name
#   DEFAULT_CONTAINER  Default container name
#   DEFAULT_MODE       Default mode (allow-stub | require-real)
#   BINARY_PATH        Path to binary inside container
#   BINARY_PAYLOAD_PATH Optional real payload path when BINARY_PATH is a small launcher
#   HEALTH_KEY         Health check key present in readiness JSON (.checks.HEALTH_KEY.status)
#   LOG_PREFIX         Short lowercase identifier for log prefix (prusa | orca)
#   SIZE_THRESHOLD     Minimum size (bytes) that indicates a real binary (e.g. 2048)
#
# Produced / mutated variables:
#   MODE IMAGE CONTAINER_NAME BINSIZE (after vw_assess_binary)
#
# Exit Codes (unified):
#   0  success
#   2  docker missing
#   3  container start failed
#   4  liveness timeout
#   5  binary missing
#   6  stub not allowed in require-real
#   7  --help failed in require-real
#   8  readiness non-200
#   9  <health_key> check missing
#   10 <health_key> not healthy
#   11 readiness is relaxed while require-real requested
#   12 jq required for strict JSON parsing but not available (reserved / not currently triggered)
#   13 require-real requested but the image attests no verified binary identity
#   14 a stub binary is installed while the image attests a pinned binary identity

vw_log() { printf "[verify-%s] %s\n" "${LOG_PREFIX}" "$*"; }
vw_err() { vw_log "ERROR: $*" >&2; }

vw_show_help() {
  # Print the script's header comments (lines starting with #) for usage
  grep '^#' "$0" | sed 's/^# \{0,1\}//'
}

vw_parse_args() {
  # Precedence: CLI > env vars > defaults
  local env_image_var="${ENV_PREFIX}_IMAGE";
  local env_container_var="${ENV_PREFIX}_CONTAINER";
  local env_mode_var="${ENV_PREFIX}_MODE";

  IMAGE="${!env_image_var:-${DEFAULT_IMAGE}}"
  CONTAINER_NAME="${!env_container_var:-${DEFAULT_CONTAINER}}"
  MODE="${!env_mode_var:-${DEFAULT_MODE}}"

  if [[ $# -ge 1 && "$1" != -* ]]; then MODE="$1"; shift; fi
  if [[ $# -ge 1 && "$1" != -* ]]; then IMAGE="$1"; shift; fi
  if [[ $# -ge 1 && "$1" != -* ]]; then CONTAINER_NAME="$1"; shift; fi

  export MODE IMAGE CONTAINER_NAME
  vw_log "Configuration: mode=${MODE} image=${IMAGE} container=${CONTAINER_NAME} binary=${BINARY_PATH} key=${HEALTH_KEY}"
}

vw_require_docker() {
  if ! command -v docker >/dev/null 2>&1; then vw_err "Docker CLI not found"; exit 2; fi
}

vw_start_container() {
  vw_log "Starting ephemeral container (${IMAGE}) mode=${MODE}..."
  # If ORCA_REDIS was provided by caller or discovered, forward it as ConnectionStrings__Redis
  local docker_envs=()
  if [[ -n "${ORCA_REDIS-}" ]]; then
    docker_envs+=( -e "ConnectionStrings__Redis=${ORCA_REDIS}" )
  elif [[ "${DISCOVER_REDIS-}" == "true" ]]; then
    # try to discover a container named printfarmer-redis-distributed on the default bridge network
    if docker inspect printfarmer-redis-distributed >/dev/null 2>&1; then
      local ip
      ip=$(docker inspect --format '{{range $k,$v := .NetworkSettings.Networks}}{{$v.IPAddress}}{{end}}' printfarmer-redis-distributed 2>/dev/null || true)
      if [[ -n "${ip}" ]]; then
        docker_envs+=( -e "ConnectionStrings__Redis=${ip}:6379" )
      fi
    fi
  fi

  ID=$(docker run -d --rm --name "${CONTAINER_NAME}" ${docker_envs[*]} "${IMAGE}" 2>/dev/null || true)
  if [ -z "${ID}" ]; then vw_err "Failed to start container from image '${IMAGE}'."; exit 3; fi
  cleanup() { docker kill "${CONTAINER_NAME}" >/dev/null 2>&1 || true; }; trap cleanup EXIT
}

vw_wait_liveness() {
  vw_log "Waiting for liveness /healthz (max 40s)..."
  for i in {1..40}; do
    if docker exec "${CONTAINER_NAME}" wget -q -O - http://localhost:8080/healthz >/dev/null 2>&1; then
      vw_log "Health endpoint responded."; return 0; fi
    sleep 1
    if [ "$i" -eq 40 ]; then
      vw_err "Health endpoint did not become ready in time. Showing recent container logs:"
      docker logs --tail=120 "${CONTAINER_NAME}" 2>&1 | sed "s/^/[verify-${LOG_PREFIX}][log] /"
      exit 4
    fi
  done
}

vw_assess_binary() {
  local assessment_path="${BINARY_PAYLOAD_PATH:-${BINARY_PATH}}"
  vw_log "Assessing binary payload at ${assessment_path}..."
  if ! docker exec "${CONTAINER_NAME}" test -f "${assessment_path}"; then
    vw_err "Binary payload ${assessment_path} missing"; exit 5
  fi
  BINSIZE=$(docker exec "${CONTAINER_NAME}" stat -c %s "${assessment_path}" 2>/dev/null || echo 0)
  if [ "${BINSIZE}" -le "${SIZE_THRESHOLD}" ]; then
    vw_log "Binary size (${BINSIZE}) suggests stub or invalid binary (<${SIZE_THRESHOLD}+1)"
    if [ "${MODE}" = "require-real" ]; then vw_err "Stub binary not permitted in require-real mode"; exit 6; fi
  else
    vw_log "Binary size (${BINSIZE}) looks plausible"
  fi
}

# Asserts that the binary identity the image attests matches the binary it actually installed.
# Requires ATTESTATION_PATH to be set by the calling script; skipped otherwise.
# Must run after vw_assess_binary, which sets BINSIZE.
vw_check_binary_attestation() {
  if [ -z "${ATTESTATION_PATH-}" ]; then
    return 0
  fi

  local attested
  attested=$(docker exec "${CONTAINER_NAME}" sh -c "cat '${ATTESTATION_PATH}' 2>/dev/null || true" | tr -d '[:space:]')
  local is_digest="false"
  if printf '%s' "${attested}" | grep -Eq '^[0-9a-fA-F]{64}$'; then is_digest="true"; fi
  vw_log "Binary attestation at ${ATTESTATION_PATH}: verified=${is_digest}"

  if [ "${MODE}" = "require-real" ] && [ "${is_digest}" != "true" ]; then
    vw_err "require-real demands an attested binary identity, but the image attests none"; exit 13
  fi

  # A stub must never carry a pinned identity: registration would advertise a binary that is not there.
  if [ "${BINSIZE:-0}" -le "${SIZE_THRESHOLD}" ] && [ "${is_digest}" = "true" ]; then
    vw_err "A stub binary is installed but the image attests a pinned identity"; exit 14
  fi
}

vw_invoke_help() {
  local invocation_path="${BINARY_PAYLOAD_PATH:-${BINARY_PATH}}"
  # Always attempt help; in stub mode it may fail (only enforced in require-real)
  if /usr/bin/env bash -c "docker exec '${CONTAINER_NAME}' '${invocation_path}' --help >/dev/null 2>&1"; then
    vw_log "Help invocation succeeded"
  else
    vw_log "Help invocation failed"
    if [ "${MODE}" = "require-real" ]; then vw_err "Help invocation failure not allowed"; exit 7; fi
  fi
}

vw_check_readiness() {
  vw_log "Checking readiness (/health/ready) including ${HEALTH_KEY}..."
  local readiness_body="/tmp/printfarmer-readiness.json"
  STATUS=$(docker exec "${CONTAINER_NAME}" curl -sS \
    -o "${readiness_body}" \
    -w '%{http_code}' \
    http://localhost:8080/health/ready) || true
  if [ "${STATUS}" != "200" ]; then
    vw_err "Readiness endpoint returned ${STATUS:-?}. Dumping logs + body (if any):"
    docker logs --tail=80 "${CONTAINER_NAME}" 2>&1 | sed "s/^/[verify-${LOG_PREFIX}][log] /"
    docker exec "${CONTAINER_NAME}" cat "${readiness_body}" 2>/dev/null | sed "s/^/[verify-${LOG_PREFIX}][body] /" || true
    exit 8
  fi
  BODY=$(docker exec "${CONTAINER_NAME}" cat "${readiness_body}" || true)

  # Detect relaxed readiness
  local RELAXED
  if command -v jq >/dev/null 2>&1; then
    RELAXED=$(echo "$BODY" | jq -r '.relaxed // false' 2>/dev/null || echo false)
  else
    RELAXED=$(echo "$BODY" | grep -qi '"relaxed"\s*:\s*true' && echo true || echo false)
  fi
  if [ "$RELAXED" = true ]; then
    vw_err "Readiness is relaxed (relaxed=true) while require-real mode demands full binary health"; exit 11
  fi

  # Parse health key
  if command -v jq >/dev/null 2>&1; then
    if ! echo "$BODY" | jq -e --arg hk "${HEALTH_KEY}" '.checks[$hk]' >/dev/null 2>&1; then
      vw_err "${HEALTH_KEY} check missing in readiness body"; exit 9; fi
    if ! echo "$BODY" | jq -e --arg hk "${HEALTH_KEY}" '.checks[$hk].status == "Healthy"' >/dev/null 2>&1; then
      vw_err "${HEALTH_KEY} not healthy"; exit 10; fi
  else
    vw_log "jq not found; falling back to legacy grep parsing (install jq for stronger validation)"
    echo "$BODY" | grep -q "${HEALTH_KEY}" || { vw_err "${HEALTH_KEY} check missing in readiness body"; exit 9; }
    echo "$BODY" | grep -qi "\"${HEALTH_KEY}\"" && echo "$BODY" | grep -qi 'Healthy' || { vw_err "${HEALTH_KEY} not healthy"; exit 10; }
  fi
  vw_log "Readiness OK (${HEALTH_KEY} healthy)"
}
