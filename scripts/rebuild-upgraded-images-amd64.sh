#!/usr/bin/env bash

# Rebuild pre-upgraded base images as amd64 for cross-platform deployment
# This creates amd64 images that can be used on Linux servers and pushed to registry

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

print_info() { echo -e "${BLUE}ℹ${NC} $*"; }
print_success() { echo -e "${GREEN}✓${NC} $*"; }
print_warning() { echo -e "${YELLOW}⚠${NC} $*"; }
print_error() { echo -e "${RED}✗${NC} $*"; }

REGISTRY_HOST="${DOCKER_REGISTRY:-localhost:5001}"
DOCKER_BUILDX_PLATFORM="${DOCKER_BUILDX_PLATFORM:-linux/amd64}"

print_info "Rebuilding base images as ${DOCKER_BUILDX_PLATFORM} for registry"
print_info "Registry: ${REGISTRY_HOST}"
print_info ""

# Check if buildx is available
if ! docker buildx version >/dev/null 2>&1; then
    print_error "docker buildx not available. Install it first:"
    echo "  docker buildx create --name multiarch --platform linux/amd64,linux/arm64"
    exit 1
fi

# Create builder if needed
BUILDER_NAME="multiarch"
if ! docker buildx ls | grep -q "^${BUILDER_NAME}\s"; then
    print_info "Creating buildx builder: ${BUILDER_NAME}"
    docker buildx create --name "${BUILDER_NAME}" --platform linux/amd64,linux/arm64 || true
fi

# List of images to rebuild with their Dockerfile locations and build context
declare -a IMAGES_TO_BUILD=(
    "sdk:10.0.102-alpine-upgraded|Dockerfile.base-sdk|dockerfiles"
    "aspnet:10.0.1-alpine-upgraded|Dockerfile.base-aspnet|dockerfiles"
    "ubuntu:24.04-upgraded|Dockerfile.base-ubuntu|dockerfiles"
    "nginx:alpine-upgraded|Dockerfile.base-nginx|dockerfiles"
    "node:22-alpine-upgraded|Dockerfile.base-node|dockerfiles"
    "postgres:17-alpine-upgraded|Dockerfile.base-postgres|dockerfiles"
)

print_info "Building and pushing ${#IMAGES_TO_BUILD[@]} images as ${DOCKER_BUILDX_PLATFORM}..."
echo

success=0
failed=0

for build_spec in "${IMAGES_TO_BUILD[@]}"; do
    IFS='|' read -r image dockerfile context <<< "$build_spec"
    
    registry_image="${REGISTRY_HOST}/${image}"
    dockerfile_path="dockerfiles/${dockerfile}"
    
    print_info "Building: ${image}"
    print_info "  Dockerfile: ${dockerfile_path}"
    print_info "  Context: ${context}"
    print_info "  Target: ${registry_image}"
    print_info "  Platform: ${DOCKER_BUILDX_PLATFORM}"
    
    # Use buildx to build and push directly to registry in one step
    # This ensures proper platform metadata is set
    if docker buildx build \
        --builder "${BUILDER_NAME}" \
        --platform "${DOCKER_BUILDX_PLATFORM}" \
        --file "${dockerfile_path}" \
        --tag "${registry_image}" \
        --push \
        "${context}" 2>&1 | tail -5; then
        print_success "Built and pushed: ${registry_image}"
        ((success++)) || true
    else
        print_error "Failed to build: ${image}"
        ((failed++)) || true
    fi
    echo
done

echo
print_info "Build summary:"
print_success "Successful: ${success}"
print_warning "Failed: ${failed}"

if [ ${failed} -gt 0 ]; then
    exit 1
fi

print_success "All images rebuilt as ${DOCKER_BUILDX_PLATFORM} and pushed to ${REGISTRY_HOST}"
