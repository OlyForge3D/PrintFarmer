#!/usr/bin/env bash
# Verifies one real published insider offline recovery bundle read-only and network-denied (#3195).
# Connected phase: select the newest published insider release, download its signed bundle archive,
# check the release API digest, and fetch the public-good Sigstore trusted root. Denied phase: verify
# the archive signature and the offline bundle inside an internal-network container whose only
# resolver is the egress sink. Nothing is built, re-signed, imported, activated or reset, and there is
# no fallback download: any failure fails the check.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
REPOSITORY="OlyForge3D/PrintFarmer"
VERIFY_IMAGE="node:24-bookworm-slim"
SINK_IMAGE="python:3.12-alpine"

WORK_DIR="$REPO_ROOT/.recovery-matrix-work"
EVIDENCE="$REPO_ROOT/.recovery-matrix-work/published-bundle-evidence.json"
COSIGN="${PF_COSIGN:-}"
TAG=""
KEEP_WORK=0

usage() {
  cat >&2 <<'EOF'
Usage: scripts/ci/recovery-matrix/verify-published-bundle.sh [OPTIONS]

Options:
  --tag vX.Y.Z-insider.N    Verify this published insider release instead of the newest one.
  --work-dir DIR            Repo-local scratch directory. Default: .recovery-matrix-work
  --evidence FILE           Evidence JSON output path.
  --cosign FILE             Cosign executable. Default: PF_COSIGN or cosign on PATH
  --keep-work               Do not delete containers/networks/work files.
  -h, --help                Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag) TAG="${2:?}"; shift 2 ;;
    --work-dir) WORK_DIR="${2:?}"; shift 2 ;;
    --evidence) EVIDENCE="${2:?}"; shift 2 ;;
    --cosign) COSIGN="${2:?}"; shift 2 ;;
    --keep-work) KEEP_WORK=1; shift ;;
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

require_tool() {
  command -v "$1" >/dev/null 2>&1 || { echo "$1 is required" >&2; exit 2; }
}
for tool in bash node docker jq gh git sha256sum; do require_tool "$tool"; done
if [[ -z "$COSIGN" ]]; then COSIGN="$(command -v cosign || true)"; fi
if [[ -z "$COSIGN" || ! -x "$COSIGN" ]]; then
  echo "cosign is required (pass --cosign or set PF_COSIGN)" >&2
  exit 2
fi
COSIGN="$(cd "$(dirname "$COSIGN")" && pwd)/$(basename "$COSIGN")"

mkdir -p "$WORK_DIR" "$(dirname "$EVIDENCE")"
WORK_DIR="$(cd "$WORK_DIR" && pwd)"
EVIDENCE="$(cd "$(dirname "$EVIDENCE")" && pwd)/$(basename "$EVIDENCE")"

RUN_ID="published-bundle-$(date -u +%Y%m%dt%H%M%Sz)-$$"
RUN_ROOT="$WORK_DIR/$RUN_ID"
NETWORK="$RUN_ID-network"
SINK="$RUN_ID-egress-sink"
RUN_LABEL="printfarmer.recovery-matrix.run=$RUN_ID"
SUBNET_OCTET=$(( ($$ % 200) + 30 ))
NETWORK_SUBNET="172.31.${SUBNET_OCTET}.0/24"
STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"
HARNESS_COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD)"
PUBLISHED="$RUN_ROOT/published"
TRUST="$RUN_ROOT/trust"
OUT="$RUN_ROOT/out"
mkdir -p "$PUBLISHED" "$TRUST" "$OUT" "$RUN_ROOT/egress-sink"

cleanup() {
  docker rm -f "$SINK" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
  if [[ "$KEEP_WORK" == 1 ]]; then
    echo "Keeping work directory: $RUN_ROOT" >&2
  else
    chmod -R u+w "$RUN_ROOT" 2>/dev/null || true
    rm -rf "$RUN_ROOT"
  fi
}
trap cleanup EXIT

host_state() {
  {
    echo "images:"; docker image ls -q --no-trunc | LC_ALL=C sort -u
    echo "volumes:"; docker volume ls -q | LC_ALL=C sort -u
    echo "trusted-root: $(sha256sum "$TRUST/trusted_root.json" | awk '{print $1}')"
  } > "$1"
}

echo "Selecting the published insider offline recovery bundle"
gh api --paginate "repos/$REPOSITORY/releases?per_page=100" --jq '.[]' | jq -s '.' > "$RUN_ROOT/releases.json"
select_args=(select --releases "$RUN_ROOT/releases.json")
if [[ -n "$TAG" ]]; then select_args+=(--tag "$TAG"); fi
node "$SCRIPT_DIR/published-bundle.mjs" "${select_args[@]}" > "$RUN_ROOT/selected.json"
SELECTED_TAG="$(jq -r '.tag' "$RUN_ROOT/selected.json")"
VERSION="$(jq -r '.version' "$RUN_ROOT/selected.json")"
BUNDLE_NAME="$(jq -r '.bundle.name' "$RUN_ROOT/selected.json")"
SIGNATURE_NAME="$(jq -r '.signature.name' "$RUN_ROOT/selected.json")"
echo "Verifying $BUNDLE_NAME from $SELECTED_TAG"

gh release download "$SELECTED_TAG" --repo "$REPOSITORY" --dir "$PUBLISHED" \
  --pattern "$BUNDLE_NAME" --pattern "$SIGNATURE_NAME"
for role in bundle signature; do
  name="$(jq -r ".$role.name" "$RUN_ROOT/selected.json")"
  expected="$(jq -r ".$role.sha256" "$RUN_ROOT/selected.json")"
  actual="$(sha256sum "$PUBLISHED/$name" | awk '{print $1}')"
  if [[ "$actual" != "$expected" ]]; then
    echo "Downloaded $name does not match its published digest" >&2
    exit 1
  fi
done
chmod a-w "$PUBLISHED" "$PUBLISHED"/*
BUNDLE_SHA256="$(jq -r '.bundle.sha256' "$RUN_ROOT/selected.json")"

# The public-good Sigstore trusted root, fetched through TUF while still connected; the denied phase
# gets only this file, so verification cannot reach Rekor, Fulcio or the TUF CDN.
TUF_ROOT="$RUN_ROOT/tuf" "$COSIGN" initialize >/dev/null
trusted_root="$(find "$RUN_ROOT/tuf" "$HOME/.sigstore/root" -type f -name trusted_root.json 2>/dev/null | head -n 1 || true)"
if [[ -z "$trusted_root" ]]; then
  echo "cosign initialize did not produce a trusted_root.json" >&2
  exit 1
fi
cp "$trusted_root" "$TRUST/trusted_root.json"
chmod a-w "$TRUST/trusted_root.json"

docker pull "$SINK_IMAGE" >/dev/null
docker pull "$VERIFY_IMAGE" >/dev/null
host_state "$RUN_ROOT/host-before.txt"

echo "Creating internal Docker network and egress sink"
touch "$RUN_ROOT/egress-sink/network-attempts.ndjson"
docker network create --internal --subnet "$NETWORK_SUBNET" --label "$RUN_LABEL" "$NETWORK" >/dev/null
docker run -d --name "$SINK" --label "$RUN_LABEL" --network "$NETWORK" --network-alias egress-sink \
  -v "$SCRIPT_DIR/egress-sink.py:/egress-sink.py:ro" \
  -v "$RUN_ROOT/egress-sink:/egress:rw" \
  "$SINK_IMAGE" python /egress-sink.py /egress/network-attempts.ndjson /egress/ready >/dev/null
# The canary runs immediately; a sink that has not bound its listeners refuses the DNS query, so the
# attempt is never recorded and the canary fails spuriously.
for _ in $(seq 1 150); do
  [[ -f "$RUN_ROOT/egress-sink/ready" ]] && break
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

denied() {
  docker run --rm --label "$RUN_LABEL" --network "$NETWORK" --dns "$SINK_IP" \
    --read-only --tmpfs /tmp --user "$(id -u):$(id -g)" -e HOME=/tmp \
    -v "$REPO_ROOT:/repo:ro" -v "$PUBLISHED:/published:ro" -v "$TRUST:/trust:ro" \
    -v "$COSIGN:/usr/local/bin/cosign:ro" -v "$OUT:/out:rw" \
    "$VERIFY_IMAGE" "$@"
}

# Canary: prove the container cannot reach the internet and that DNS lands on the sink.
denied bash -c '
  if timeout 3 bash -c "cat </dev/null >/dev/tcp/1.1.1.1/443" 2>/dev/null; then exit 90; fi
  if getent hosts canary.printfarmer.invalid. >/dev/null; then exit 91; fi
' || { echo "Network-denial canary failed: the verification container reached the network" >&2; exit 1; }
for _ in $(seq 1 25); do
  grep -q 'canary.printfarmer.invalid' "$RUN_ROOT/egress-sink/network-attempts.ndjson" && break
  sleep 0.2
done
if ! grep -q 'canary.printfarmer.invalid' "$RUN_ROOT/egress-sink/network-attempts.ndjson"; then
  echo "Network-denial canary was not recorded by the egress sink" >&2
  exit 1
fi

echo "Verifying the published bundle with network denied"
denied node /repo/scripts/ci/recovery-matrix/published-bundle.mjs verify \
  --bundle "/published/$BUNDLE_NAME" --signature "/published/$SIGNATURE_NAME" --version "$VERSION" \
  --trusted-root /trust/trusted_root.json --staging /out/staging --cosign /usr/local/bin/cosign \
  > "$RUN_ROOT/verification.json"

docker rm -f "$SINK" >/dev/null
docker network rm "$NETWORK" >/dev/null
host_state "$RUN_ROOT/host-after.txt"
BUNDLE_SHA256_AFTER="$(sha256sum "$PUBLISHED/$BUNDLE_NAME" | awk '{print $1}')"

node "$SCRIPT_DIR/published-bundle.mjs" evidence --output "$EVIDENCE" --run-id "$RUN_ID" \
  --started-at "$STARTED_AT" --harness-commit "$HARNESS_COMMIT" --verification "$RUN_ROOT/verification.json" \
  --bundle-sha256 "$BUNDLE_SHA256" --bundle-sha256-after "$BUNDLE_SHA256_AFTER" \
  --host-before "$RUN_ROOT/host-before.txt" --host-after "$RUN_ROOT/host-after.txt" \
  --network-attempts "$RUN_ROOT/egress-sink/network-attempts.ndjson" --cosign "$COSIGN"
echo "Published bundle verification evidence: $EVIDENCE"
