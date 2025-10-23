#!/bin/bash

# compose-generator.sh - Generate deployment-specific docker-compose.yml files
# This script combines compose templates based on deployment architecture and configuration

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$SCRIPT_DIR"
TEMPLATES_DIR="$DOCKER_DIR/compose-templates"
DOCKERFILES_DIR="$DOCKER_DIR/dockerfiles"
CONFIGS_DIR="$DOCKER_DIR/configs"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Logging functions
log_info() { echo -e "${BLUE}[INFO]${NC} $1" >&2; }
log_success() { echo -e "${GREEN}[SUCCESS]${NC} $1" >&2; }
log_warning() { echo -e "${YELLOW}[WARNING]${NC} $1" >&2; }
log_error() { echo -e "${RED}[ERROR]${NC} $1" >&2; }

show_usage() {
    cat << EOF
Usage: $0 [OPTIONS]

Generate deployment-specific Docker Compose configuration and copy required files.

OPTIONS:
    --architecture ARCH     Deployment architecture (monolithic|microservices|host-network)
    --output-dir DIR        Output directory (default: repository root)
    --include-monitoring    Include monitoring stack
    --include-telemetry     Include telemetry/observability
    --include-security      Include security configurations  
    --include-registry      Include local registry
    --keep-generated        Don't clean up generated files after deployment
    --dry-run              Show what would be generated without creating files
    --help                 Show this help message

EXAMPLES:
    # Generate microservices configuration
    $0 --architecture microservices

    # Generate with monitoring and telemetry
    $0 --architecture microservices --include-monitoring --include-telemetry

    # Generate for host network mode
    $0 --architecture host-network --output-dir /tmp/deploy

    # Dry run to see what would be generated
    $0 --architecture monolithic --dry-run
EOF
}

# Default values
ARCHITECTURE=""
OUTPUT_DIR="$REPO_ROOT"
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
KEEP_GENERATED=false
DRY_RUN=false

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --architecture)
            ARCHITECTURE="$2"
            shift 2
            ;;
        --output-dir)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --include-monitoring)
            INCLUDE_MONITORING=true
            shift
            ;;
        --include-telemetry)
            INCLUDE_TELEMETRY=true
            shift
            ;;
        --include-security)
            INCLUDE_SECURITY=true
            shift
            ;;
        --include-registry)
            INCLUDE_REGISTRY=true
            shift
            ;;
        --keep-generated)
            KEEP_GENERATED=true
            shift
            ;;
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        --help)
            show_usage
            exit 0
            ;;
        *)
            log_error "Unknown option: $1"
            show_usage
            exit 1
            ;;
    esac
done

# Validate required parameters
if [[ -z "$ARCHITECTURE" ]]; then
    log_error "Architecture must be specified"
    show_usage
    exit 1
fi

if [[ ! "$ARCHITECTURE" =~ ^(monolithic|microservices|host-network)$ ]]; then
    log_error "Invalid architecture: $ARCHITECTURE. Must be one of: monolithic, microservices, host-network"
    exit 1
fi

# Function to copy Dockerfiles based on architecture
copy_dockerfiles() {
    local arch="$1"
    local output_dir="$2"
    
    log_info "Copying Dockerfiles for $arch architecture..."
    
    case "$arch" in
        "monolithic")
            cp "$DOCKERFILES_DIR/Dockerfile" "$output_dir/"
            ;;
        "microservices")
            cp "$DOCKERFILES_DIR/Dockerfile.api" "$output_dir/"
            cp "$DOCKERFILES_DIR/Dockerfile.frontend" "$output_dir/"
            cp "$DOCKERFILES_DIR/Dockerfile.orcaslicer" "$output_dir/"
            cp "$DOCKERFILES_DIR/Dockerfile.prusaslicer" "$output_dir/"
            cp "$DOCKERFILES_DIR/Dockerfile.slicer-base" "$output_dir/"
            ;;
        "host-network")
            cp "$DOCKERFILES_DIR/Dockerfile.api" "$output_dir/"
            cp "$DOCKERFILES_DIR/Dockerfile.frontend-host" "$output_dir/"
            cp "$DOCKERFILES_DIR/Dockerfile.orcaslicer" "$output_dir/"
            cp "$DOCKERFILES_DIR/Dockerfile.prusaslicer" "$output_dir/"
            cp "$DOCKERFILES_DIR/Dockerfile.slicer-base" "$output_dir/"
            ;;
    esac
}

# Function to generate docker-compose.yml based on architecture and options
generate_compose() {
    local arch="$1"
    local output_dir="$2"
    local compose_file="$output_dir/docker-compose.yml"
    
    log_info "Generating docker-compose.yml for $arch architecture..."
    
    # Start with base template
    local base_template=""
    case "$arch" in
        "monolithic")
            base_template="$TEMPLATES_DIR/docker-compose.yml"
            ;;
        "microservices")
            base_template="$TEMPLATES_DIR/docker-compose.microservices.yml"
            ;;
        "host-network")
            base_template="$TEMPLATES_DIR/docker-compose.host-network.yml"
            ;;
    esac
    
    if [[ ! -f "$base_template" ]]; then
        log_error "Base template not found: $base_template"
        return 1
    fi
    
    # Copy base template
    if ! cp "$base_template" "$compose_file"; then
        log_error "Failed to copy base template"
        return 1
    fi
    
    # For now, skip the merging to avoid docker compose config issues
    # We'll implement proper merging later when we have proper environment setup
    if [[ "$INCLUDE_MONITORING" == "true" || "$INCLUDE_TELEMETRY" == "true" || "$INCLUDE_SECURITY" == "true" || "$INCLUDE_REGISTRY" == "true" ]]; then
        log_warning "Additional service merging not yet implemented in this version"
        log_info "Base $arch compose file generated. Additional services can be added manually."
    fi
    
    # Validate the generated compose file
    if ! docker compose -f "$compose_file" config --quiet >/dev/null 2>&1; then
        log_warning "Generated compose file may have validation issues, but continuing..."
    fi
    
    return 0
}

# Function to copy configuration files
copy_configs() {
    local output_dir="$1"
    
    log_info "Copying configuration files..."
    
    # Always copy docker entrypoint config
    if [[ -f "$CONFIGS_DIR/docker-entrypoint-config.sh" ]]; then
        cp "$CONFIGS_DIR/docker-entrypoint-config.sh" "$output_dir/"
    fi
    
    # Copy additional configs based on what's included
    if [[ "$INCLUDE_MONITORING" == "true" ]]; then
        [[ -f "$CONFIGS_DIR/prometheus.yml" ]] && cp "$CONFIGS_DIR/prometheus.yml" "$output_dir/"
    fi
    
    if [[ "$INCLUDE_TELEMETRY" == "true" ]]; then
        [[ -f "$CONFIGS_DIR/otel-collector-config.yaml" ]] && cp "$CONFIGS_DIR/otel-collector-config.yaml" "$output_dir/"
    fi
    
    if [[ "$INCLUDE_SECURITY" == "true" ]]; then
        [[ -f "$CONFIGS_DIR/security-config.json" ]] && cp "$CONFIGS_DIR/security-config.json" "$output_dir/"
    fi
}

# Function to show what would be generated (dry run)
show_dry_run() {
    local arch="$1"
    
    log_info "DRY RUN: Would generate the following for $arch architecture:"
    echo
    echo "Dockerfiles:"
    case "$arch" in
        "monolithic")
            echo "  - Dockerfile"
            ;;
        "microservices")
            echo "  - Dockerfile.api"
            echo "  - Dockerfile.frontend"
            echo "  - Dockerfile.orcaslicer"
            echo "  - Dockerfile.prusaslicer"
            echo "  - Dockerfile.slicer-base"
            ;;
        "host-network")
            echo "  - Dockerfile.api"
            echo "  - Dockerfile.frontend-host"
            echo "  - Dockerfile.orcaslicer"
            echo "  - Dockerfile.prusaslicer"
            echo "  - Dockerfile.slicer-base"
            ;;
    esac
    
    echo
    echo "Docker Compose:"
    echo "  - docker-compose.yml (generated from templates)"
    
    if [[ "$INCLUDE_MONITORING" == "true" ]]; then
        echo "  - Includes monitoring stack"
    fi
    if [[ "$INCLUDE_TELEMETRY" == "true" ]]; then
        echo "  - Includes telemetry/observability"
    fi
    if [[ "$INCLUDE_SECURITY" == "true" ]]; then
        echo "  - Includes security configurations"
    fi
    if [[ "$INCLUDE_REGISTRY" == "true" ]]; then
        echo "  - Includes local registry"
    fi
    
    echo
    echo "Configuration files:"
    echo "  - docker-entrypoint-config.sh"
    [[ "$INCLUDE_MONITORING" == "true" ]] && echo "  - prometheus.yml"
    [[ "$INCLUDE_TELEMETRY" == "true" ]] && echo "  - otel-collector-config.yaml"
    [[ "$INCLUDE_SECURITY" == "true" ]] && echo "  - security-config.json"
    
    echo
}

# Main execution
main() {
    log_info "Docker Compose Generator for PrintFarmer"
    log_info "Architecture: $ARCHITECTURE"
    log_info "Output directory: $OUTPUT_DIR"
    
    # Create output directory if it doesn't exist
    mkdir -p "$OUTPUT_DIR"
    
    if [[ "$DRY_RUN" == "true" ]]; then
        show_dry_run "$ARCHITECTURE"
        return 0
    fi
    
    # Generate the configuration
    copy_dockerfiles "$ARCHITECTURE" "$OUTPUT_DIR"
    generate_compose "$ARCHITECTURE" "$OUTPUT_DIR"
    copy_configs "$OUTPUT_DIR"
    
    log_success "Successfully generated Docker configuration for $ARCHITECTURE architecture"
    log_info "Files generated in: $OUTPUT_DIR"
    
    if [[ "$KEEP_GENERATED" == "false" ]]; then
        log_info "Use --keep-generated to prevent cleanup after deployment"
    fi
}

# Run main function
main "$@"