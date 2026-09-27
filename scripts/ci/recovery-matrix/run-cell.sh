#!/usr/bin/env bash
# Runs one isolated recovery-matrix cell. C2 is the first implemented cell:
# monolith + PostgreSQL, network denied during import/activation/recovery.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

CELL="c2"
WORK_DIR="$REPO_ROOT/.recovery-matrix-work"
EVIDENCE="$REPO_ROOT/.recovery-matrix-work/c2-evidence.json"
COSIGN="${PF_COSIGN:-$HOME/.cache/pf-cosign/cosign}"
KEEP_WORK=0

usage() {
  cat >&2 <<'EOF'
Usage: scripts/ci/recovery-matrix/run-cell.sh [OPTIONS]

Options:
  --cell c2                 Cell to run (currently c2 only).
  --work-dir DIR            Repo-local scratch directory. Default: .recovery-matrix-work
  --evidence FILE           Evidence JSON output path.
  --cosign FILE             Cosign executable. Default: PF_COSIGN or ~/.cache/pf-cosign/cosign
  --keep-work               Do not delete containers/networks/work files on failure.
  -h, --help                Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cell) CELL="${2:?}"; shift 2 ;;
    --work-dir) WORK_DIR="${2:?}"; shift 2 ;;
    --evidence) EVIDENCE="${2:?}"; shift 2 ;;
    --cosign) COSIGN="${2:?}"; shift 2 ;;
    --keep-work) KEEP_WORK=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
  esac
done

if [[ "$CELL" != "c2" ]]; then
  echo "Only recovery matrix cell c2 is implemented by this entrypoint." >&2
  exit 2
fi

case "$WORK_DIR" in
  /tmp/*|/var/tmp/*) echo "--work-dir must not be under a system temp directory" >&2; exit 2 ;;
esac
case "$EVIDENCE" in
  /tmp/*|/var/tmp/*) echo "--evidence must not be under a system temp directory" >&2; exit 2 ;;
esac

require_tool() {
  command -v "$1" >/dev/null 2>&1 || { echo "$1 is required" >&2; exit 2; }
}

require_tool node
require_tool docker
require_tool jq
require_tool bash
if [[ ! -x "$COSIGN" ]]; then
  if command -v cosign >/dev/null 2>&1; then
    COSIGN="$(command -v cosign)"
  else
    echo "cosign is required (pass --cosign or set PF_COSIGN)" >&2
    exit 2
  fi
fi

RUN_ID="c2-$(date -u +%Y%m%dt%H%M%Sz)-$$"
RUN_ROOT="$WORK_DIR/$RUN_ID"
NETWORK="$RUN_ID-network"
SINK="$RUN_ID-egress-sink"
SUBNET_OCTET=$(( ($$ % 200) + 30 ))
NETWORK_SUBNET="172.30.${SUBNET_OCTET}.0/24"
APP_IP="172.30.${SUBNET_OCTET}.20"
mkdir -p "$RUN_ROOT"

cleanup() {
  if [[ "$KEEP_WORK" == 1 ]]; then
    echo "Keeping work directory: $RUN_ROOT" >&2
    return
  fi
  docker rm -f "$SINK" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "Creating internal Docker network and egress sink"
docker pull python:3.12-alpine >/dev/null
mkdir -p "$RUN_ROOT/egress-sink"
touch "$RUN_ROOT/egress-sink/network-attempts.ndjson"
docker network create --internal --subnet "$NETWORK_SUBNET" "$NETWORK" >/dev/null
docker run -d --name "$SINK" --network "$NETWORK" --network-alias egress-sink \
  -v "$SCRIPT_DIR/egress-sink.py:/egress-sink.py:ro" \
  -v "$RUN_ROOT/egress-sink:/egress:rw" \
  python:3.12-alpine python /egress-sink.py /egress/network-attempts.ndjson >/dev/null
SINK_IP="$(docker inspect -f "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}" "$SINK")"
if [[ -z "$SINK_IP" ]]; then
  echo "Failed to determine egress sink IP" >&2
  exit 1
fi

node "$SCRIPT_DIR/run-cell.mjs" \
  --repo "$REPO_ROOT" \
  --run-root "$RUN_ROOT" \
  --evidence "$EVIDENCE" \
  --cosign "$COSIGN" \
  --network "$NETWORK" \
  --app-ip "$APP_IP" \
  --egress-sink "$SINK" \
  --egress-sink-ip "$SINK_IP" \
  --network-attempts "$RUN_ROOT/egress-sink/network-attempts.ndjson"
