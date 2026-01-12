#!/bin/bash
# Build script for OrcaSlicer binary layer optimization
# This script builds the binary layer first for optimal caching using consolidated multistage Dockerfile

set -e

ORCASLICER_VERSION=${ORCASLICER_VERSION:-2.3.1}
GITHUB_TOKEN=${GITHUB_TOKEN:-}

# Docker build progress flag (tty=pretty, plain=verbose, auto=smart)
DOCKER_PROGRESS=${DOCKER_PROGRESS:-tty}

echo "=== Building OrcaSlicer Binary Layer (Optimized Caching) ==="
echo "Version: $ORCASLICER_VERSION"

# Verify Dockerfile.multistage exists
if [ ! -f "./Dockerfile.multistage" ]; then
    echo "ERROR: Dockerfile.multistage not found at repository root"
    exit 1
fi

# Build the binary layer first - this will be cached and reused
echo "Building orcaslicer-binaries:$ORCASLICER_VERSION using Dockerfile.multistage..."
ORCA_BIN_CMD=(docker build --progress="${DOCKER_PROGRESS}")
if [ -n "${DOCKER_BUILD_PLATFORM:-}" ]; then
    ORCA_BIN_CMD+=(--platform "${DOCKER_BUILD_PLATFORM}")
fi

ORCA_BIN_CMD+=( -f "./Dockerfile.multistage" --target orcaslicer-binaries \
    -t orcaslicer-binaries:$ORCASLICER_VERSION \
    -t orcaslicer-binaries:latest \
    --build-arg ORCASLICER_VERSION=$ORCASLICER_VERSION \
    --build-arg ALLOW_STUB=false \
    ${GITHUB_TOKEN:+--build-arg GITHUB_TOKEN=$GITHUB_TOKEN} \
    .)

"${ORCA_BIN_CMD[@]}"

echo "✅ Binary layer built successfully!"
echo ""
echo "=== Building OrcaSlicer Worker (Using Cached Binaries) ==="

# Now build the worker, which will use the cached binary layer
WORKER_CMD=(docker build --progress="${DOCKER_PROGRESS}")
if [ -n "${DOCKER_BUILD_PLATFORM:-}" ]; then
    WORKER_CMD+=(--platform "${DOCKER_BUILD_PLATFORM}")
fi
WORKER_CMD+=( -f Dockerfile.multistage \
    --target orcaslicer-worker \
    -t printfarmer-orcaslicer-worker \
    --build-arg ORCASLICER_VERSION=$ORCASLICER_VERSION \
    --build-arg ALLOW_STUB=false \
    .)

"${WORKER_CMD[@]}"

echo "✅ OrcaSlicer worker built successfully!"
echo ""
echo "=== Build Summary ==="
echo "Binary layer: orcaslicer-binaries:$ORCASLICER_VERSION"
echo "Worker image: printfarmer-orcaslicer-worker"
echo ""
echo "💡 Future builds of the worker will skip binary download/extraction"
echo "   and only rebuild when application code changes."
echo ""
echo "To rebuild only the worker (fast):"
echo "  docker build -f Dockerfile.multistage --target orcaslicer-worker -t printfarmer-orcaslicer-worker ."
echo ""
echo "To update binaries (when new OrcaSlicer version available):"
echo "  ORCASLICER_VERSION=x.y.z $0"