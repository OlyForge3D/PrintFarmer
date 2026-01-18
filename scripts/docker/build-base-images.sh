#!/bin/bash
# Script to build and cache pre-upgraded base images for offline deployments
# Run this ONCE online to create cached tar files for offline use

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$SCRIPT_DIR/dockerfiles"
CACHE_DIR="${CACHE_DIR:-./.docker-cache}"

echo "=========================================="
echo "PrintFarmer Base Image Pre-Cache Builder"
echo "=========================================="
echo ""
echo "This script builds and caches pre-upgraded base images."
echo "Cache directory: $CACHE_DIR"
echo ""

# Create cache directory
mkdir -p "$CACHE_DIR"

# Array of base images to build and cache
declare -a IMAGES=(
    "ubuntu:24.04|Dockerfile.base-ubuntu|ubuntu-24.04-upgraded"
    "node:22-alpine|Dockerfile.base-node|node-22-alpine-upgraded"
    "mcr.microsoft.com/dotnet/aspnet:10.0-noble|Dockerfile.base-aspnet|aspnet-10.0-noble-upgraded"
    "mcr.microsoft.com/dotnet/sdk:10.0-noble|Dockerfile.base-sdk|dotnet-sdk-10.0-noble-upgraded"
    "postgres:16-alpine|Dockerfile.base-postgres|postgres-16-alpine-upgraded"
    "nginx:alpine|Dockerfile.base-nginx|nginx-alpine-upgraded"
)

SUCCESS_COUNT=0
FAIL_COUNT=0

for image_config in "${IMAGES[@]}"; do
    IFS='|' read -r base_image dockerfile tag <<< "$image_config"
    
    echo "=========================================="
    echo "Building: $tag"
    echo "  Base: $base_image"
    echo "  Dockerfile: $dockerfile"
    echo "=========================================="
    
    if docker build \
        -f "$DOCKER_DIR/$dockerfile" \
        -t "$tag" \
        --label="printfarmer-precache=true" \
        .; then
        
        echo "✓ Build successful: $tag"
        
        # Export to tar
        tar_file="$CACHE_DIR/${tag//:/-}.tar"
        echo "  Exporting to: $tar_file"
        
        if docker save -o "$tar_file" "$tag"; then
            tar_size=$(du -h "$tar_file" | cut -f1)
            echo "✓ Exported: $tar_file ($tar_size)"
            ((SUCCESS_COUNT++))
        else
            echo "✗ Export failed: $tar_file"
            ((FAIL_COUNT++))
        fi
    else
        echo "✗ Build failed: $tag"
        ((FAIL_COUNT++))
    fi
    
    echo ""
done

echo "=========================================="
echo "Pre-Cache Build Summary"
echo "=========================================="
echo "Successful: $SUCCESS_COUNT"
echo "Failed: $FAIL_COUNT"
echo ""
echo "Cached images in: $CACHE_DIR"
ls -lh "$CACHE_DIR"/*.tar 2>/dev/null || echo "No tar files found"
echo ""
echo "To deploy offline:"
echo "  1. Transfer all .tar files from $CACHE_DIR to offline system"
echo "  2. Run: docker load -i ubuntu-24.04-upgraded.tar"
echo "  3. Run: docker load -i node-22-alpine-upgraded.tar"
echo "  4. Run: docker load -i aspnet-10.0-noble-upgraded.tar"
echo "  5. Run: docker load -i dotnet-sdk-10.0-noble-upgraded.tar"
echo "  6. Run: docker load -i postgres-16-alpine-upgraded.tar"
echo "  7. Run: docker load -i nginx-alpine-upgraded.tar"
echo "  8. Then deploy: ./scripts/deploy-docker.sh --pull=missing"
echo ""
