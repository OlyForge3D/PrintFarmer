#!/usr/bin/env bash

# Rebuild and push pre-upgraded base images specifically for amd64 platform
# This ensures they have proper amd64 manifest in registry for cross-platform builds

set -euo pipefail

REGISTRY_HOST="${DOCKER_REGISTRY:-localhost:5001}"
PLATFORM="linux/amd64"

echo "=== Rebuilding Base Images as ${PLATFORM} for Registry ==="
echo "Registry: ${REGISTRY_HOST}"
echo ""

# Change to repo root
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# Array of images: (name:tag | dockerfile_path)
declare -a IMAGES=(
    "nginx:alpine-upgraded|dockerfiles/Dockerfile.base-nginx"
    "node:22-alpine-upgraded|dockerfiles/Dockerfile.base-node"
    "postgres:17-alpine-upgraded|dockerfiles/Dockerfile.base-postgres"
    "sdk:10.0.101-alpine-upgraded|dockerfiles/Dockerfile.base-sdk"
    "aspnet:10.0.1-alpine-upgraded|dockerfiles/Dockerfile.base-aspnet"
    "ubuntu:24.04-upgraded|dockerfiles/Dockerfile.base-ubuntu"
)

success=0
failed=0

for spec in "${IMAGES[@]}"; do
    IFS='|' read -r image dockerfile <<< "$spec"
    registry_image="${REGISTRY_HOST}/${image}"
    
    echo "📦 Building: ${image}"
    echo "   Dockerfile: ${dockerfile}"
    echo "   Platform: ${PLATFORM}"
    echo "   Registry: ${registry_image}"
    
    # Build locally first for this platform
    if docker build \
        --platform "${PLATFORM}" \
        --file "${dockerfile}" \
        --tag "${registry_image}" \
        . 2>&1 | grep -E "^(Step|Successfully|ERROR)" | tail -10; then
        
        echo "✓ Built locally: ${image}"
        
        # Now push to registry
        if docker push "${registry_image}" 2>&1 | grep -E "(Pushing|Digest|error)" | tail -3; then
            echo "✓ Pushed to registry: ${registry_image}"
            ((success++)) || true
        else
            echo "✗ Failed to push: ${registry_image}"
            ((failed++)) || true
        fi
    else
        echo "✗ Failed to build: ${image}"
        ((failed++)) || true
    fi
    echo ""
done

echo "========================================="
echo "Build Summary:"
echo "  ✓ Successful: ${success}"
if [ ${failed} -gt 0 ]; then
    echo "  ✗ Failed: ${failed}"
fi
echo "========================================="

if [ ${failed} -gt 0 ]; then
    exit 1
fi
