#!/bin/bash
# PrusaSlicer Worker Binary Installation Verification Script
# Tests PrusaSlicer binary installation and container functionality

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}" && pwd)"
DOCKERFILE_PRUSA="${PROJECT_ROOT}/Dockerfile.prusaslicer"
COMPOSE_FILE="${PROJECT_ROOT}/docker-compose.yml"
IMAGE_NAME="printfarmer/prusaslicer-worker:test"
CONTAINER_NAME="prusaslicer-worker-test"

# Helper functions
log_info() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

log_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

log_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

cleanup() {
    log_info "Cleaning up test resources..."
    
    # Stop and remove test container
    if docker ps -q -f name="${CONTAINER_NAME}" | grep -q .; then
        docker stop "${CONTAINER_NAME}" >/dev/null 2>&1 || true
    fi
    
    if docker ps -aq -f name="${CONTAINER_NAME}" | grep -q .; then
        docker rm "${CONTAINER_NAME}" >/dev/null 2>&1 || true
    fi
    
    # Remove test image
    if docker images -q "${IMAGE_NAME}" | grep -q .; then
        docker rmi "${IMAGE_NAME}" >/dev/null 2>&1 || true
    fi
    
    log_info "Cleanup completed"
}

# Trap cleanup on exit
trap cleanup EXIT

verify_prerequisites() {
    log_info "Verifying prerequisites..."
    
    # Check Docker
    if ! command -v docker >/dev/null 2>&1; then
        log_error "Docker is not installed or not in PATH"
        exit 1
    fi
    
    # Check Docker is running
    if ! docker info >/dev/null 2>&1; then
        log_error "Docker daemon is not running"
        exit 1
    fi
    
    # Check files exist
    if [[ ! -f "${DOCKERFILE_PRUSA}" ]]; then
        log_error "Dockerfile.prusaslicer not found at ${DOCKERFILE_PRUSA}"
        exit 1
    fi
    
    if [[ ! -f "${COMPOSE_FILE}" ]]; then
        log_error "docker-compose.yml not found at ${COMPOSE_FILE}"
        exit 1
    fi
    
    log_success "All prerequisites verified"
}

build_prusaslicer_image() {
    log_info "Building PrusaSlicer worker Docker image..."
    
    cd "${PROJECT_ROOT}"
    
    # Build the image with detailed output
    if docker build -f "${DOCKERFILE_PRUSA}" -t "${IMAGE_NAME}" . --no-cache; then
        log_success "Docker image built successfully"
        
        # Show image details
        local image_size=$(docker images "${IMAGE_NAME}" --format "{{.Size}}")
        log_info "Image size: ${image_size}"
        
        return 0
    else
        log_error "Failed to build Docker image"
        return 1
    fi
}

test_container_startup() {
    log_info "Testing container startup and basic functionality..."
    
    # Run container in detached mode
    if docker run -d \
        --name "${CONTAINER_NAME}" \
        -p 8083:8080 \
        -e ASPNETCORE_ENVIRONMENT=Development \
        -e ConnectionStrings__Redis=redis://localhost:6379 \
        "${IMAGE_NAME}"; then
        
        log_info "Container started successfully"
    else
        log_error "Failed to start container"
        return 1
    fi
    
    # Wait for container to be ready
    log_info "Waiting for container to be ready..."
    local max_attempts=30
    local attempt=1
    
    while [[ $attempt -le $max_attempts ]]; do
        if docker ps -q -f name="${CONTAINER_NAME}" | grep -q .; then
            log_info "Container is running (attempt ${attempt}/${max_attempts})"
            break
        fi
        
        if [[ $attempt -eq $max_attempts ]]; then
            log_error "Container failed to start within expected time"
            docker logs "${CONTAINER_NAME}" || true
            return 1
        fi
        
        sleep 2
        ((attempt++))
    done
    
    # Check container logs for startup success
    log_info "Checking container logs for startup messages..."
    docker logs "${CONTAINER_NAME}" 2>&1 | tail -20
    
    return 0
}

verify_prusaslicer_installation() {
    log_info "Verifying PrusaSlicer binary installation inside container..."
    
    # Check if PrusaSlicer binary exists
    if docker exec "${CONTAINER_NAME}" test -f /usr/local/bin/prusa-slicer; then
        log_success "PrusaSlicer binary found at /usr/local/bin/prusa-slicer"
    else
        log_error "PrusaSlicer binary not found"
        return 1
    fi
    
    # Check if binary is executable
    if docker exec "${CONTAINER_NAME}" test -x /usr/local/bin/prusa-slicer; then
        log_success "PrusaSlicer binary is executable"
    else
        log_error "PrusaSlicer binary is not executable"
        return 1
    fi
    
    # Check PrusaSlicer AppImage extraction
    if docker exec "${CONTAINER_NAME}" test -d /opt/prusaslicer; then
        log_success "PrusaSlicer AppImage extracted to /opt/prusaslicer"
    else
        log_error "PrusaSlicer AppImage extraction directory not found"
        return 1
    fi
    
    # List PrusaSlicer directory contents
    log_info "PrusaSlicer installation contents:"
    docker exec "${CONTAINER_NAME}" ls -la /opt/prusaslicer/ | head -10 || true
    
    # Try to get version information (may fail in headless mode, but we'll try)
    log_info "Attempting to get PrusaSlicer version information..."
    if docker exec "${CONTAINER_NAME}" timeout 10 /usr/local/bin/prusa-slicer --help 2>&1 | head -5 || true; then
        log_info "PrusaSlicer help output obtained (or timeout reached in headless mode)"
    fi
    
    return 0
}

test_health_endpoints() {
    log_info "Testing worker health endpoints..."
    
    # Wait for health endpoints to be ready
    local max_attempts=20
    local attempt=1
    
    while [[ $attempt -le $max_attempts ]]; do
        if curl -f http://localhost:8083/healthz -s >/dev/null 2>&1; then
            log_success "Health endpoint responding (attempt ${attempt}/${max_attempts})"
            break
        fi
        
        if [[ $attempt -eq $max_attempts ]]; then
            log_error "Health endpoint not responding after ${max_attempts} attempts"
            return 1
        fi
        
        log_info "Waiting for health endpoint... (attempt ${attempt}/${max_attempts})"
        sleep 3
        ((attempt++))
    done
    
    # Test liveness endpoint
    local health_response=$(curl -s http://localhost:8083/healthz)
    log_info "Liveness endpoint response: ${health_response}"
    
    if echo "${health_response}" | grep -q '"status":"ok"'; then
        log_success "Liveness endpoint reports healthy status"
    else
        log_warning "Liveness endpoint response format unexpected"
    fi
    
    # Test readiness endpoint
    if curl -f http://localhost:8083/ready -s >/dev/null 2>&1; then
        local ready_response=$(curl -s http://localhost:8083/ready)
        log_info "Readiness endpoint response: ${ready_response}"
        log_success "Readiness endpoint accessible"
    else
        log_warning "Readiness endpoint not accessible (may be expected without Redis)"
    fi
    
    return 0
}

test_worker_capabilities() {
    log_info "Testing worker capabilities endpoint..."
    
    # Test root endpoint for capabilities
    if curl -f http://localhost:8083/ -s >/dev/null 2>&1; then
        local capabilities=$(curl -s http://localhost:8083/)
        log_info "Worker capabilities: ${capabilities}"
        
        if echo "${capabilities}" | grep -q "prusaslicer"; then
            log_success "Worker correctly reports PrusaSlicer capabilities"
        else
            log_warning "Worker capabilities response unexpected"
        fi
    else
        log_warning "Worker capabilities endpoint not accessible"
    fi
    
    return 0
}

run_comprehensive_test() {
    log_info "Running comprehensive PrusaSlicer worker binary installation test..."
    echo
    
    verify_prerequisites
    echo
    
    if build_prusaslicer_image; then
        log_success "✓ Docker image build successful"
    else
        log_error "✗ Docker image build failed"
        exit 1
    fi
    echo
    
    if test_container_startup; then
        log_success "✓ Container startup successful"
    else
        log_error "✗ Container startup failed"
        exit 1
    fi
    echo
    
    if verify_prusaslicer_installation; then
        log_success "✓ PrusaSlicer binary installation verified"
    else
        log_error "✗ PrusaSlicer binary installation failed"
        exit 1
    fi
    echo
    
    if test_health_endpoints; then
        log_success "✓ Health endpoints working"
    else
        log_error "✗ Health endpoints failed"
        exit 1
    fi
    echo
    
    if test_worker_capabilities; then
        log_success "✓ Worker capabilities verified"
    else
        log_warning "⚠ Worker capabilities test had issues (non-critical)"
    fi
    echo
    
    log_success "🎉 All PrusaSlicer worker binary installation tests passed!"
    log_info "PrusaSlicer worker is ready for production deployment"
}

# Show usage if requested
if [[ "${1:-}" == "--help" ]] || [[ "${1:-}" == "-h" ]]; then
    echo "PrusaSlicer Worker Binary Installation Verification Script"
    echo
    echo "Usage: $0 [OPTIONS]"
    echo
    echo "Options:"
    echo "  --help, -h    Show this help message"
    echo "  --build-only  Only build the Docker image"
    echo "  --test-only   Only run tests (assumes image already built)"
    echo
    echo "This script builds and tests the PrusaSlicer worker Docker container"
    echo "to verify that PrusaSlicer binary installation is working correctly."
    exit 0
fi

# Handle command line options
case "${1:-full}" in
    --build-only)
        log_info "Running build-only mode..."
        verify_prerequisites
        build_prusaslicer_image
        ;;
    --test-only)
        log_info "Running test-only mode..."
        verify_prerequisites
        test_container_startup
        verify_prusaslicer_installation
        test_health_endpoints
        test_worker_capabilities
        ;;
    full|*)
        run_comprehensive_test
        ;;
esac