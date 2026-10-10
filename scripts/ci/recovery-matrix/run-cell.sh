#!/usr/bin/env bash
# Runs one isolated recovery-matrix cell with network denied during import,
# activation and recovery.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CELL="c2"
WORK_DIR="$REPO_ROOT/.recovery-matrix-work"
EVIDENCE="$REPO_ROOT/.recovery-matrix-work/c2-evidence.json"
EVIDENCE_PROVIDED=0
COSIGN="${PF_COSIGN:-$HOME/.cache/pf-cosign/cosign}"
KEEP_WORK=0
RELEASE_LOCK=0
FAULT_ARGS=()

usage() {
  cat >&2 <<'EOF'
Usage: scripts/ci/recovery-matrix/run-cell.sh [OPTIONS]

Options:
  --cell <id|all|faults|imports>  Cell to run. Use all for every topology cell, faults for every fault cell,
                            imports for every live import cell.
  --work-dir DIR            Repo-local scratch directory. Default: .recovery-matrix-work
  --evidence FILE           Evidence JSON output path.
  --cosign FILE             Cosign executable. Default: PF_COSIGN or ~/.cache/pf-cosign/cosign
  --fault POINT=COMMAND      Inject a fault hook at before-activate, during-activate, or before-recover.
  --keep-work               Do not delete containers/networks/work files on failure.
  --release-lock            Remove a specifically named stale daemon lock and exit.
  -h, --help                Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cell) CELL="${2:?}"; shift 2 ;;
    --work-dir) WORK_DIR="${2:?}"; shift 2 ;;
    --evidence) EVIDENCE="${2:?}"; EVIDENCE_PROVIDED=1; shift 2 ;;
    --cosign) COSIGN="${2:?}"; shift 2 ;;
    --fault) FAULT_ARGS+=("--fault" "${2:?}"); shift 2 ;;
    --keep-work) KEEP_WORK=1; shift ;;
    --release-lock) RELEASE_LOCK=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
  esac
done

case "$WORK_DIR" in
  /tmp/*|/var/tmp/*) echo "--work-dir must not be under a system temp directory" >&2; exit 2 ;;
esac
case "$EVIDENCE" in
  /tmp/*|/var/tmp/*) echo "--evidence must not be under a system temp directory" >&2; exit 2 ;;
esac
mkdir -p "$WORK_DIR" "$(dirname "$EVIDENCE")"
WORK_DIR="$(cd "$WORK_DIR" && pwd)"
EVIDENCE_DIR="$(cd "$(dirname "$EVIDENCE")" && pwd)"
EVIDENCE="$EVIDENCE_DIR/$(basename "$EVIDENCE")"

require_tool() {
  command -v "$1" >/dev/null 2>&1 || { echo "$1 is required" >&2; exit 2; }
}

LOCK_NAME="${PF_RECOVERY_MATRIX_LOCK_NAME:-printfarmer-recovery-matrix-daemon-lock}"
LOCK_LABEL_KEY="printfarmer.recovery-matrix.lock"

resources_for_owner() {
  local owner=$1
  {
    docker ps -aq --filter "label=printfarmer.recovery-matrix.run=$owner" --format 'container {{.ID}} {{.Names}}'
    docker volume ls --filter "label=printfarmer.recovery-matrix.run=$owner" --format 'volume {{.Name}}'
    docker network ls --filter "label=printfarmer.recovery-matrix.run=$owner" --format 'network {{.ID}} {{.Name}}'
  } | sed '/^$/d'
}

if [[ "$RELEASE_LOCK" == 1 ]]; then
  require_tool docker
  lock_owner="$(docker inspect --format "{{index .Config.Labels \"$LOCK_LABEL_KEY\"}}" "$LOCK_NAME" 2>/dev/null || true)"
  if [[ -z "$lock_owner" ]]; then
    echo "No recovery matrix daemon lock named $LOCK_NAME exists." >&2
    exit 0
  fi
  active_resources="$(resources_for_owner "$lock_owner" 2>/dev/null || true)"
  if [[ -n "$active_resources" ]]; then
    echo "Refusing to remove $LOCK_NAME: recovery-matrix resources for $lock_owner are still present." >&2
    exit 1
  fi
  echo "Removing recovery matrix daemon lock $LOCK_NAME owned by $lock_owner; confirm no recovery-matrix resources remain first." >&2
  docker rm -f "$LOCK_NAME" >/dev/null
  exit 0
fi

require_tool bash
require_tool node

CELL_IDS="$(
  node --input-type=module -e "import { cellIds } from '$SCRIPT_DIR/cells.mjs'; console.log(cellIds.join(' '));"
)"
FAULT_CELL_IDS="$(
  node --input-type=module -e "import { faultCellIds } from '$SCRIPT_DIR/fault-cells.mjs'; console.log(faultCellIds.join(' '));"
)"
IMPORT_CELL_IDS="$(
  node --input-type=module -e "import { importCellIds } from '$SCRIPT_DIR/import-cells.mjs'; console.log(importCellIds.join(' '));"
)"

evidence_for_cell() {
  local evidence_path=$1
  local matrix_cell=$2
  local dir base stem ext
  dir="$(dirname "$evidence_path")"
  base="$(basename "$evidence_path")"
  if [[ "$base" == *.* ]]; then
    stem="${base%.*}"
    ext=".${base##*.}"
  else
    stem="$base"
    ext=""
  fi
  printf '%s/%s-%s%s\n' "$dir" "$stem" "$matrix_cell" "$ext"
}

if [[ "$CELL" == "all" || "$CELL" == "faults" || "$CELL" == "imports" ]]; then
  if [[ "$KEEP_WORK" == 1 ]]; then
    echo "--keep-work cannot be combined with a recovery matrix cell group; run one cell at a time to retain debug resources and its daemon lock." >&2
    exit 2
  fi
  group_ids="$CELL_IDS"
  if [[ "$CELL" == "faults" ]]; then
    group_ids="$FAULT_CELL_IDS"
  elif [[ "$CELL" == "imports" ]]; then
    group_ids="$IMPORT_CELL_IDS"
  fi
  status=0
  for matrix_cell in $group_ids; do
    cell_evidence="$EVIDENCE"
    if [[ "$EVIDENCE_PROVIDED" == 1 ]]; then
      cell_evidence="$(evidence_for_cell "$EVIDENCE" "$matrix_cell")"
    else
      cell_evidence="$WORK_DIR/$matrix_cell-evidence.json"
    fi
    args=(--cell "$matrix_cell" --work-dir "$WORK_DIR" --evidence "$cell_evidence" --cosign "$COSIGN")
    if [[ "$KEEP_WORK" == 1 ]]; then
      args+=(--keep-work)
    fi
    "$0" "${args[@]}" "${FAULT_ARGS[@]}" || status=$?
  done
  exit "$status"
fi

case " $CELL_IDS $FAULT_CELL_IDS $IMPORT_CELL_IDS " in
  *" $CELL "*) ;;
  *) echo "Unknown recovery matrix cell: $CELL (expected one of: $CELL_IDS $FAULT_CELL_IDS $IMPORT_CELL_IDS, all, faults, imports)" >&2; exit 2 ;;
esac

require_tool docker
require_tool jq
if [[ ! -x "$COSIGN" ]]; then
  if command -v cosign >/dev/null 2>&1; then
    COSIGN="$(command -v cosign)"
  else
    echo "cosign is required (pass --cosign or set PF_COSIGN)" >&2
    exit 2
  fi
fi

RUN_ID="$CELL-$(date -u +%Y%m%dt%H%M%Sz)-$$"
RUN_ROOT="$WORK_DIR/$RUN_ID"
NETWORK="$RUN_ID-network"
SINK="$RUN_ID-egress-sink"
HOST="$RUN_ID-host"
HOST_IMAGE="$RUN_ID-host-image"
RUN_LABEL_KEY="printfarmer.recovery-matrix.run"
RUN_LABEL="$RUN_LABEL_KEY=$RUN_ID"
LOCK_LABEL="$LOCK_LABEL_KEY=$RUN_ID"
LOCK_ACQUIRED=0
SUBNET_OCTET=$(( ($$ % 200) + 30 ))
NETWORK_SUBNET="172.30.${SUBNET_OCTET}.0/24"
APP_IP="172.30.${SUBNET_OCTET}.20"
mkdir -p "$RUN_ROOT"

release_daemon_lock() {
  if [[ "$LOCK_ACQUIRED" != 1 ]]; then
    return
  fi
  local owner
  owner="$(docker inspect --format "{{index .Config.Labels \"$LOCK_LABEL_KEY\"}}" "$LOCK_NAME" 2>/dev/null || true)"
  if [[ "$owner" == "$RUN_ID" ]]; then
    docker rm -f "$LOCK_NAME" >/dev/null 2>&1 || true
  else
    echo "Recovery matrix daemon lock ownership changed; leaving $LOCK_NAME untouched" >&2
  fi
  LOCK_ACQUIRED=0
}

acquire_daemon_lock() {
  local create_status=0 owner
  if docker create --name "$LOCK_NAME" \
      --label "$LOCK_LABEL" \
      python:3.12-alpine sleep infinity >/dev/null 2>&1; then
    LOCK_ACQUIRED=1
    return
  else
    create_status=$?
  fi

  owner="$(docker inspect --format "{{index .Config.Labels \"$LOCK_LABEL_KEY\"}}" "$LOCK_NAME" 2>/dev/null || true)"
  if [[ -n "$owner" ]]; then
    echo "Recovery matrix requires an exclusive Docker daemon; another run owns $LOCK_NAME ($owner). Refusing to start so canonical fixture image tags remain stable." >&2
    exit 75
  fi
  echo "Failed to acquire the recovery-matrix Docker daemon lock $LOCK_NAME (docker create exited $create_status)." >&2
  exit 1
}

cleanup() {
  if [[ "$KEEP_WORK" == 1 ]]; then
    echo "Keeping work directory and daemon lock: $RUN_ROOT" >&2
    return
  fi
  if [[ -f "$RUN_ROOT/deployment/docker-compose.recovery.yml" ]]; then
    docker compose -f "$RUN_ROOT/deployment/docker-compose.recovery.yml" -p "$RUN_ID" down -v --remove-orphans >/dev/null 2>&1 || true
  fi
  # The CLI runs as root inside the host container; hand its backups/staging back to a non-root
  # runner so the retained scratch directory stays removable. Fall back to a throwaway container
  # from the local host image when the host container is already gone.
  if [[ "$(id -u)" != 0 ]]; then
    docker exec "$HOST" chown -h -R "$(id -u):$(id -g)" "$RUN_ROOT" >/dev/null 2>&1 \
      || docker run --rm --network none --user 0 --entrypoint chown \
        -v "$RUN_ROOT:$RUN_ROOT" "$HOST_IMAGE" -h -R "$(id -u):$(id -g)" "$RUN_ROOT" >/dev/null 2>&1 \
      || echo "Warning: could not restore ownership of $RUN_ROOT; remove it as root" >&2
  fi
  docker rm -f "$HOST" >/dev/null 2>&1 || true
  docker rm -f "$SINK" >/dev/null 2>&1 || true
  docker rm -f "$RUN_ID-printer-emulator" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
  docker image rm "$HOST_IMAGE" >/dev/null 2>&1 || true
  while IFS= read -r image; do
    [[ -z "$image" ]] || docker image rm "$image" >/dev/null 2>&1 || true
  done < <(docker image ls --format '{{.Repository}}:{{.Tag}}' | awk -v prefix="printfarmer-${RUN_ID}-" 'index($0, prefix) == 1')
  local leaks
  leaks="$(resources_for_owner "$RUN_ID")"
  while IFS= read -r image; do
    [[ -z "$image" ]] || leaks+=$'\n'"image $image"
  done < <(docker image ls --format '{{.Repository}}:{{.Tag}}' | awk -v prefix="printfarmer-${RUN_ID}-" 'index($0, prefix) == 1')
  if [[ -n "$leaks" ]]; then
    echo "Recovery matrix cleanup leaked resources for $RUN_LABEL:" >&2
    echo "$leaks" >&2
    release_daemon_lock
    return 1
  fi
  release_daemon_lock
}
trap cleanup EXIT

docker pull python:3.12-alpine >/dev/null
acquire_daemon_lock
echo "Creating internal Docker network and egress sink"
docker pull mcr.microsoft.com/dotnet/sdk:10.0-noble >/dev/null
cat > "$RUN_ROOT/host-container.Dockerfile" <<'EOF'
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble
RUN apt-get update \
  && apt-get install -y --no-install-recommends bash ca-certificates curl dnsutils netcat-openbsd nodejs tar gzip findutils coreutils \
  && rm -rf /var/lib/apt/lists/*
EOF
docker build --pull=false -t "$HOST_IMAGE" -f "$RUN_ROOT/host-container.Dockerfile" "$RUN_ROOT" >/dev/null
mkdir -p "$RUN_ROOT/egress-sink"
touch "$RUN_ROOT/egress-sink/network-attempts.ndjson"
docker network create --internal --subnet "$NETWORK_SUBNET" --label "$RUN_LABEL" "$NETWORK" >/dev/null
docker run -d --name "$SINK" --label "$RUN_LABEL" --network "$NETWORK" --network-alias egress-sink \
  -v "$SCRIPT_DIR/egress-sink.py:/egress-sink.py:ro" \
  -v "$RUN_ROOT/egress-sink:/egress:rw" \
  python:3.12-alpine python /egress-sink.py /egress/network-attempts.ndjson /egress/ready >/dev/null
for _ in $(seq 1 50); do
  if [[ -f "$RUN_ROOT/egress-sink/ready" ]]; then
    break
  fi
  if [[ "$(docker inspect -f '{{.State.Running}}' "$SINK" 2>/dev/null)" != "true" ]]; then
    echo "Egress sink exited before becoming ready" >&2
    docker logs "$SINK" >&2 || true
    exit 1
  fi
  sleep 0.2
done
if [[ ! -f "$RUN_ROOT/egress-sink/ready" ]]; then
  echo "Egress sink did not become ready" >&2
  exit 1
fi
SINK_IP="$(docker inspect -f "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}" "$SINK")"
if [[ -z "$SINK_IP" ]]; then
  echo "Failed to determine egress sink IP" >&2
  exit 1
fi
# The packaged CLI runs `docker compose` inside the host container, so it needs the runner's compose plugin.
COMPOSE_PLUGIN="$(docker info --format '{{range .ClientInfo.Plugins}}{{if eq .Name "compose"}}{{.Path}}{{end}}{{end}}' 2>/dev/null || true)"
if [[ -z "$COMPOSE_PLUGIN" || ! -x "$COMPOSE_PLUGIN" ]]; then
  echo "Docker Compose CLI plugin not found on the runner" >&2
  exit 1
fi
docker run -d --name "$HOST" --label "$RUN_LABEL" --network "$NETWORK" --dns "$SINK_IP" \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v /usr/bin/docker:/usr/bin/docker:ro \
  -v "$COMPOSE_PLUGIN:/usr/libexec/docker/cli-plugins/docker-compose:ro" \
  -v "$REPO_ROOT:$REPO_ROOT" \
  -v "$WORK_DIR:$WORK_DIR" \
  -w "$REPO_ROOT" \
  "$HOST_IMAGE" sleep infinity >/dev/null

node "$SCRIPT_DIR/run-cell.mjs" \
  --cell "$CELL" \
  --repo "$REPO_ROOT" \
  --run-root "$RUN_ROOT" \
  --evidence "$EVIDENCE" \
  --cosign "$COSIGN" \
  --network "$NETWORK" \
  --app-ip "$APP_IP" \
  --egress-sink "$SINK" \
  --host-container "$HOST" \
  --egress-sink-ip "$SINK_IP" \
  --network-attempts "$RUN_ROOT/egress-sink/network-attempts.ndjson" \
  "${FAULT_ARGS[@]}"
