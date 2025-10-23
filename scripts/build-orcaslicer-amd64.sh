#!/usr/bin/env bash
set -euo pipefail
# Build the orcaslicer-worker image for amd64 using optimized binary layer caching.
# This script builds both the binary layer and worker for amd64 to obtain a real (non-stub) OrcaSlicer binary even on arm64 hosts.
# Requires docker buildx with an amd64 builder configured (example: docker run --privileged --rm tonistiigi/binfmt --install all).
# Usage:
#   scripts/build-orcaslicer-amd64.sh [tag]
# Default tag: orcaslicer-worker:amd64
TAG=${1:-orcaslicer-worker:amd64}

if ! docker buildx version >/dev/null 2>&1; then
  echo "[buildx] ERROR: docker buildx not available. Install Docker Buildx before using this script." >&2
  exit 2
fi

ORCA_VERSION="${ORCASLICER_VERSION:-2.3.1}"

echo "[buildx] Building orcaslicer-binaries:${ORCA_VERSION} for linux/amd64 (cached layer)";
DOCKER_BUILDKIT=1 docker buildx build \
  --platform linux/amd64 \
  -t "orcaslicer-binaries:${ORCA_VERSION}" \
  -f Dockerfile.orcaslicer-binaries \
  --build-arg ORCASLICER_VERSION="${ORCA_VERSION}" \
  --build-arg ALLOW_STUB=false \
  --load .

echo "[buildx] Building worker image for linux/amd64 -> ${TAG} (using cached binaries)";
DOCKER_BUILDKIT=1 docker buildx build \
  --platform linux/amd64 \
  -t "${TAG}" \
  -f Dockerfile.orcaslicer \
  --build-arg ORCASLICER_VERSION="${ORCA_VERSION}" \
  --load .

# Tag for local development stack compatibility
docker tag "${TAG}" printfarmer-orca-worker:latest

echo "[buildx] Build complete. To verify with strict mode using the amd64 image run:"
echo "  ORCA_IMAGE=${TAG} scripts/verify-orcaslicer-worker.sh require-real"