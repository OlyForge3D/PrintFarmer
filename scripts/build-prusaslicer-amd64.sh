#!/usr/bin/env bash
set -euo pipefail
# Build the prusaslicer-worker image for amd64 to obtain a real (non-stub) PrusaSlicer binary even on arm64 hosts.
# Requires docker buildx with an amd64 builder configured (e.g. docker run --privileged --rm tonistiigi/binfmt --install all).
# Usage:
#   scripts/build-prusaslicer-amd64.sh [tag]
# Default tag: prusaslicer-worker:amd64
TAG=${1:-prusaslicer-worker:amd64}

if ! docker buildx version >/dev/null 2>&1; then
  echo "[buildx] ERROR: docker buildx not available. Install Docker Buildx before using this script." >&2
  exit 2
fi

echo "[buildx] Building image for linux/amd64 -> ${TAG}";
DOCKER_BUILDKIT=1 docker buildx build \
  --platform linux/amd64 \
  -t "${TAG}" \
  -f Dockerfile.prusaslicer \
  --load .

echo "[buildx] Build complete. To verify with strict mode using the amd64 image run:"
echo "  PRUSA_IMAGE=${TAG} scripts/verify-prusaslicer-worker.sh require-real"
