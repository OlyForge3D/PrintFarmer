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
    --architecture ARCH     Deployment architecture (monolithic|microservices|host-network)
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

    # Generate for host network mode without any slicer workers
    $0 --architecture host-network --enable-orca-worker no
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

    local db_template_file="$TEMPLATES_DIR/docker-compose.databases.yml"
    if [[ ! -f "$db_template_file" ]]; then
        log_error "Database templates file not found: $db_template_file"
        return 1
    fi

    log_info "Using $provider database configuration"

    # Extract the provider service block from the databases template and rename the service to 'database'
    # The databases file uses services: with two-space indented service names
    awk -v prov="$provider" '
    /^services:/ { in_services=1; next }
    in_services && $0 ~ ("^  " prov ":") { printing=1; print; next }
    printing && $0 ~ /^  [a-zA-Z]/ { exit }
    printing { print }
    ' "$db_template_file" | sed -E "s/^  ${provider}:/  database:/"
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
    DB_PROVIDER="${DB_PROVIDER:-postgres}"
    KEEP_GENERATED="true"
    DRY_RUN="false"

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --architecture)
                ARCHITECTURE="$2"; shift 2 ;;
            --output-dir)
                OUTPUT_DIR="$2"; shift 2 ;;
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

# Function to merge addon services into the main compose file
merge_addon_services() {
    local compose_file="$1"
    local addon_type="$2"
    local addon_template="$TEMPLATES_DIR/docker-compose.$addon_type.yml"

    if [[ "$addon_type" == "monitoring" && "$SUPPORTS_ELASTIC_STACK" != "true" ]]; then
        local lite_template="$TEMPLATES_DIR/docker-compose.monitoring.lite.yml"
        if [[ -f "$lite_template" ]]; then
            local reason_message="${ELASTIC_STACK_REASON:-disabled}"
            if [[ "$reason_message" == "not supported on architecture $SYSTEM_ARCH" ]]; then
                log_warning "Elastic Stack is not supported on architecture $SYSTEM_ARCH; using lightweight monitoring template"
            else
                log_info "Elastic Stack ${reason_message}; using lightweight monitoring template (set ENABLE_ELASTIC_STACK=true to enable Elasticsearch/Kibana/Logstash)"
            fi
            addon_template="$lite_template"
        else
            log_warning "Elastic Stack is not supported on architecture $SYSTEM_ARCH and no lightweight monitoring template found; skipping monitoring services"
            return 0
        fi
    fi
    
    if [[ -f "$addon_template" ]]; then
        # If ruamel.yaml based merge helper exists, use it for robust YAML-aware merging
        if command -v python3 >/dev/null 2>&1 && [[ -f "$SCRIPT_DIR/compose-merge.py" ]] && python3 -c "import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('ruamel') else 1)" >/dev/null 2>&1; then
            # Use YAML-aware merge helper (ruamel.yaml must be available)
            temp_combined=$(mktemp)
            python3 "$SCRIPT_DIR/compose-merge.py" "$compose_file" "$addon_template" > "$temp_combined"
            mv "$temp_combined" "$compose_file"
        else
            # Fallback to the original (conservative) merging approach
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

    # Merge provider top-level maps (volumes/networks) from databases template so volumes like postgres-data exist
    merge_provider_maps() {
        local provider="${DB_PROVIDER:-postgres}"
        local db_template="$TEMPLATES_DIR/docker-compose.databases.yml"
        if [[ ! -f "$db_template" ]]; then
            return 0
        fi

        # Extract top-level volumes and networks sections from databases file using Python (more portable)
        local temp_db_maps temp_merged_maps temp_vols temp_nets
        temp_db_maps="$(mktemp)"
        temp_merged_maps="$(mktemp)"
        temp_vols="$(mktemp)"
        temp_nets="$(mktemp)"

        # Extract volumes and networks separately from the db template
        # (use Python for portability and robust matching)
        python3 - <<PY > "$temp_db_maps"
import re
path = "${db_template}"
txt = open(path,'r').read()
out_vol = ''
out_net = ''
mv = re.search(r'(^volumes:\n(?:^[ \t].*\n)*)', txt, re.M)
if mv:
    out_vol = mv.group(1)
mn = re.search(r'(^networks:\n(?:^[ \t].*\n)*)', txt, re.M)
if mn:
    out_net = mn.group(1)
print(out_vol + ('\n' + out_net if out_net else ''))
PY

        # split the captured maps into separate temp files for safe insertion
        # extract just the contents under the header (no 'volumes:' line)
        if grep -q '^volumes:' "$temp_db_maps"; then
            grep '^volumes:' -A9999 "$temp_db_maps" | sed '1d' > "$temp_vols" || true
        else
            : > "$temp_vols"
        fi
        if grep -q '^networks:' "$temp_db_maps"; then
            grep '^networks:' -A9999 "$temp_db_maps" | sed '1d' > "$temp_nets" || true
        else
            : > "$temp_nets"
        fi

        # If compose-merge.py exists and ruamel is available, use YAML-aware merge
        if command -v python3 >/dev/null 2>&1 && [[ -f "$SCRIPT_DIR/compose-merge.py" ]] && python3 -c "import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('ruamel') else 1)" >/dev/null 2>&1; then
            # create a minimal addon file that only contains the temp maps for ruamel
            temp_minimal_addon=$(mktemp)
            echo "" > "$temp_minimal_addon"
            if [[ -s "$temp_vols" ]]; then
                echo "volumes:" >> "$temp_minimal_addon"
                cat "$temp_vols" >> "$temp_minimal_addon"
            fi
            if [[ -s "$temp_nets" ]]; then
                echo "" >> "$temp_minimal_addon"
                echo "networks:" >> "$temp_minimal_addon"
                cat "$temp_nets" >> "$temp_minimal_addon"
            fi
            python3 "$SCRIPT_DIR/compose-merge.py" "$compose_file" "$temp_minimal_addon" > "$temp_merged_maps"
            mv "$temp_merged_maps" "$compose_file"
            rm -f "$temp_minimal_addon"
        else
            # Fallback: append volumes and networks contents conservatively
            if [[ -s "$temp_vols" ]]; then
                if grep -q '^volumes:' "$compose_file"; then
                    awk -v addon="$temp_vols" '/^volumes:/ { print; while ((getline line < addon) > 0) print line; next } { print }' "$compose_file" > "$temp_merged_maps" && mv "$temp_merged_maps" "$compose_file"
                else
                    echo "" >> "$compose_file"
                    echo "volumes:" >> "$compose_file"
                    cat "$temp_vols" >> "$compose_file" || true
                fi
            fi
            if [[ -s "$temp_nets" ]]; then
                if grep -q '^networks:' "$compose_file"; then
                    awk -v addon="$temp_nets" '/^networks:/ { print; while ((getline line < addon) > 0) print line; next } { print }' "$compose_file" > "$temp_merged_maps" && mv "$temp_merged_maps" "$compose_file"
                else
                    echo "" >> "$compose_file"
                    echo "networks:" >> "$compose_file"
                    cat "$temp_nets" >> "$compose_file" || true
                fi
            fi
        fi

    rm -f "$temp_db_maps" "$temp_merged_maps" "$temp_vols" "$temp_nets"
    }

    merge_provider_maps
    
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
    if [[ -d "$DOCKERFILES_DIR" ]]; then
        mkdir -p "$output_dir/dockerfiles"
        cp -r "$DOCKERFILES_DIR"/* "$output_dir/dockerfiles/" 2>/dev/null || true
    else
        log_warning "No dockerfiles directory found at $DOCKERFILES_DIR; skipping copy"
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
    parse_args "$@"
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
    
    if [[ "$KEEP_GENERATED" == "true" ]]; then
        log_info "Generated files retained; use --cleanup-generated to remove them automatically after deployment"
    else
        log_info "Generated files marked for cleanup after deployment"
    fi
}

# Run main function
main "$@"