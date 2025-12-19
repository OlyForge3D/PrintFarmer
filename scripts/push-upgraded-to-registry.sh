#!/bin/bash
# Push pre-upgraded Docker images to local registry
# This makes the pre-built images available for docker compose builds
# Much simpler than TAR file management!

set -euo pipefail

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

print_header() {
    echo -e "\n${BLUE}╔════════════════════════════════════════════════════════════════╗${NC}"
    echo -e "${BLUE}║${NC} $1"
    echo -e "${BLUE}╚════════════════════════════════════════════════════════════════╝${NC}\n"
}

print_success() {
    echo -e "${GREEN}✓${NC} $1"
}

print_error() {
    echo -e "${RED}✗${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}⚠${NC} $1"
}

print_info() {
    echo -e "${BLUE}ℹ${NC} $1"
}

REGISTRY_HOST=${REGISTRY_HOST:-localhost:5001}  # macOS AirPlay uses 5000, so default to 5001
REGISTRY_PORT=${REGISTRY_PORT:-5001}

print_header "Push Pre-Upgraded Images to Local Registry"

# Pre-upgraded images that were built with --prepare-offline
UPGRADED_IMAGES=(
    "mcr.microsoft.com/dotnet/sdk:9.0-upgraded"
    "mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim-upgraded"
    "ubuntu:24.04-upgraded"
    "node:22-alpine-upgraded"
    "postgres:16-alpine-upgraded"
    "nginx:alpine-upgraded"
)

print_info "Registry: $REGISTRY_HOST"
echo

# Step 1: Start registry if not running
print_info "Checking if local registry is running..."

if docker ps | grep -q local-registry; then
    print_success "Local registry is running"
else
    print_warning "Local registry not running - starting it..."
    print_info "Running: ./setup-local-registry.sh"
    if bash ./setup-local-registry.sh; then
        print_success "Registry started successfully"
    else
        print_error "Failed to start registry"
        exit 1
    fi
fi

echo

# Step 2: Verify all upgraded images exist locally
print_info "Checking for pre-upgraded images..."
missing=0
for image in "${UPGRADED_IMAGES[@]}"; do
    if docker image inspect "$image" >/dev/null 2>&1; then
        print_success "$image"
    else
        print_warning "$image (NOT FOUND - will skip)"
        ((missing++)) || true
    fi
done

if [ $missing -eq ${#UPGRADED_IMAGES[@]} ]; then
    print_error "No upgraded images found!"
    print_info "Run: ./scripts/deploy-docker.sh --prepare-offline"
    exit 1
fi

echo

# Step 3: Test registry connectivity
print_info "Testing registry connectivity..."
if curl -f http://${REGISTRY_HOST}/v2/ >/dev/null 2>&1; then
    print_success "Registry is accessible at $REGISTRY_HOST"
else
    print_error "Cannot reach registry at $REGISTRY_HOST"
    print_info "Verify registry is running or set REGISTRY_HOST environment variable"
    exit 1
fi

echo

# Step 4: Tag and push images
print_header "Pushing Images to Registry"

success=0
failed=0

for image in "${UPGRADED_IMAGES[@]}"; do
    # Skip if image doesn't exist
    if ! docker image inspect "$image" >/dev/null 2>&1; then
        print_warning "Skipping $image (not found)"
        continue
    fi
    
    # Create registry image name (remove special characters)
    registry_image="${REGISTRY_HOST}/${image}"
    
    print_info "Tagging: $image → $registry_image"
    if docker tag "$image" "$registry_image"; then
        print_info "Pushing: $registry_image (platform: linux/amd64)"
        # Push with explicit platform to ensure manifest is set correctly
        if docker push "$registry_image" 2>&1 | tail -3; then
            print_success "Pushed: $registry_image"
            ((success++)) || true
        else
            print_error "Failed to push: $registry_image"
            ((failed++)) || true
        fi
    else
        print_error "Failed to tag: $image"
        ((failed++)) || true
    fi
    echo
done

print_header "Summary"
print_success "Successfully pushed: $success images"
if [ $failed -gt 0 ]; then
    print_warning "Failed to push: $failed images"
fi

echo

# Step 5: Configure deployment to use registry
print_header "Next Steps"

print_info "1. Update your deployment to use the local registry:"
print_info "   Option A: Set DOCKER_REGISTRY environment variable"
echo "      export DOCKER_REGISTRY=localhost:5000"
echo "      ./scripts/deploy-docker.sh --non-interactive"
echo

print_info "   Option B: Manually configure compose file to use registry images"
echo "      Edit docker-compose.yml and change:"
echo "        FROM nginx:alpine → FROM localhost:5000/nginx:alpine-upgraded"
echo

print_info "2. Run deployment:"
echo "   ./scripts/deploy-docker.sh --non-interactive"
echo

print_info "3. Verify images are being used:"
echo "   docker images | grep localhost:5000"
echo

print_success "All upgraded images are now available in local registry!"
print_info "Registry location: $REGISTRY_HOST"
print_info "Data stored at: \$HOME/docker-registry-data"
