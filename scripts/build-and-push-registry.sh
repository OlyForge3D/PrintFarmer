#!/bin/bash
# Build and push OrcaSlicer images to local registry
# This script builds the optimized binary layer and worker, then pushes to local registry

set -e

REGISTRY_HOST=${REGISTRY_HOST:-localhost:5000}
ORCASLICER_VERSION=${ORCASLICER_VERSION:-2.4.0}
GITHUB_TOKEN=${GITHUB_TOKEN:-}

# Short git SHA of the source commit, injected into the worker build so
# /api/system/version reports the deployed commit (.git is not in the build context).
GIT_SHA=$(git -C "$(dirname "$0")/.." rev-parse --short HEAD 2>/dev/null || echo "unknown")

# Docker build progress flag (tty=pretty, plain=verbose, auto=smart)
DOCKER_PROGRESS=${DOCKER_PROGRESS:-tty}

echo "=== Building and Pushing OrcaSlicer Images to Local Registry ==="
echo "Registry: $REGISTRY_HOST"
echo "OrcaSlicer Version: $ORCASLICER_VERSION"
echo ""

# Check if registry is accessible
if ! curl -f http://${REGISTRY_HOST}/v2/ >/dev/null 2>&1; then
    echo "❌ Registry at $REGISTRY_HOST is not accessible"
    echo "Make sure the registry is running: ./setup-local-registry.sh"
    exit 1
fi

echo "✅ Registry is accessible"
echo ""

# Build binary layer
echo "🔨 Building binary layer: orcaslicer-binaries:$ORCASLICER_VERSION"
ORCA_BUILD_CMD=(docker build --progress="${DOCKER_PROGRESS}")
if [ -n "${DOCKER_BUILD_PLATFORM:-}" ]; then
    ORCA_BUILD_CMD+=(--platform "${DOCKER_BUILD_PLATFORM}")
fi

# Use Dockerfile.multistage with orcaslicer-binaries target (consolidated single source)
if [ ! -f "./Dockerfile.multistage" ]; then
    echo "[error] Dockerfile.multistage not found - required for consolidated OrcaSlicer builds"
    exit 1
fi

ORCA_BUILD_CMD+=( -f ./Dockerfile.multistage --target orcaslicer-binaries \
    -t "orcaslicer-binaries:$ORCASLICER_VERSION" \
    -t "orcaslicer-binaries:latest" \
    -t "$REGISTRY_HOST/orcaslicer-binaries:$ORCASLICER_VERSION" \
    -t "$REGISTRY_HOST/orcaslicer-binaries:latest" \
    --build-arg ORCASLICER_VERSION=$ORCASLICER_VERSION \
    --build-arg ALLOW_STUB=false \
    ${GITHUB_TOKEN:+--build-arg GITHUB_TOKEN=$GITHUB_TOKEN} \
    .)

"${ORCA_BUILD_CMD[@]}"

echo "✅ Binary layer built successfully"
echo ""

# Build worker using multistage target (consolidated single source)
echo "🔨 Building worker: printfarmer-orcaslicer-worker"
WORKER_CMD=(docker build --progress="${DOCKER_PROGRESS}")
if [ -n "${DOCKER_BUILD_PLATFORM:-}" ]; then
    WORKER_CMD+=(--platform "${DOCKER_BUILD_PLATFORM}")
fi
WORKER_CMD+=( -f Dockerfile.multistage --target orcaslicer-worker \
    -t "printfarmer-orcaslicer-worker:latest" \
    -t "$REGISTRY_HOST/printfarmer-orcaslicer-worker:latest" \
    -t "$REGISTRY_HOST/printfarmer-orcaslicer-worker:$ORCASLICER_VERSION" \
    --build-arg ORCASLICER_VERSION=$ORCASLICER_VERSION \
    --build-arg ALLOW_STUB=false \
    --build-arg GIT_SHA=$GIT_SHA \
    .)

"${WORKER_CMD[@]}"

echo "✅ Worker built successfully"
echo ""

# Push images to registry
echo "📤 Pushing binary layer to registry..."
docker push "$REGISTRY_HOST/orcaslicer-binaries:$ORCASLICER_VERSION"
docker push "$REGISTRY_HOST/orcaslicer-binaries:latest"

echo "📤 Pushing worker to registry..."
docker push "$REGISTRY_HOST/printfarmer-orcaslicer-worker:latest"
docker push "$REGISTRY_HOST/printfarmer-orcaslicer-worker:$ORCASLICER_VERSION"

echo ""
echo "🎉 Build and Push Complete!"
echo ""
echo "📋 Images available in registry:"
echo "  • $REGISTRY_HOST/orcaslicer-binaries:$ORCASLICER_VERSION"
echo "  • $REGISTRY_HOST/orcaslicer-binaries:latest"
echo "  • $REGISTRY_HOST/printfarmer-orcaslicer-worker:$ORCASLICER_VERSION"
echo "  • $REGISTRY_HOST/printfarmer-orcaslicer-worker:latest"
echo ""
echo "🚀 To deploy on another server:"
echo "  docker pull $REGISTRY_HOST/orcaslicer-binaries:$ORCASLICER_VERSION"
echo "  docker pull $REGISTRY_HOST/printfarmer-orcaslicer-worker:latest"
echo ""
echo "📊 Registry contents:"
curl -s "http://${REGISTRY_HOST}/v2/_catalog" | jq -r '.repositories[]' | sort | sed 's/^/  • /'