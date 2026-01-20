#!/usr/bin/env bash

# Rebuild all 6 pre-upgraded base images for amd64 platform
# Push to local Docker registry with proper amd64 architecture metadata

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

REGISTRY_HOST="${DOCKER_REGISTRY:-localhost:5001}"

echo "🔨 Rebuilding 6 base images for amd64 platform..."
echo "📍 Registry: ${REGISTRY_HOST}"
echo ""

# Array of dockerfiles and images
declare -a DOCKERFILES=(
    "dockerfiles/Dockerfile.base-nginx"
    "dockerfiles/Dockerfile.base-node"
    "dockerfiles/Dockerfile.base-postgres"
    "dockerfiles/Dockerfile.base-sdk"
    "dockerfiles/Dockerfile.base-aspnet"
    "dockerfiles/Dockerfile.base-ubuntu"
)

declare -a IMAGES=(
    "nginx:alpine-upgraded"
    "node:22-alpine-upgraded"
    "postgres:17-alpine-upgraded"
    "sdk:10.0.102-alpine-upgraded"
    "aspnet:10.0.1-alpine-upgraded"
    "ubuntu:24.04-upgraded"
)

success=0
failed=0

for i in "${!DOCKERFILES[@]}"; do
    dockerfile="${DOCKERFILES[$i]}"
    image="${IMAGES[$i]}"
    registry_image="${REGISTRY_HOST}/${image}"
    
    printf "%-35s " "Building: ${image}"
    
    # Build locally for amd64
    if DOCKER_BUILDKIT=1 docker build \
        --platform linux/amd64 \
        --file "${dockerfile}" \
        --tag "${registry_image}" \
        . >/dev/null 2>&1; then
        
        printf "✓ Local | "
        
        # Push to registry
        if docker push "${registry_image}" >/dev/null 2>&1; then
            echo "✓ Pushed"
            ((success++)) || true
        else
            echo "✗ Push failed"
            ((failed++)) || true
        fi
    else
        echo "✗ Build failed"
        ((failed++)) || true
    fi
done

echo ""
echo "========================================="
echo "Summary: ${success} successful, ${failed} failed"
echo "========================================="

if [ ${failed} -gt 0 ]; then
    exit 1
fi

echo "✓ All images rebuilt for amd64 and pushed to ${REGISTRY_HOST}"
