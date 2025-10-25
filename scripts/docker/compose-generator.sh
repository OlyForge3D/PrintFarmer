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
    --enable-orca-worker VAL    Enable OrcaSlicer workers (yes/no/true/false or count, default: yes)

    --db-provider PROVIDER  Database provider (postgres|sqlserver|mysql, default: postgres)
    --keep-generated        Don't clean up generated files after deployment
    --dry-run              Show what would be generated without creating files
    --help                 Show this help message

EXAMPLES:
    # Generate microservices configuration with OrcaSlicer workers only
    $0 --architecture microservices --enable-orca-worker yes

    # Generate with monitoring and telemetry
    $0 --architecture microservices --include-monitoring --include-telemetry

    # Generate for host network mode without any slicer workers
    $0 --architecture host-network --enable-orca-worker no

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
ENABLE_ORCA_WORKER=""
DB_PROVIDER=""

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
        --enable-orca-worker)
            ENABLE_ORCA_WORKER="$2"
            shift 2
            ;;
        --db-provider)
            DB_PROVIDER="$2"
            shift 2
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
    
    # Determine if workers are needed based on configuration or environment
    local need_orca_worker="${ENABLE_ORCA_WORKER:-${ORCA_WORKER_COUNT:-yes}}"
    
    # Parse yes/no and numeric values
    if [[ "$need_orca_worker" =~ ^(yes|true|1)$ ]] || [[ "$need_orca_worker" =~ ^[0-9]+$ && "$need_orca_worker" -gt 0 ]]; then
        need_orca_worker="true"
    else
        need_orca_worker="false"
    fi
    
    case "$arch" in
        "monolithic"|"microservices"|"host-network")
            # All architectures now use multi-stage builds for efficiency
            log_info "Using multi-stage Dockerfile for $arch architecture"
            cp "$DOCKERFILES_DIR/Dockerfile.multistage" "$output_dir/"
            
            if [[ "$need_orca_worker" == "true" ]]; then
                log_info "Including OrcaSlicer worker Dockerfiles (workers enabled)"
                cp "$DOCKERFILES_DIR/Dockerfile.orcaslicer" "$output_dir/"
                cp "$DOCKERFILES_DIR/Dockerfile.slicer-base" "$output_dir/"
            fi
            ;;
    esac
}

# Function to generate database service configuration based on provider
generate_database_config() {
    local provider="${DB_PROVIDER:-postgres}"
    local database_templates_dir="$DOCKER_DIR/database-templates"
    local temp_file
    
    # Normalize provider name
    case "$provider" in
        "postgres"|"postgresql")
            provider="postgres"
            ;;
        "sqlserver"|"mssql"|"sql-server")
            provider="sqlserver"
            ;;
        "mysql"|"mariadb")
            provider="mysql"
            ;;
        *)
            log_warning "Unknown database provider '$provider', defaulting to postgres"
            provider="postgres"
            ;;
    esac
    
    local template_file="$database_templates_dir/$provider.yml"
    
    if [[ ! -f "$template_file" ]]; then
        log_error "Database template not found: $template_file"
        return 1
    fi
    
    log_info "Using $provider database configuration"
    cat "$template_file"
}

# Function to merge addon services into the main compose file
merge_addon_services() {
    local compose_file="$1"
    local addon_type="$2"
    local addon_template="$TEMPLATES_DIR/docker-compose.$addon_type.yml"
    
    if [[ ! -f "$addon_template" ]]; then
        log_error "Addon template not found: $addon_template"
        return 1
    fi
    
    # Create temporary files for merging
    local temp_merged temp_addon_services temp_addon_volumes temp_addon_networks
    temp_merged="$(mktemp)"
    temp_addon_services="$(mktemp)"
    temp_addon_volumes="$(mktemp)"
    temp_addon_networks="$(mktemp)"
    
    # Extract services from addon template (excluding comments and metadata)
    awk '
    BEGIN { in_services=0 }
    /^services:/ { in_services=1; next }
    /^[a-zA-Z][^:]*:/ && !/^  / { in_services=0 }
    in_services { print }
    ' "$addon_template" > "$temp_addon_services"
    
    # Extract volumes from addon template
    awk '
    BEGIN { in_volumes=0 }
    /^volumes:/ { in_volumes=1; next }
    /^[a-zA-Z][^:]*:/ && !/^  / { in_volumes=0 }
    in_volumes { print }
    ' "$addon_template" > "$temp_addon_volumes"
    
    # Extract networks from addon template
    awk '
    BEGIN { in_networks=0 }
    /^networks:/ { in_networks=1; next }
    /^[a-zA-Z][^:]*:/ && !/^  / { in_networks=0 }
    in_networks { print }
    ' "$addon_template" > "$temp_addon_networks"
    
    # Merge services into main compose file
    if [[ -s "$temp_addon_services" ]]; then
        # Find the line after the last service definition
        local last_service_line
        last_service_line=$(grep -n '^  [a-zA-Z]' "$compose_file" | tail -1 | cut -d: -f1 | tr -d ' ')
        
        if [[ -n "$last_service_line" && "$last_service_line" -gt 0 ]]; then
            # Simpler approach: just append services at the end before volumes/networks
            local volumes_line
            volumes_line=$(grep -n '^volumes:' "$compose_file" | head -1 | cut -d: -f1 2>/dev/null || echo "")
            
            if [[ -n "$volumes_line" && "$volumes_line" -gt 0 ]]; then
                # Insert before volumes section
                head -n "$((volumes_line - 1))" "$compose_file" > "$temp_merged"
                echo "" >> "$temp_merged"  # Add blank line
                cat "$temp_addon_services" >> "$temp_merged"
                echo "" >> "$temp_merged"  # Add blank line
                tail -n +"$volumes_line" "$compose_file" >> "$temp_merged"
                mv "$temp_merged" "$compose_file"
            else
                # Append at the end
                echo "" >> "$compose_file"
                cat "$temp_addon_services" >> "$compose_file"
            fi
        fi
    fi
    
    # Merge volumes section
    if [[ -s "$temp_addon_volumes" ]]; then
        if grep -q '^volumes:' "$compose_file"; then
            # Append to existing volumes section
            awk -v addon_volumes="$temp_addon_volumes" '
            /^volumes:/ { print; while ((getline line < addon_volumes) > 0) print line; close(addon_volumes); next }
            { print }
            ' "$compose_file" > "$temp_merged"
            mv "$temp_merged" "$compose_file"
        else
            # Add volumes section at the end
            echo "" >> "$compose_file"
            echo "volumes:" >> "$compose_file"
            cat "$temp_addon_volumes" >> "$compose_file"
        fi
    fi
    
    # Merge networks section
    if [[ -s "$temp_addon_networks" ]]; then
        if grep -q '^networks:' "$compose_file"; then
            # Append to existing networks section
            awk -v addon_networks="$temp_addon_networks" '
            /^networks:/ { print; while ((getline line < addon_networks) > 0) print line; close(addon_networks); next }
            { print }
            ' "$compose_file" > "$temp_merged"
            mv "$temp_merged" "$compose_file"
        else
            # Add networks section at the end
            echo "" >> "$compose_file"
            echo "networks:" >> "$compose_file"
            cat "$temp_addon_networks" >> "$compose_file"
        fi
    fi
    
    # Clean up temporary files
    rm -f "$temp_merged" "$temp_addon_services" "$temp_addon_volumes" "$temp_addon_networks"
    
    return 0
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
    
    # Copy base template and replace database configuration
    if ! cp "$base_template" "$compose_file"; then
        log_error "Failed to copy base template"
        return 1
    fi
    
    # Replace the database service with provider-specific configuration
    # Check if architecture needs a database service (skip monolithic as it uses SQLite)
    if [[ "$arch" == "microservices" || "$arch" == "host-network" ]]; then
        # Generate provider-specific database config
        local db_config
        if ! db_config="$(generate_database_config)"; then
            log_error "Failed to generate database configuration"
            return 1
        fi
        
        # Create temporary files
        local temp_before temp_after temp_new_compose
        temp_before="$(mktemp)"
        temp_after="$(mktemp)"
        temp_new_compose="$(mktemp)"
        
        # Split the compose file: everything before database service, and everything after
        awk '/^  database:/{exit} {print}' "$compose_file" > "$temp_before"
        
        # Find everything after the database service (skip until next service or volumes/networks)
        awk '
        BEGIN { found_db=0; skip=0 }
        /^  database:/ { found_db=1; skip=1; next }
        found_db && skip && /^  [a-zA-Z]/ { skip=0 }
        found_db && skip && /^(volumes|networks|version):/ { skip=0 }
        found_db && !skip { print }
        ' "$compose_file" > "$temp_after"
        
        # Combine: before + new database config + after
        cat "$temp_before" > "$temp_new_compose"
        echo "$db_config" >> "$temp_new_compose"
        cat "$temp_after" >> "$temp_new_compose"
        
        # Replace the original file
        if ! mv "$temp_new_compose" "$compose_file"; then
            log_error "Failed to update compose file with new database configuration"
            rm -f "$temp_before" "$temp_after" "$temp_new_compose"
            return 1
        fi
        
        # Clean up temporary files
        rm -f "$temp_before" "$temp_after"
        
        log_info "Replaced database service with ${DB_PROVIDER:-postgres} configuration"
    fi
    
    # Merge addon services into the compose file
    local addons_merged=false
    
    if [[ "$INCLUDE_MONITORING" == "true" ]]; then
        if merge_addon_services "$compose_file" "monitoring"; then
            log_info "Merged monitoring stack services"
            addons_merged=true
        else
            log_warning "Failed to merge monitoring services, continuing without them"
        fi
    fi
    
    if [[ "$INCLUDE_TELEMETRY" == "true" ]]; then
        if merge_addon_services "$compose_file" "telemetry"; then
            log_info "Merged telemetry stack services"
            addons_merged=true
        else
            log_warning "Failed to merge telemetry services, continuing without them"
        fi
    fi
    
    if [[ "$INCLUDE_SECURITY" == "true" ]]; then
        if merge_addon_services "$compose_file" "security"; then
            log_info "Merged security stack services"
            addons_merged=true
        else
            log_warning "Failed to merge security services, continuing without them"
        fi
    fi
    
    if [[ "$INCLUDE_REGISTRY" == "true" ]]; then
        if merge_addon_services "$compose_file" "registry"; then
            log_info "Merged registry stack services"
            addons_merged=true
        else
            log_warning "Failed to merge registry services, continuing without them"
        fi
    fi
    
    if [[ "$addons_merged" == "true" ]]; then
        log_info "Successfully merged addon services into compose file"
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
        # Copy Prometheus configuration
        if [[ -f "$CONFIGS_DIR/prometheus.yml" ]]; then
            mkdir -p "$output_dir/monitoring/prometheus"
            cp "$CONFIGS_DIR/prometheus.yml" "$output_dir/monitoring/prometheus/"
        fi
        
        # Copy Grafana configurations
        if [[ -d "$REPO_ROOT/grafana" ]]; then
            mkdir -p "$output_dir/monitoring/grafana"
            cp -r "$REPO_ROOT/grafana/"* "$output_dir/monitoring/grafana/"
        fi
        
        log_info "Copied monitoring stack configurations"
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
    # Determine worker configuration for dry run display
    local need_orca_worker="${ENABLE_ORCA_WORKER:-${ORCA_WORKER_COUNT:-yes}}"
    # Parse yes/no and numeric values
    if [[ "$need_orca_worker" =~ ^(yes|true|1)$ ]] || [[ "$need_orca_worker" =~ ^[0-9]+$ && "$need_orca_worker" -gt 0 ]]; then
        need_orca_worker="true"
    else
        need_orca_worker="false"
    fi

    case "$arch" in
        "monolithic"|"microservices"|"host-network")
            echo "  - Dockerfile.multistage (efficient multi-stage build for all services)"
            if [[ "$need_orca_worker" == "true" ]]; then
                echo "    • Includes OrcaSlicer worker build target"
            else
                echo "    • Slicer workers disabled"  
            fi
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
    
    if [[ "$DRY_RUN" == "true" ]]; then
        show_dry_run "$ARCHITECTURE"
        return 0
    fi
    
    # Create output directory if it doesn't exist
    mkdir -p "$OUTPUT_DIR"
    
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