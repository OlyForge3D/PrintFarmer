#!/bin/bash
# Build script for OrcaSlicer binary layer optimization
# This script builds the binary layer first for optimal caching

set -e

ORCASLICER_VERSION=${ORCASLICER_VERSION:-2.3.1}
GITHUB_TOKEN=${GITHUB_TOKEN:-}

echo "=== Building OrcaSlicer Binary Layer (Optimized Caching) ==="
echo "Version: $ORCASLICER_VERSION"

# Build the binary layer first - this will be cached and reused
echo "Building orcaslicer-binaries:$ORCASLICER_VERSION..."
docker build \
    -f Dockerfile.orcaslicer-binaries \
    -t orcaslicer-binaries:$ORCASLICER_VERSION \
    -t orcaslicer-binaries:latest \
    --build-arg ORCASLICER_VERSION=$ORCASLICER_VERSION \
    --build-arg ALLOW_STUB=false \
    ${GITHUB_TOKEN:+--build-arg GITHUB_TOKEN=$GITHUB_TOKEN} \
    .

echo "✅ Binary layer built successfully!"
echo ""
echo "=== Building OrcaSlicer Worker (Using Cached Binaries) ==="

# Now build the worker, which will use the cached binary layer
docker build \
    -f Dockerfile.orcaslicer \
    -t printfarmer-orcaslicer-worker \
    --build-arg ORCASLICER_VERSION=$ORCASLICER_VERSION \
    --build-arg ALLOW_STUB=false \
    .

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
echo "  docker build -f Dockerfile.orcaslicer -t printfarmer-orcaslicer-worker ."
echo ""
echo "To update binaries (when new OrcaSlicer version available):"
echo "  ORCASLICER_VERSION=x.y.z $0"