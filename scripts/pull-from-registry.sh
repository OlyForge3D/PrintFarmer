#!/bin/bash
# Pull and deploy OrcaSlicer images from local registry
# Run this script on your deployment server

set -e

REGISTRY_HOST=${REGISTRY_HOST:-}
ORCASLICER_VERSION=${ORCASLICER_VERSION:-2.3.2}

if [ -z "$REGISTRY_HOST" ]; then
    echo "❌ REGISTRY_HOST environment variable is required"
    echo "Usage: REGISTRY_HOST=192.168.1.100:5000 $0"
    echo "Or:    $0 192.168.1.100:5000"
    exit 1
fi

# Allow passing registry host as first argument
if [ -n "$1" ]; then
    REGISTRY_HOST="$1"
fi

echo "=== Pulling OrcaSlicer Images from Local Registry ==="
echo "Registry: $REGISTRY_HOST"
echo "OrcaSlicer Version: $ORCASLICER_VERSION"
echo ""

# Check if registry is accessible
if ! curl -f "http://${REGISTRY_HOST}/v2/" >/dev/null 2>&1; then
    echo "❌ Registry at $REGISTRY_HOST is not accessible"
    echo "Make sure:"
    echo "  1. Registry is running on the dev machine"
    echo "  2. This machine can reach the registry IP"
    echo "  3. Insecure registries are configured in Docker daemon"
    exit 1
fi

echo "✅ Registry is accessible"
echo ""

# Pull binary layer
echo "📥 Pulling binary layer..."
docker pull "$REGISTRY_HOST/orcaslicer-binaries:$ORCASLICER_VERSION"
docker pull "$REGISTRY_HOST/orcaslicer-binaries:latest"

# Pull worker
echo "📥 Pulling worker..."
docker pull "$REGISTRY_HOST/printfarmer-orcaslicer-worker:latest"
docker pull "$REGISTRY_HOST/printfarmer-orcaslicer-worker:$ORCASLICER_VERSION"

echo ""
echo "🏷️  Tagging images for local use..."

# Tag for local docker-compose compatibility
docker tag "$REGISTRY_HOST/orcaslicer-binaries:$ORCASLICER_VERSION" "orcaslicer-binaries:$ORCASLICER_VERSION"
docker tag "$REGISTRY_HOST/orcaslicer-binaries:latest" "orcaslicer-binaries:latest"
docker tag "$REGISTRY_HOST/printfarmer-orcaslicer-worker:latest" "printfarmer-orcaslicer-worker:latest"
docker tag "$REGISTRY_HOST/printfarmer-orcaslicer-worker:$ORCASLICER_VERSION" "printfarmer-orcaslicer-worker:$ORCASLICER_VERSION"

echo ""
echo "✅ Pull and Tag Complete!"
echo ""
echo "📋 Images ready for deployment:"
echo "  • orcaslicer-binaries:$ORCASLICER_VERSION"
echo "  • orcaslicer-binaries:latest"
echo "  • printfarmer-orcaslicer-worker:latest"
echo "  • printfarmer-orcaslicer-worker:$ORCASLICER_VERSION"
echo ""
echo "🚀 Deploy with:"
echo "  docker compose --profile orca up orcaslicer-worker"
echo ""
echo "💡 Note: Images are now cached locally and will work without registry access"