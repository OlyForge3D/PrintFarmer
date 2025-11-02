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
SYSTEM_ARCH="${TARGET_ARCH:-$(uname -m)}"

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

show_usage() {
    cat << EOF
Usage: $0 [OPTIONS]

Generate deployment-specific Docker Compose configuration and copy required files.

OPTIONS:
    --architecture ARCH     Deployment architecture (monolithic|microservices)
    --output-dir DIR        Output directory (default: repository root)
    --include-monitoring    Include monitoring stack
    --include-telemetry     Include telemetry/observability
    --include-security      Include security configurations  
    --include-registry      Include local registry
    --enable-orca-worker VAL    Enable OrcaSlicer workers (yes/no/true/false or count, default: yes)

    --db-provider PROVIDER  Database provider (postgres|sqlserver|mysql, default: postgres)
    --cleanup-generated     Remove generated files after deployment (default keeps them)
    --keep-generated        Preserve generated files (default; retained for compatibility)
    --dry-run              Show what would be generated without creating files
    --help                 Show this help message

EXAMPLES:
    # Generate microservices configuration with OrcaSlicer workers only
    $0 --architecture microservices --enable-orca-worker yes

    # Generate with monitoring and telemetry
    $0 --architecture microservices --include-monitoring --include-telemetry
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
        "mysql"|"mariadb")
            provider="mysql"
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
    ARCHITECTURE=""
    OUTPUT_DIR=""
    INCLUDE_MONITORING="false"
    INCLUDE_TELEMETRY="false"
    INCLUDE_SECURITY="false"
    INCLUDE_REGISTRY="false"
    ENABLE_ORCA_WORKER=""
    API_PORT=""
    DB_PROVIDER="${DB_PROVIDER:-postgres}"
    KEEP_GENERATED="true"
    DRY_RUN="false"

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --architecture)
                ARCHITECTURE="$2"; shift 2 ;;
            --output-dir)
                OUTPUT_DIR="$2"; shift 2 ;;
            --api-port)
                API_PORT="$2"; shift 2 ;;
            --include-monitoring)
                INCLUDE_MONITORING="true"; shift ;;
            --include-telemetry)
                INCLUDE_TELEMETRY="true"; shift ;;
            --include-security)
                INCLUDE_SECURITY="true"; shift ;;
            --include-registry)
                INCLUDE_REGISTRY="true"; shift ;;
            --enable-orca-worker)
                ENABLE_ORCA_WORKER="$2"; shift 2 ;;
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

    ARCHITECTURE="${ARCHITECTURE:-microservices}"
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

            rm -f "$temp_merged" "$temp_addon_services" "$temp_addon_volumes" "$temp_addon_networks"
        fi
    else
        log_error "Addon template not found: $addon_template"
        return 1
    fi

    # Clean up temporary files
    rm -f "$temp_merged" "$temp_addon_services" "$temp_addon_volumes" "$temp_addon_networks"

    return 0
}

# Function to generate docker-compose.yml based on architecture and options
generate_compose() {
    local arch="$1"
    local output_dir="$2"
    # Always output to docker-compose.yml regardless of architecture
    # The generator customizes the template for the specific architecture
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
        *)
            log_error "Unsupported architecture: $arch"
            log_error "Valid options: monolithic, microservices"
            return 1
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
    if [[ "$arch" == "microservices" ]]; then
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

    # When generating microservices deployment, allow API to run in host network mode
    # while keeping other services on the bridge network. Use a small Python snippet
    # to perform the rewriting in a robust and portable way (avoids awk dialect issues).
    if [[ "$arch" == "microservices" ]]; then
        log_info "Applying microservices adjustments: API -> host network, frontend on bridge network"

    python3 - "$compose_file" <<'PY'
import sys
path = sys.argv[1]
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

start, end = find_block(txt, 'api')
if start is not None:
    block = txt[start:end]
    # remove ports entries under api
    new_block = remove_ports(block)
    
    # When using network_mode: "host", networks: section must be removed
    # (Docker doesn't allow both - they're mutually exclusive)
    # Use a cleaner approach: remove the entire networks: section and its contents
    filtered = []
    i = 0
    while i < len(new_block):
        line = new_block[i]
        # Check if this line defines "networks:" as a key (must be followed by : with optional value)
        stripped = line.lstrip()
        if stripped.startswith('networks:') and not stripped.startswith('#'):
            # Skip this line and all following indented lines that belong to networks
            i += 1
            while i < len(new_block):
                next_line = new_block[i]
                next_stripped = next_line.lstrip()
                # Stop skipping when we hit an empty line or a line at same/lower indentation that's a new key
                if not next_stripped:
                    i += 1
                    continue
                # If line starts with spaces (indented under networks:), skip it
                if next_line.startswith('    ') and next_stripped and not next_stripped[0].isalpha():
                    i += 1
                    continue
                # If it's a new key at the same level, stop skipping
                if next_stripped[0].isalpha() and ':' in next_line:
                    break
                i += 1
            continue
        filtered.append(line)
        i += 1
    new_block = filtered
    
    # ensure network_mode: "host" exists under api
    if not any(l.strip().startswith('network_mode:') for l in new_block[1:3]):
        # insert after header line
        new_block.insert(1, '    network_mode: "host"')
    # replace
    txt = txt[:start] + new_block + txt[end:]

    # Also remove any stray literal port mapping for API that may remain (guard against template artifacts)
    # e.g. lines like: - "${API_PORT:-5245}:5245"
    start2, end2 = find_block(txt, 'api')
    if start2 is not None:
        cleaned = []
        for l in txt[start2:end2]:
            if l.strip() == '- "${API_PORT:-5245}:5245"' or l.strip() == "- \"${API_PORT:-5245}:5245\"":
                # skip stray port mapping
                continue
            cleaned.append(l)
        txt = txt[:start2] + cleaned + txt[end2:]

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
                block.insert(idx+1, '      - "host.docker.internal:host-gateway"')
                inserted = True
                break
        if not inserted:
            # append at end of block (before next service)
            block.append('    extra_hosts:')
            block.append('      - "host.docker.internal:host-gateway"')
    txt = txt[:start] + block + txt[end:]

open(path,'w').write('\n'.join(txt) + '\n')
PY
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

# Copy dockerfiles needed for builds into the output directory (tolerant noop if not present)
copy_dockerfiles() {
    local arch="$1"
    local output_dir="$2"

    log_info "Copying Dockerfiles for $arch into $output_dir"
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
        "monolithic"|"microservices")
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
    parse_args "$@"
    log_info "Docker Compose Generator for PrintFarmer"
    log_info "Architecture: $ARCHITECTURE"
    log_info "Output directory: $OUTPUT_DIR"
    
    # Validate architecture
    case "$ARCHITECTURE" in
        monolithic|microservices)
            : # Valid architecture
            ;;
        *)
            log_error "Invalid architecture: $ARCHITECTURE"
            log_error "Valid options: monolithic, microservices"
            return 1
            ;;
    esac
    
    # Validate database provider
    case "$DB_PROVIDER" in
        postgres|sqlserver|mysql)
            : # Valid provider
            ;;
        *)
            log_error "Invalid database provider: $DB_PROVIDER"
            log_error "Valid options: postgres, sqlserver, mysql"
            return 1
            ;;
    esac
    
    # Validate port numbers
    if ! validate_port "$API_PORT" "API port"; then
        return 1
    fi
    
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
    
    if [[ "$KEEP_GENERATED" == "true" ]]; then
        log_info "Generated files retained; use --cleanup-generated to remove them automatically after deployment"
    else
        log_info "Generated files marked for cleanup after deployment"
    fi
}

# Run main function
main "$@"