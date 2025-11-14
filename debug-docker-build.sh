#!/bin/bash
# Debug Docker Build Script
# This script builds the Docker image with verbose output and saves build logs

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}=== PrintFarmer Docker Build Debug ===${NC}"
echo "Build timestamp: $(date)"
echo ""

# Check if Dockerfile.multistage exists
if [ ! -f "Dockerfile.multistage" ]; then
    echo -e "${RED}Error: Dockerfile.multistage not found${NC}"
    exit 1
fi

# Set build options
BUILD_LOG="/tmp/printfarmer-build.log"
PROGRESS="${DOCKER_PROGRESS:-tty}"  # Options: auto, tty, plain (tty=pretty default, plain=verbose)
PLATFORM="${TARGETPLATFORM:-linux/amd64}"

echo -e "${BLUE}Build Configuration:${NC}"
echo "  Dockerfile: Dockerfile.multistage"
echo "  Platform: $PLATFORM"
echo "  Progress: $PROGRESS"
echo "  Log file: $BUILD_LOG"
echo ""

echo -e "${BLUE}Starting Docker build...${NC}"
echo "Command: docker build --progress=$PROGRESS -t printfarmer-multistage:latest -f Dockerfile.multistage --build-arg TARGETPLATFORM=$PLATFORM ."
echo ""

# Run build with full output
if docker build \
    --progress="$PROGRESS" \
    -t printfarmer-multistage:latest \
    -f Dockerfile.multistage \
    --build-arg TARGETPLATFORM="$PLATFORM" \
    . 2>&1 | tee "$BUILD_LOG"; then
    
    echo ""
    echo -e "${GREEN}=== Build successful! ===${NC}"
    echo "Image: printfarmer-multistage:latest"
    docker images | grep printfarmer-multistage
    
else
    echo ""
    echo -e "${RED}=== Build failed! ===${NC}"
    echo ""
    echo -e "${YELLOW}Last 100 lines of build output:${NC}"
    tail -100 "$BUILD_LOG"
    echo ""
    echo -e "${YELLOW}Searching for error patterns in build log:${NC}"
    
    # Search for common error patterns
    if grep -i "error\|failed\|exception" "$BUILD_LOG"; then
        echo ""
        echo -e "${YELLOW}Error context (10 lines around first error):${NC}"
        grep -i -A 5 -B 5 "error\|failed" "$BUILD_LOG" | head -30
    fi
    
    exit 1
fi

echo ""
echo -e "${BLUE}Build debug complete. Full log saved to: $BUILD_LOG${NC}"
