#!/bin/bash

# compose-generator.sh - Generate deployment-specific docker-compose.yml files
# This script combines compose templates based on configuration options
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$SCRIPT_DIR"
TEMPLATES_DIR="$DOCKER_DIR/compose-templates"
DOCKERFILES_DIR="$DOCKER_DIR/dockerfiles"
CONFIGS_DIR="$DOCKER_DIR/configs"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
SYSTEM_ARCH="${TARGET_ARCH:-$(uname -m)}"

# Source container versions from single source of truth
VERSIONS_FILE="$DOCKER_DIR/container-versions.conf"
if [[ -f "$VERSIONS_FILE" ]]; then
    source "$VERSIONS_FILE"
    # Export all sourced variables so envsubst can use them
    export SDK_TAG ASPNET_TAG NODE_TAG NGINX_TAG UBUNTU_TAG ORCASLICER_VERSION BUILD_VERBOSITY
fi

# Ensure required compose templates exist
required_templates=(
    "$TEMPLATES_DIR/docker-compose.yml"
    "$TEMPLATES_DIR/docker-compose.common.yml"
)
for tf in "${required_templates[@]}"; do
    if [[ ! -f "$tf" ]]; then
        log_error "Required template missing: $tf"
        log_error "Please restore the missing template under scripts/docker/compose-templates/"
        exit 2
    fi
done

# Elastic Stack capabilities (disabled by default)
SUPPORTS_ELASTIC_STACK=false
ELASTIC_STACK_REASON="disabled by default"

if [[ -z "${ENABLE_ELASTIC_STACK:-}" ]]; then
    case "$SYSTEM_ARCH" in
        arm*|aarch64)
            ELASTIC_STACK_REASON="not supported on architecture $SYSTEM_ARCH"
            ;;
        *)
            ELASTIC_STACK_REASON="disabled by default"
            ;;
    esac
else
    _elastic_stack_lower=$(printf '%s' "$ENABLE_ELASTIC_STACK" | tr '[:upper:]' '[:lower:]')
    case "$_elastic_stack_lower" in
        true|yes|1)
            SUPPORTS_ELASTIC_STACK=true
            ELASTIC_STACK_REASON=""
            ;;
        false|no|0)
            ELASTIC_STACK_REASON="explicitly disabled via ENABLE_ELASTIC_STACK=${ENABLE_ELASTIC_STACK}"
            ;;
        *)
            ELASTIC_STACK_REASON="disabled (unrecognized ENABLE_ELASTIC_STACK value '${ENABLE_ELASTIC_STACK}')"
            ;;
    esac
    unset _elastic_stack_lower
fi

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

# Get the host IP for Docker extra_hosts configuration
get_host_ip() {
    # Try to get primary host IP address
    # First try hostname -I (Linux)
    if command -v hostname &>/dev/null; then
        local ip=$(hostname -I 2>/dev/null | awk '{print $1}' || echo "")
        if [[ -n "$ip" ]]; then
            echo "$ip"
            return 0
        fi
    fi
    # Fallback to ifconfig (macOS)
    if command -v ifconfig &>/dev/null; then
        local ip=$(ifconfig 2>/dev/null | grep -E "inet " | grep -v "127.0.0.1" | head -1 | awk '{print $2}' || echo "")
        if [[ -n "$ip" ]]; then
            echo "$ip"
            return 0
        fi
    fi
    # Last resort: use localhost for loopback
    echo "127.0.0.1"
}

HOST_IP="${HOST_IP:-$(get_host_ip)}"

show_usage() {
    cat << EOF
Usage: $0 [OPTIONS]

Generate deployment-specific Docker Compose configuration and copy required files.

OPTIONS:
    --output-dir DIR        Output directory (default: repository root)
    --include-monitoring    Include monitoring stack
    --include-telemetry     Include telemetry/observability
    --include-security      Include security configurations  
    --include-registry      Include local registry
    --include-discovery     Include printer discovery service
    --enable-orca-worker VAL    Enable OrcaSlicer workers (yes/no/true/false or count, default: yes)
    --enable-pgadmin            Enable pgAdmin web UI (PostgreSQL only)

    --db-provider PROVIDER  Database provider (postgres|sqlserver, default: postgres)
    --cleanup-generated     Remove generated files after deployment (default keeps them)
    --keep-generated        Preserve generated files (default; retained for compatibility)
    --dry-run              Show what would be generated without creating files
    --help                 Show this help message

EXAMPLES:
    # Generate with OrcaSlicer workers
    $0 --enable-orca-worker yes

    # Generate with monitoring and telemetry
    $0 --include-monitoring --include-telemetry
EOF

}


generate_database_config() {
    # Determine provider from environment or default
    local provider_raw="${DB_PROVIDER:-postgres}"
    local provider="$(printf '%s' "$provider_raw" | tr '[:upper:]' '[:lower:]')"

    case "$provider" in
        "postgres"|"postgresql")
            provider="postgres"
            ;;
        "sqlserver"|"mssql"|"sql-server")
            provider="sqlserver"
            ;;

        *)
            log_warning "Unknown database provider '$provider', defaulting to postgres"
            provider="postgres"
            ;;
    esac

    log_info "Using $provider database configuration"

    # Use dedicated database provider template file instead of extraction
    # This eliminates parsing complexity and the previous duplicate volumes bug
    # Each provider has its own clean template file with only that provider's service configuration
    local db_template_file="$TEMPLATES_DIR/docker-compose.database.${provider}.yml"
    
    if [[ ! -f "$db_template_file" ]]; then
        log_error "Database template file not found: $db_template_file"
        log_error "Expected: $db_template_file"
        return 1
    fi

    # Wrap the template content in "database:" key for proper YAML structure
    # The template contains only the service configuration (indented content)
    # We need to wrap it in a "database:" key so it can be properly merged
    # Skip comment lines (lines starting with #) to keep YAML clean
    echo "database:"
    grep -v '^\s*#' "$db_template_file" | sed 's/^/  /'
}

# Parse CLI arguments and set defaults
parse_args() {
    OUTPUT_DIR=""
    # Monitoring and telemetry enabled by default for production observability
    INCLUDE_MONITORING="true"
    INCLUDE_TELEMETRY="true"
    INCLUDE_SECURITY="false"
    INCLUDE_REGISTRY="false"
    INCLUDE_DISCOVERY="false"
    ENABLE_ORCA_WORKER=""
    ENABLE_PGADMIN="false"
    API_PORT=""
    DB_PROVIDER="${DB_PROVIDER:-postgres}"
    KEEP_GENERATED="true"
    DRY_RUN="false"

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --architecture)
                # Accepted for backwards compatibility, ignored
                shift 2 ;;
            --output-dir)
                OUTPUT_DIR="$2"; shift 2 ;;
            --api-port)
                API_PORT="$2"; shift 2 ;;
            # Legacy include flags (monitoring/telemetry now on by default)
            --include-monitoring)
                INCLUDE_MONITORING="true"; shift ;;
            --include-telemetry)
                INCLUDE_TELEMETRY="true"; shift ;;
            # Exclude flags to opt-out of default observability stack
            --exclude-monitoring)
                INCLUDE_MONITORING="false"; shift ;;
            --exclude-telemetry)
                INCLUDE_TELEMETRY="false"; shift ;;
            --include-security)
                INCLUDE_SECURITY="true"; shift ;;
            --include-registry)
                INCLUDE_REGISTRY="true"; shift ;;
            --include-discovery)
                INCLUDE_DISCOVERY="true"; shift ;;
            --enable-orca-worker)
                ENABLE_ORCA_WORKER="$2"; shift 2 ;;
            --enable-pgadmin)
                ENABLE_PGADMIN="true"; shift ;;
            --db-provider)
                DB_PROVIDER="$2"; shift 2 ;;
            --cleanup-generated)
                KEEP_GENERATED="false"; shift ;;
            --keep-generated)
                KEEP_GENERATED="true"; shift ;;
            --dry-run)
                DRY_RUN="true"; shift ;;
            --help|-h)
                show_usage; exit 0 ;;
            *)
                # Ignore unknown args for forward-compat
                shift ;;
        esac
    done

    OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT}"
    ENABLE_ORCA_WORKER="${ENABLE_ORCA_WORKER:-${ORCA_WORKER_COUNT:-yes}}"
}

# Function to validate port numbers
validate_port() {
    local port="$1"
    local port_name="${2:-port}"
    
    # Check if empty
    if [[ -z "$port" ]]; then
        return 0  # Empty is OK (will use default)
    fi
    
    # Check if it's a valid number
    if ! [[ "$port" =~ ^[0-9]+$ ]]; then
        log_error "Invalid $port_name: '$port' is not a valid number"
        return 1
    fi
    
    # Check if port is in valid range (1-65535)
    if [[ $port -lt 1 || $port -gt 65535 ]]; then
        log_error "Invalid $port_name: $port is out of valid range (1-65535)"
        return 1
    fi
    
    return 0
}

# Function to inject YAML anchors from common compose file into generated file
inject_health_check_anchors() {
    local compose_file="$1"
    local common_file="$TEMPLATES_DIR/docker-compose.common.yml"
    
    if [[ ! -f "$common_file" ]]; then
        log_warning "Common compose file not found: $common_file (health check anchors will use inline definitions)"
        return 0
    fi
    
    log_info "Extracting and injecting health check anchors from docker-compose.common.yml"
    
    # Extract full anchor definitions with their content (can be multi-line) using Python
    local temp_injected=$(mktemp)
    python3 - "$common_file" "$compose_file" "$temp_injected" <<'PY'
import sys
import re

common_file = sys.argv[1]
compose_file = sys.argv[2]
out_file = sys.argv[3]

# Read anchor definitions from common file
with open(common_file, 'r') as f:
    common_lines = f.readlines()

# Extract all x-* anchor definitions
anchors = []
i = 0
while i < len(common_lines):
    line = common_lines[i]
    # Check if this is an anchor definition (starts with x-name: &name)
    if re.match(r'^x-[\w-]+: &[\w-]+', line):
        anchor_block = [line]
        i += 1
        # Capture the entire anchor definition (indented content)
        while i < len(common_lines):
            next_line = common_lines[i]
            # Stop if we hit a non-indented line that's not empty/comment
            if next_line.strip() and not next_line[0].isspace() and not next_line.startswith('#'):
                break
            anchor_block.append(next_line)
            i += 1
        anchors.append(''.join(anchor_block))
    else:
        i += 1

if not anchors:
    print(f"ERROR: No anchors found in {common_file}", file=sys.stderr)
    sys.exit(1)

# Read compose file
with open(compose_file, 'r') as f:
    compose_lines = f.readlines()

# Build output: insert anchors before 'services:'
output = []
inserted = False

for line in compose_lines:
    # Skip any existing x-* anchor definitions (they'll be replaced)
    if line.startswith('x-'):
        continue
    
    if not inserted and line.strip().startswith('services:'):
        # Insert anchors before services
        output.append('\n')
        for anchor in anchors:
            output.append(anchor)
        output.append('\n')
        inserted = True
    
    output.append(line)

with open(out_file, 'w') as f:
    f.writelines(output)
PY

    local py_exit=$?
    if [[ $py_exit -ne 0 ]]; then
        log_error "Failed to extract/inject health check anchors from common file"
        rm -f "$temp_injected"
        return 1
    fi

    # Replace original compose file with injected version
    if [[ -s "$temp_injected" ]]; then
        mv "$temp_injected" "$compose_file"
        log_info "Successfully injected health check anchors from docker-compose.common.yml"
    else
        log_error "Failed to inject anchors - generated file is empty"
        rm -f "$temp_injected"
        return 1
    fi
    
    return 0
}

# Function to merge addon services into the main compose file
merge_addon_services() {
    local compose_file="$1"
    local addon_type="$2"
    local addon_template="$TEMPLATES_DIR/docker-compose.$addon_type.yml"

    if [[ "$addon_type" == "monitoring" ]]; then
        # If user explicitly disabled elastic stack, prefer the lite template when available
        if [[ -n "${ENABLE_ELASTIC_STACK:-}" ]]; then
            _elastic_lower=$(printf '%s' "$ENABLE_ELASTIC_STACK" | tr '[:upper:]' '[:lower:]')
            if [[ "$_elastic_lower" == "false" || "$_elastic_lower" == "0" || "$_elastic_lower" == "no" ]]; then
                local lite_template="$TEMPLATES_DIR/docker-compose.monitoring.lite.yml"
                if [[ -f "$lite_template" ]]; then
                    log_info "ENABLE_ELASTIC_STACK explicitly set to false; using lightweight monitoring template"
                    addon_template="$lite_template"
                else
                    log_warning "ENABLE_ELASTIC_STACK is false but no lite template found; proceeding with full monitoring template if available"
                fi
            fi
            unset _elastic_lower
        else
            # No explicit user override: use full monitoring template when requested
            :
        fi
    fi
    
    if [[ -f "$addon_template" ]]; then
        # Initialize temp files at function level so cleanup works regardless of code path
        local temp_merged temp_addon_services temp_addon_volumes temp_addon_networks temp_combined
        
        # If ruamel.yaml based merge helper exists, use it for robust YAML-aware merging
        if command -v python3 >/dev/null 2>&1 && [[ -f "$SCRIPT_DIR/compose-merge.py" ]] && python3 -c "import ruamel.yaml" >/dev/null 2>&1; then
            # Use YAML-aware merge helper (ruamel.yaml must be available)
            temp_combined=$(mktemp)
            python3 "$SCRIPT_DIR/compose-merge.py" "$compose_file" "$addon_template" > "$temp_combined"
            mv "$temp_combined" "$compose_file"
        else
            # Fallback to the original (conservative) merging approach
            # Create temporary files for merging
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

            # Simplified safe append of services (no dedupe)
            if [[ -s "$temp_addon_services" ]]; then
                # Filter out any addon services that already exist in the base compose
                temp_filtered_services="$(mktemp)"
                python3 - "$temp_addon_services" "$compose_file" "$temp_filtered_services" <<'PY'
import sys,re
addon_file=sys.argv[1]; base_file=sys.argv[2]; out_file=sys.argv[3]
addon_lines=open(addon_file,'r').read().splitlines()
base_txt=open(base_file,'r').read()

# Parse addon services into blocks (each starts with two-space indent and name)
blocks=[]
cur=None
for line in addon_lines:
    m=re.match(r'^\s{2}([A-Za-z0-9_.-]+):\s*$', line)
    if m:
        if cur:
            blocks.append(cur)
        cur=[line]
    elif cur is not None:
        cur.append(line)
if cur:
    blocks.append(cur)

kept=[]
for b in blocks:
    m=re.match(r'^\s{2}([A-Za-z0-9_.-]+):', b[0])
    if not m:
        continue
    name=m.group(1)
    # look for the service name in the base compose (under services:)
    if re.search(r'^\s{2}'+re.escape(name)+r':\s*$', base_txt, flags=re.M):
        # service already present in base - skip to avoid duplicate mapping key
        continue
    kept.extend(b)

with open(out_file,'w') as f:
    if kept:
        f.write('\n'.join(kept))
PY
                # Insert services before the first root-level section (networks/volumes)
                local volumes_line networks_line insertion_line file_length
                volumes_line=$(grep -n '^volumes:' "$compose_file" | head -1 | cut -d: -f1 2>/dev/null || echo "")
                networks_line=$(grep -n '^networks:' "$compose_file" | head -1 | cut -d: -f1 2>/dev/null || echo "")

                insertion_line=""
                if [[ -n "$networks_line" && "$networks_line" -gt 0 ]]; then
                    insertion_line="$networks_line"
                fi
                if [[ -n "$volumes_line" && "$volumes_line" -gt 0 ]]; then
                    if [[ -z "$insertion_line" || "$volumes_line" -lt "$insertion_line" ]]; then
                        insertion_line="$volumes_line"
                    fi
                fi
                if [[ -z "$insertion_line" ]]; then
                    file_length=$(wc -l < "$compose_file")
                    insertion_line=$((file_length + 1))
                fi

                if [[ "$insertion_line" -gt 1 ]]; then
                    head -n "$((insertion_line - 1))" "$compose_file" > "$temp_merged"
                    echo "" >> "$temp_merged"
                    cat "$temp_filtered_services" >> "$temp_merged"
                    echo "" >> "$temp_merged"
                    tail -n +"$insertion_line" "$compose_file" >> "$temp_merged"
                    mv "$temp_merged" "$compose_file"
                else
                    # Fallback: prepend to file
                    cat "$temp_filtered_services" "$compose_file" > "$temp_merged"
                    mv "$temp_merged" "$compose_file"
                fi
            fi
            rm -f "$temp_filtered_services"

            # Merge volumes section
            awk '
            BEGIN { in_volumes=0 }
            /^volumes:/ { in_volumes=1; next }
            /^[a-zA-Z][^:]*:/ && !/^  / { in_volumes=0 }
            in_volumes { print }
            ' "$addon_template" > "$temp_addon_volumes"

            if [[ -s "$temp_addon_volumes" ]]; then
                if grep -q '^volumes:' "$compose_file"; then
                    awk -v addon_volumes="$temp_addon_volumes" '
                    /^volumes:/ { print; while ((getline line < addon_volumes) > 0) print line; close(addon_volumes); next }
                    { print }
                    ' "$compose_file" > "$temp_merged"
                    mv "$temp_merged" "$compose_file"
                else
                    echo "" >> "$compose_file"
                    echo "volumes:" >> "$compose_file"
                    cat "$temp_addon_volumes" >> "$compose_file"
                fi
            fi

            # Merge networks section
            awk '
            BEGIN { in_networks=0 }
            /^networks:/ { in_networks=1; next }
            /^[a-zA-Z][^:]*:/ && !/^  / { in_networks=0 }
            in_networks { print }
            ' "$addon_template" > "$temp_addon_networks"

            if [[ -s "$temp_addon_networks" ]]; then
                if grep -q '^networks:' "$compose_file"; then
                    awk -v addon_networks="$temp_addon_networks" '
                    /^networks:/ { print; while ((getline line < addon_networks) > 0) print line; close(addon_networks); next }
                    { print }
                    ' "$compose_file" > "$temp_merged"
                    mv "$temp_merged" "$compose_file"
                else
                    echo "" >> "$compose_file"
                    echo "networks:" >> "$compose_file"
                    cat "$temp_addon_networks" >> "$compose_file"
                fi
            fi

            # Clean up temporary files (only created in fallback path)
            rm -f "$temp_merged" "$temp_addon_services" "$temp_addon_volumes" "$temp_addon_networks"
        fi
    else
        log_error "Addon template not found: $addon_template"
        return 1
    fi

    return 0
}

# Function to generate docker-compose.yml from templates and configuration
generate_compose() {
    local output_dir="$1"
    local compose_file="$output_dir/docker-compose.yml"
    
    log_info "Generating docker-compose.yml..."
    
    local base_template="$TEMPLATES_DIR/docker-compose.yml"
    
    if [[ ! -f "$base_template" ]]; then
        log_error "Base template not found: $base_template"
        return 1
    fi
    
    # Copy base template and replace database configuration
    if ! cp "$base_template" "$compose_file"; then
        log_error "Failed to copy base template"
        return 1
    fi
    
    # Populate container version variables using envsubst
    # This ensures the single source of truth (container-versions.conf) is used
    if command -v envsubst >/dev/null 2>&1; then
        log_info "Populating container image versions from container-versions.conf..."
        envsubst < "$compose_file" > "${compose_file}.tmp" && mv "${compose_file}.tmp" "$compose_file"
    fi
    
    # Inject health check anchors from common compose file
    if ! inject_health_check_anchors "$compose_file"; then
        log_error "Failed to inject health check anchors"
        return 1
    fi
    
    # Replace the database service with provider-specific configuration
    # Generate provider-specific database config
    local db_config
    if ! db_config="$(generate_database_config)"; then
        log_error "Failed to generate database configuration"
        return 1
    fi
    
    # CRITICAL: Check for required dependencies BEFORE attempting any replacements
    # Python3 is required to properly handle YAML structure and indentation
    if ! command -v python3 >/dev/null 2>&1; then
        log_error "FATAL: python3 is required for database service configuration"
        log_error "       Please install Python 3 to continue"
        log_error "       Installation: apt-get install python3 (Debian/Ubuntu) or equivalent"
        return 1
    fi
    
    # CRITICAL: ruamel.yaml is required for proper YAML handling
    # Check if the Python module is available
    if ! python3 -c "from ruamel.yaml import YAML" 2>/dev/null; then
        log_error "FATAL: Python module 'ruamel.yaml' is not installed"
        log_error "       This module is REQUIRED for proper Docker Compose YAML generation"
        log_error "       Installation: pip install ruamel.yaml"
        log_error "       Or for system-wide: apt-get install python3-ruamel.yaml (Debian/Ubuntu)"
        return 1
    fi
    
    # CRITICAL: Verify the Python replacement script exists
    if [[ ! -f "$SCRIPT_DIR/compose-replace-db.py" ]]; then
        log_error "FATAL: Python script not found: $SCRIPT_DIR/compose-replace-db.py"
        log_error "       This script is required for database service configuration"
        return 1
    fi
    
    # Now perform the Python-based YAML replacement
    # There is NO FALLBACK - if this fails, we fail loudly so users know there's a problem
    local temp_replaced py_error
    temp_replaced="$(mktemp)"
    py_error="$(mktemp)"
    
    if ! python3 "$SCRIPT_DIR/compose-replace-db.py" "$compose_file" "$db_config" > "$temp_replaced" 2>"$py_error"; then
        log_error "FATAL: Failed to generate database configuration"
        log_error "       Error details:"
        cat "$py_error" | sed 's/^/         /' >&2
        rm -f "$temp_replaced" "$py_error"
        return 1
    fi
    
    # Verify Python produced valid output
    if [[ ! -s "$temp_replaced" ]]; then
        log_error "FATAL: Python script produced empty output"
        log_error "       This indicates a problem with the YAML generation"
        rm -f "$temp_replaced" "$py_error"
        return 1
    fi
    
    # Replace the original compose file
    if ! mv "$temp_replaced" "$compose_file"; then
        log_error "FATAL: Failed to update compose file with generated configuration"
        rm -f "$temp_replaced" "$py_error"
        return 1
    fi
    
    rm -f "$py_error"
    log_info "Replaced database service with ${DB_PROVIDER:-postgres} configuration"

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
    
    if [[ "$INCLUDE_DISCOVERY" == "true" ]]; then
        if merge_addon_services "$compose_file" "discovery"; then
            log_info "Merged printer discovery service"
            addons_merged=true
        else
            log_warning "Failed to merge discovery service, continuing without it"
        fi
    fi
    
    # Conditionally merge orcaslicer-worker addon if enabled
    local need_orca_worker="${ENABLE_ORCA_WORKER:-${ORCA_WORKER_COUNT:-yes}}"
    # Parse yes/no and numeric values
    if [[ "$need_orca_worker" =~ ^(yes|true|1)$ ]] || [[ "$need_orca_worker" =~ ^[0-9]+$ && "$need_orca_worker" -gt 0 ]]; then
        if merge_addon_services "$compose_file" "orcaslicer-worker"; then
            log_info "Merged OrcaSlicer worker service (ENABLE_ORCA_WORKER=$ENABLE_ORCA_WORKER)"
            addons_merged=true
        else
            log_warning "Failed to merge OrcaSlicer worker service, continuing without it"
        fi

        # Slicer-host always accompanies orca-worker (orchestrator for distributed slicing)
        if merge_addon_services "$compose_file" "slicer-host"; then
            log_info "Merged slicer-host service (distributed slicing orchestrator)"
            addons_merged=true
            # Switch nginx to the split-mode config that routes /api/slicer to slicer-host
            sed -i 's|/deploy/nginx/${NGINX_CONFIG:-nginx-proxy.conf}|/deploy/nginx/nginx-proxy-split.conf|' "$compose_file"
            log_info "Switched nginx config to nginx-proxy-split.conf for slicer routing"
        else
            log_warning "Failed to merge slicer-host service, continuing without it"
        fi
    else
        log_info "OrcaSlicer worker service disabled (ENABLE_ORCA_WORKER=$ENABLE_ORCA_WORKER)"
    fi
    
    # Conditionally merge pgAdmin addon if enabled and using PostgreSQL
    if [[ "$ENABLE_PGADMIN" == "true" ]]; then
        local db_provider_lower=$(printf '%s' "$DB_PROVIDER" | tr '[:upper:]' '[:lower:]')
        if [[ "$db_provider_lower" == "postgres" || "$db_provider_lower" == "postgresql" ]]; then
            if merge_addon_services "$compose_file" "pgadmin"; then
                log_info "Merged pgAdmin service (ENABLE_PGADMIN=$ENABLE_PGADMIN)"
                addons_merged=true
            else
                log_warning "Failed to merge pgAdmin service, continuing without it"
            fi
        else
            log_warning "pgAdmin is only supported with PostgreSQL database (current: $DB_PROVIDER, skipping)"
        fi
    fi
    
    if [[ "$addons_merged" == "true" ]]; then
        log_info "Successfully merged addon services into compose file"
    fi

    # Validate the generated compose file when Docker Compose is available
    local compose_validate_cmd=""
    if docker compose version >/dev/null 2>&1; then
        compose_validate_cmd="docker compose"
    elif command -v docker-compose >/dev/null 2>&1; then
        compose_validate_cmd="docker-compose"
    fi

    if [[ -n "$compose_validate_cmd" ]]; then
        # If dedupe helper exists, run generated compose through it before validation
        if [[ -x "$SCRIPT_DIR/compose-dedupe.sh" ]]; then
            log_info "Post-processing generated compose file through compose-dedupe.sh"
            tmp_dedup=$(mktemp)
            if ! "$SCRIPT_DIR/compose-dedupe.sh" < "$compose_file" > "$tmp_dedup"; then
                log_warning "compose-dedupe.sh failed; continuing with original compose file"
                rm -f "$tmp_dedup"
            else
                mv "$tmp_dedup" "$compose_file"
            fi
        fi

        local validation_output=""
        if ! validation_output=$($compose_validate_cmd -f "$compose_file" config --quiet 2>&1); then
            log_warning "Generated compose file failed validation via '$compose_validate_cmd config':"
            [[ -n "$validation_output" ]] && printf '%s\n' "$validation_output" >&2
            log_warning "Continuing despite validation failure"
        fi
    else
        log_info "Docker Compose validation skipped (command not available)"
    fi

    # Remove frontend ports to avoid conflicts with nginx-proxy.
    # nginx-proxy is the only service that should bind host ports.
    # API stays on bridge network for service discovery by hostname.
    log_info "Applying microservices adjustments: removing frontend host ports (keep bridge network)"

    python3 - "$compose_file" "$HOST_IP" <<'PY'
import sys
path = sys.argv[1]
host_ip = sys.argv[2]
txt = open(path,'r').read().splitlines()

def find_block(lines, name):
    # returns (start_index, end_index) of block starting with '  name:' (inclusive)
    start = None
    for i, line in enumerate(lines):
        if line.startswith('  ' + name + ':'):
            start = i
            break
    if start is None:
        return None, None
    # scan until next top-level service (two-space indent + word + ':') or EOF
    end = len(lines)
    for j in range(start+1, len(lines)):
        if lines[j].startswith('  ') and not lines[j].startswith('    '):
            end = j
            break
    return start, end

def remove_ports(block_lines):
    out = []
    skip = False
    for l in block_lines:
        if l.lstrip().startswith('ports:') and l.startswith('    '):
            skip = True
            continue
        if skip:
            # continue skipping indented port entries
            if l.startswith('      -') or l.startswith('      "') or l.startswith('      '):
                continue
            else:
                skip = False
        out.append(l)
    return out

# Remove frontend ports to avoid conflicts with nginx-proxy.
# In microservices mode, nginx-proxy is the only service that should bind the host port.
f_start, f_end = find_block(txt, 'frontend')
if f_start is not None:
    frontend_block = txt[f_start:f_end]
    new_frontend_block = remove_ports(frontend_block)
    txt = txt[:f_start] + new_frontend_block + txt[f_end:]

start, end = find_block(txt, 'nginx-proxy')
if start is not None:
    block = txt[start:end]
    # check if extra_hosts exists
    if not any('extra_hosts:' in l for l in block):
        # try to insert before volumes/ports/environment if present
        inserted = False
        for idx in range(1, len(block)):
            if block[idx].lstrip().startswith('volumes:') or block[idx].lstrip().startswith('ports:') or block[idx].lstrip().startswith('environment:'):
                block.insert(idx, '    extra_hosts:')
                block.insert(idx+1, f'      - "host.docker.internal:{host_ip}"')
                inserted = True
                break
        if not inserted:
            # append at end of block (before next service)
            block.append('    extra_hosts:')
            block.append(f'      - "host.docker.internal:{host_ip}"')
    txt = txt[:start] + block + txt[end:]

open(path,'w').write('\n'.join(txt) + '\n')
PY
    
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

# Copy dockerfiles needed for builds into the output directory (tolerant noop if not present)
copy_dockerfiles() {
    local output_dir="$1"

    log_info "Copying Dockerfiles into $output_dir"
    # Prefer dockerfiles under scripts/docker/dockerfiles, but fall back to repository-level dockerfiles/
    local src_dir=""
    if [[ -d "$DOCKERFILES_DIR" ]]; then
        src_dir="$DOCKERFILES_DIR"
    elif [[ -d "$REPO_ROOT/dockerfiles" ]]; then
        src_dir="$REPO_ROOT/dockerfiles"
    fi

    if [[ -n "$src_dir" && -d "$src_dir" ]]; then
        mkdir -p "$output_dir/dockerfiles"
        cp -r "$src_dir"/* "$output_dir/dockerfiles/" 2>/dev/null || true
        # Ensure Dockerfile.multistage is also available at the output root (some tests expect this)
        if [[ -f "$src_dir/Dockerfile.multistage" ]]; then
            cp "$src_dir/Dockerfile.multistage" "$output_dir/Dockerfile.multistage" 2>/dev/null || true
        fi
    else
        log_warning "No dockerfiles directory found at $DOCKERFILES_DIR or $REPO_ROOT/dockerfiles; skipping copy"
    fi
}

# Function to show what would be generated (dry run)
show_dry_run() {
    log_info "DRY RUN: Would generate the following:"
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

    echo "  - Dockerfile.multistage (efficient multi-stage build for all services)"
    if [[ "$need_orca_worker" == "true" ]]; then
        echo "    • Includes OrcaSlicer worker build target"
    else
        echo "    • Slicer workers disabled"  
    fi
    
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
    local dry_run_orca="${ENABLE_ORCA_WORKER:-${ORCA_WORKER_COUNT:-yes}}"
    if [[ "$dry_run_orca" =~ ^(yes|true|1)$ ]] || [[ "$dry_run_orca" =~ ^[0-9]+$ && "$dry_run_orca" -gt 0 ]]; then
        echo "  - Includes slicer-host service (distributed slicing orchestrator)"
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
    parse_args "$@"
    log_info "Docker Compose Generator for PrintFarmer"
    log_info "Output directory: $OUTPUT_DIR"
    
    # Validate database provider - SQLite not supported for Docker deployments
    # Docker deployments require a separate database container for production reliability
    case "$DB_PROVIDER" in
        postgres|sqlserver)
            : # Valid provider for Docker deployments
            ;;

        sqlite)
            log_error "SQLite is not supported for Docker deployments"
            log_error "Docker deployments require a separate database container for:"
            log_error "  - Data persistence across container rebuilds"
            log_error "  - Independent backup/restore capabilities"
            log_error "  - Production-ready concurrent access"
            log_error ""
            log_error "Use --db-provider postgres (default) or --db-provider sqlserver"
            log_error "For local development without Docker, SQLite is still available"
            return 1
            ;;
        *)
            log_error "Invalid database provider: $DB_PROVIDER"
            log_error "Valid options: postgres (default), sqlserver"
            return 1
            ;;
    esac
    
    # Validate port numbers
    if ! validate_port "$API_PORT" "API port"; then
        return 1
    fi
    
    if [[ "$DRY_RUN" == "true" ]]; then
        show_dry_run
        return 0
    fi
    
    # Create output directory if it doesn't exist
    mkdir -p "$OUTPUT_DIR"
    
    # Generate the configuration
    copy_dockerfiles "$OUTPUT_DIR"
    generate_compose "$OUTPUT_DIR"
    copy_configs "$OUTPUT_DIR"
    
    log_success "Successfully generated Docker configuration"
    log_info "Files generated in: $OUTPUT_DIR"
    
    if [[ "$KEEP_GENERATED" == "true" ]]; then
        log_info "Generated files retained; use --cleanup-generated to remove them automatically after deployment"
    else
        log_info "Generated files marked for cleanup after deployment"
    fi
}

# Run main function
main "$@"