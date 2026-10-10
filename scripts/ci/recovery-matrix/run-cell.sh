#!/usr/bin/env bash
# Run a recovery-matrix command while holding an exclusive Docker-daemon lock.
#
# The lock is a named internal Docker network, so acquiring it does not pull
# or depend on an image that the matrix is supposed to test.
set -euo pipefail

LOCK_NAME="${PF_RECOVERY_MATRIX_LOCK_NAME:-printfarmer-recovery-matrix-daemon-lock}"
LOCK_LABEL_KEY="printfarmer.recovery-matrix.lock"
RUN_ID="${PF_RECOVERY_MATRIX_RUN_ID:-recovery-matrix-$(date -u +%Y%m%dt%H%M%SZ)-$$}"
LOCK_ACQUIRED=0
RELEASE_LOCK=0

usage() {
  cat >&2 <<'EOF'
Usage:
  scripts/ci/recovery-matrix/run-cell.sh [--release-lock]
  scripts/ci/recovery-matrix/run-cell.sh [--lock-name NAME] -- COMMAND [ARG...]

The command runs only after this process acquires the exclusive Docker-daemon
lock. Contention exits with status 75. --release-lock removes a stale lock
only when no resources owned by its recorded run remain.
EOF
}

require_docker() {
  command -v docker >/dev/null 2>&1 || {
    echo "docker is required" >&2
    exit 2
  }
}

resources_for_owner() {
  local owner=$1
  {
    docker ps -aq --filter "label=printfarmer.recovery-matrix.run=$owner" \
      --format 'container {{.ID}} {{.Names}}'
    docker volume ls --filter "label=printfarmer.recovery-matrix.run=$owner" \
      --format 'volume {{.Name}}'
    docker network ls --filter "label=printfarmer.recovery-matrix.run=$owner" \
      --format 'network {{.ID}} {{.Name}}' | awk -v lock="$LOCK_NAME" '$3 != lock'
  } | sed '/^$/d'
}

lock_owner() {
  docker network inspect --format "{{index .Labels \"$LOCK_LABEL_KEY\"}}" \
    "$LOCK_NAME" 2>/dev/null || true
}

release_stale_lock() {
  local owner active_resources
  owner="$(lock_owner)"
  if [[ -z "$owner" ]]; then
    echo "No recovery matrix daemon lock named $LOCK_NAME exists." >&2
    return 0
  fi
  active_resources="$(resources_for_owner "$owner" 2>/dev/null || true)"
  if [[ -n "$active_resources" ]]; then
    echo "Refusing to remove $LOCK_NAME: recovery-matrix resources for $owner are still present." >&2
    return 1
  fi
  echo "Removing recovery matrix daemon lock $LOCK_NAME owned by $owner." >&2
  docker network rm "$LOCK_NAME" >/dev/null
}

acquire_lock() {
  local create_status=0 owner
  if docker network create --internal \
      --label "$LOCK_LABEL_KEY=$RUN_ID" \
      --label "printfarmer.recovery-matrix.run=$RUN_ID" \
      "$LOCK_NAME" >/dev/null 2>&1; then
    LOCK_ACQUIRED=1
    return
  else
    create_status=$?
  fi

  owner="$(lock_owner)"
  if [[ -n "$owner" ]]; then
    echo "Recovery matrix requires an exclusive Docker daemon; another run owns $LOCK_NAME ($owner). Refusing to start so canonical fixture image tags remain stable." >&2
    exit 75
  fi
  echo "Failed to acquire the recovery-matrix Docker daemon lock $LOCK_NAME (docker network create exited $create_status)." >&2
  exit 1
}

cleanup() {
  if [[ "$LOCK_ACQUIRED" != 1 ]]; then
    return
  fi
  if [[ "$(lock_owner)" == "$RUN_ID" ]]; then
    docker network rm "$LOCK_NAME" >/dev/null 2>&1 || true
  else
    echo "Recovery matrix daemon lock ownership changed; leaving $LOCK_NAME untouched" >&2
  fi
  LOCK_ACQUIRED=0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --lock-name)
      LOCK_NAME="${2:?--lock-name requires a value}"
      shift 2
      ;;
    --release-lock)
      RELEASE_LOCK=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --)
      shift
      break
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 2
      ;;
  esac
done

require_docker
if [[ "$RELEASE_LOCK" == 1 ]]; then
  if [[ $# -gt 0 ]]; then
    echo "--release-lock cannot be combined with a command" >&2
    exit 2
  fi
  release_stale_lock
  exit $?
fi

if [[ $# -eq 0 ]]; then
  echo "A command is required after --" >&2
  usage
  exit 2
fi

trap cleanup EXIT
acquire_lock
"$@"
