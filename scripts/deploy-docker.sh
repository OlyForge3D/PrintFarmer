#!/bin/bash

# PrintFarmer Docker Deployment Script
# Automated setup for Docker-based deployment with user-friendly prompts
#
# FEATURES:
#   - Interactive or non-interactive deployment modes
#   - Multi-architecture support (monolithic, microservices)
#   - Optional monitoring and telemetry stacks
#   - Automatic initial admin user setup (skips setup wizard)
#   - Database provider selection
#   - Dry-run validation
#
# AUTO-ADMIN SETUP:
#   The --auto-admin flag enables automatic creation of the initial administrator account,
#   which skips the setup wizard UI. This is useful for:
#   - CI/CD pipelines that need fully automated deployments
#   - Infrastructure-as-Code (IaC) scenarios
#   - Containerized deployments that bypass manual setup
#
#   METHODS:
#   1. Command-line flags: ./scripts/deploy-docker.sh --auto-admin --auto-admin-password=SecurePass123!
#   2. Config file: Create ~/.auto-admin-config, ~/.config/printfarmer/auto-admin-config, or ./.auto-admin-config
#      with AUTO_ADMIN=true, AUTO_ADMIN_USERNAME=admin, AUTO_ADMIN_PASSWORD=secret, AUTO_ADMIN_EMAIL=admin@example.com
#      Script auto-detects and loads these files (same pattern as start-all-local.sh)
#   3. Environment variables: export AUTO_ADMIN=true && ./scripts/deploy-docker.sh
#
#   If no password is provided, one is automatically generated and displayed.
#   Config file method keeps credentials separate from main .deploy-config file.

set -euo pipefail

# Pre-process args to support verify-only mode with optional env/config file overrides.
# Accept both '--flag value' and '--flag=value' forms.
# Source shared utilities
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
source "$SCRIPT_DIR/docker-utils.sh"
source "$SCRIPT_DIR/common-utils.sh"

# Default flags
DRY_RUN=false
NON_INTERACTIVE=false
TEAR_DOWN=false
SHOW_HELP=false
REDEPLOY=false
PREPULL=false
AUTO_ADMIN=false
AUTO_ADMIN_USERNAME=""
AUTO_ADMIN_PASSWORD=""
AUTO_ADMIN_EMAIL=""
# Build verbosity: quiet (default), minimal, normal, detailed
BUILD_VERBOSITY="${BUILD_VERBOSITY:-quiet}"
# Compose up option to pass --remove-orphans (default true)
COMPOSE_REMOVE_ORPHANS=${COMPOSE_REMOVE_ORPHANS:-true}

# Generated files are retained by default; allow env override for CI
KEEP_GENERATED=${KEEP_GENERATED:-true}

# Auto-admin config file (separate from main .deploy-config to keep credentials isolated)
# Uses same search pattern as start-all-local.sh for consistency
AUTO_ADMIN_CONFIG_FILE=""

# Auto-detect auto-admin config file if not provided via --auto-admin-config
auto_detect_admin_config() {
    # Check for config in common locations (in order of priority)
    for default_location in ~/.auto-admin-config ~/.config/printfarmer/auto-admin-config ./.auto-admin-config; do
        if [ -f "$default_location" ]; then
            AUTO_ADMIN_CONFIG_FILE="$default_location"
            break
        fi
    done
}

# Load auto-admin config file if found
load_auto_admin_config() {
    if [ -n "$AUTO_ADMIN_CONFIG_FILE" ] && [ -f "$AUTO_ADMIN_CONFIG_FILE" ]; then
        print_info "Loading auto-admin config from $AUTO_ADMIN_CONFIG_FILE"
        # shellcheck disable=SC1090
        source "$AUTO_ADMIN_CONFIG_FILE"
        print_success "Auto-admin config loaded"
        return 0
    fi
    return 1
}

# Offline deployment flags
PREPARE_OFFLINE=false
DEPLOY_OFFLINE=false
PULL_IMAGES=false
SAVE_IMAGES=false
LOAD_IMAGES=false
CACHE_ORCASLICER=false
LOAD_CACHED_ORCASLICER=false
IMAGES_DIR="./docker-images"

# Base container images for offline deployment (standard upstream images)
# These are pulled as fallback if upgraded images aren't available
DOCKER_BASE_IMAGES=(
    "mcr.microsoft.com/dotnet/sdk:9.0"
    "mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim"
    "ubuntu:24.04"
    "node:22-alpine"
    "postgres:16-alpine"
    "nginx:alpine"
    "docker/dockerfile:1"  # BuildKit Dockerfile frontend parser (required for # syntax=docker/dockerfile:1)
)

# Pre-upgraded base images with apt/apk updates and tools pre-installed
# These are built during --prepare-offline and preferred over standard images
DOCKER_UPGRADED_IMAGES=(
    "mcr.microsoft.com/dotnet/sdk:9.0-upgraded"
    "mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim-upgraded"
    "ubuntu:24.04-upgraded"
    "node:22-alpine-upgraded"
    "postgres:16-alpine-upgraded"
    "nginx:alpine-upgraded"
    "docker/dockerfile:1"  # No upgrade needed - just cache the original
)

# Locally-built images for offline deployment (built during --prepare-offline, not pulled from registry)
DOCKER_LOCAL_IMAGES=(
    "orcaslicer-binaries:2.3.1"
)

# ============================================================================
# OFFLINE DEPLOYMENT FUNCTIONS
# ============================================================================

# Test if a Docker image exists locally
test_image_exists() {
    local image_name="$1"
    if docker image inspect "$image_name" >/dev/null 2>&1; then
        return 0
    fi
    return 1
}

# Find cached Docker images in common locations
find_cached_images_dir() {
    local search_paths=(
        "./docker-images"
        "$PWD/docker-images"
        "$HOME/docker-images"
        "/mnt/usb/docker-images"
        "/media/*/docker-images"
    )
    
    for path in "${search_paths[@]}"; do
        # Handle glob patterns
        if [[ "$path" == *"*"* ]]; then
            for expanded_path in $path; do
                if [ -d "$expanded_path" ] && [ -n "$(find "$expanded_path" -maxdepth 1 -name "*.tar" 2>/dev/null)" ]; then
                    print_info "Found cached images at: $expanded_path" >&2
                    echo "$expanded_path"
                    return 0
                fi
            done
        else
            if [ -d "$path" ] && [ -n "$(find "$path" -maxdepth 1 -name "*.tar" 2>/dev/null)" ]; then
                print_info "Found cached images at: $path" >&2
                echo "$path"
                return 0
            fi
        fi
    done
    
    return 1
}

# Auto-load cached images if they exist and are not in Docker
# Core function to load Docker images from TAR files
# Usage: _load_tar_images <images_dir> [quiet]
# Returns: 0 on success, 1 on failure
# Arguments:
#   images_dir: Directory containing .tar files
#   quiet: If "quiet", minimal output (for auto-load scenarios)
_load_tar_images() {
    local images_dir="$1"
    local quiet="${2:-}"
    
    if [ ! -d "$images_dir" ]; then
        [ "$quiet" != "quiet" ] && print_error "Images directory not found: $images_dir"
        return 1
    fi
    
    local tar_files
    tar_files=$(find "$images_dir" -maxdepth 1 -name "*.tar" 2>/dev/null)
    
    if [ -z "$tar_files" ]; then
        [ "$quiet" != "quiet" ] && print_error "No TAR files found in $images_dir"
        return 1
    fi
    
    local tar_count
    tar_count=$(echo "$tar_files" | wc -l)
    print_info "Found $tar_count cached image TAR file(s). Loading from $images_dir..."
    
    local success_count=0
    local fail_count=0
    
    # Save tar_files to a temporary file to avoid here-string stdin issues with docker load
    local tar_list_file
    tar_list_file=$(mktemp)
    echo "$tar_files" > "$tar_list_file"
    
    while IFS= read -r tar_file; do
        [ -z "$tar_file" ] && continue
        local basename
        basename=$(basename "$tar_file")
        print_info "Loading $basename..."
        if docker load -i "$tar_file" > /dev/null 2>&1; then
            print_success "Loaded: $basename"
            ((++success_count)) || true  # Avoid set -e exit on ((0++))
        else
            print_warning "Failed to load $basename"
            ((++fail_count)) || true
        fi
    done < "$tar_list_file"
    rm -f "$tar_list_file"
    
    if [ $fail_count -gt 0 ]; then
        print_warning "Failed to load $fail_count images from cache"
        return 1
    fi
    
    print_success "Successfully loaded $success_count/$tar_count images"
    return 0
}

# Auto-load cached images if they exist and are not already in Docker
auto_load_cached_images() {
    local images_dir="${1:-.}"
    
    # If ImagesDir not specified, search for cached images automatically
    if [ -z "$images_dir" ] || [ "$images_dir" = "." ]; then
        print_info "Searching for cached Docker images..."
        images_dir=$(find_cached_images_dir) || {
            print_info "No cached images found in common locations"
            return 1
        }
    fi
    
    if [ ! -d "$images_dir" ]; then
        print_info "Images directory not found: $images_dir"
        return 1
    fi
    
    # Check if there are any TAR files
    local tar_files
    tar_files=$(find "$images_dir" -maxdepth 1 -name "*.tar" 2>/dev/null)
    if [ -z "$tar_files" ]; then
        print_info "No cached images found in $images_dir"
        return 1
    fi
    
    # Find images that need to be loaded
    local images_to_load=()
    for image in "${DOCKER_BASE_IMAGES[@]}"; do
        if ! test_image_exists "$image"; then
            images_to_load+=("$image")
        fi
    done
    
    if [ ${#images_to_load[@]} -eq 0 ]; then
        print_info "All required images are already in Docker"
        return 0
    fi
    
    # Use the core loading function
    _load_tar_images "$images_dir" "quiet"
    
    # After loading, check if orcaslicer-binaries was loaded and set ORCA_ASSET_IMAGE
    # This enables the build system to skip downloading OrcaSlicer from GitHub
    for local_image in "${DOCKER_LOCAL_IMAGES[@]}"; do
        if docker image inspect "$local_image" >/dev/null 2>&1; then
            if [[ "$local_image" == orcaslicer-binaries:* ]]; then
                export ORCA_ASSET_IMAGE="$local_image"
                print_info "Loaded prebuilt OrcaSlicer binaries: $local_image"
            fi
        fi
    done
}

# Find OrcaSlicer AppImage in common cache locations
find_cached_orcaslicer_dir() {
    local search_paths=(
        "./docker-images/orcaslicer"
        "$PWD/docker-images/orcaslicer"
        "$HOME/docker-images/orcaslicer"
        "/mnt/usb/docker-images/orcaslicer"
        "/media/*/docker-images/orcaslicer"
    )
    
    for path in "${search_paths[@]}"; do
        # Handle glob patterns
        if [[ "$path" == *"*"* ]]; then
            for expanded_path in $path; do
                if [ -d "$expanded_path" ] && [ -n "$(find "$expanded_path" -maxdepth 1 -name "*.AppImage" 2>/dev/null)" ]; then
                    echo "$expanded_path"
                    return 0
                fi
            done
        else
            if [ -d "$path" ] && [ -n "$(find "$path" -maxdepth 1 -name "*.AppImage" 2>/dev/null)" ]; then
                echo "$path"
                return 0
            fi
        fi
    done
    
    return 1
}

# Auto-load OrcaSlicer AppImage if found in cache
auto_load_orcaslicer() {
    local orca_dir="${1:-.}"
    
    # If OrcaDir not specified, search for it
    if [ -z "$orca_dir" ] || [ "$orca_dir" = "." ]; then
        orca_dir=$(find_cached_orcaslicer_dir) || {
            print_info "OrcaSlicer AppImage not found in any cache location"
            print_info "Will be downloaded during first Docker build if needed"
            return 0
        }
    fi
    
    if [ ! -d "$orca_dir" ]; then
        print_info "OrcaSlicer cache directory not found: $orca_dir"
        return 0
    fi
    
    local appimages
    appimages=$(find "$orca_dir" -maxdepth 1 -name "*.AppImage" 2>/dev/null)
    if [ -z "$appimages" ]; then
        print_info "No OrcaSlicer AppImage found in cache: $orca_dir"
        return 0
    fi
    
    # Set environment variable for Docker build context
    export ORCA_ASSET_PATH="$orca_dir"
    
    local count
    count=$(echo "$appimages" | wc -l)
    print_success "Found $count cached OrcaSlicer AppImage(s)"
    while IFS= read -r img; do
        local size
        size=$(($(stat -f%z "$img" 2>/dev/null || stat -c%s "$img" 2>/dev/null) / 1048576))
        print_info "  ✓ $(basename "$img") ($size MB)"
    done <<< "$appimages"
    
    print_info "OrcaSlicer cache location: $orca_dir"
    print_info "Automatically configured for deployment"
    
    return 0
}

 # Verify deployment
# Note: verify_deployment() is defined later in this script. The older/duplicate
# implementation that previously appeared here was removed to avoid function
# shadowing. Keep a single canonical implementation (the later one) so behavior
# is deterministic.

# Global guard: disable all slicer-related automatic builds when requested
if [ "${DISABLE_SLICER_BUILDS:-}" = "true" ] || [ "${DISABLE_SLICER_BUILDS:-}" = "1" ]; then
    print_warning "DISABLE_SLICER_BUILDS is set; automatic OrcaSlicer worker builds will be disabled."
    # Ensure variables exist so downstream logic respects the disable
    ENABLE_ORCA_WORKER=no
    ORCA_WORKER_COUNT=0
fi

# Defaults for Orca worker flags

# Audit log helper: record removal actions with timestamp and actor
# Note: audit_log function is defined in common-utils.sh
DEPLOY_AUDIT_LOG=${DEPLOY_AUDIT_LOG:-"./.deploy-audit.log"}

# Defensive helper: remove any top-level `version:` keys from YAML files generated by this script.
# Uses awk to filter lines starting with optional whitespace followed by 'version:' (case-insensitive)
# and rewrites the file atomically. This avoids macOS vs Linux sed -i portability issues.
remove_version_keys() {
    local file="$1"
    [ -f "$file" ] || return 0
    # Create a temporary file in the same dir to avoid cross-fs mv issues
    local dir
    dir=$(dirname "$file")
    local tmp
    tmp=$(mktemp "$dir/.tmp.XXXXXX") || tmp="${file}.tmp"
    # Filter out lines that begin with optional whitespace then 'version:' (case-insensitive)
    awk 'BEGIN{IGNORECASE=1} !/^[[:space:]]*version[[:space:]]*:/' "$file" > "$tmp" || true
    # Preserve mode if possible
    if [ -f "$tmp" ]; then
        mv "$tmp" "$file" 2>/dev/null || (cat "$tmp" > "$file" && rm -f "$tmp")
    fi
}

# Generic helper to upsert KEY=value pairs in simple env/config files
update_kv_file() {
    local file="$1"
    local key="$2"
    local value="$3"
    local tmp
    tmp=$(mktemp "${file}.tmp.XXXXXX" 2>/dev/null || mktemp)
    local found=0

    if [ -f "$file" ]; then
        while IFS= read -r line || [ -n "$line" ]; do
            if [[ "$line" == "$key="* ]]; then
                echo "$key=$value" >> "$tmp"
                found=1
            else
                echo "$line" >> "$tmp"
            fi
        done < "$file"
        if [ $found -eq 0 ]; then
            echo "$key=$value" >> "$tmp"
        fi
    else
        echo "$key=$value" > "$tmp"
    fi

    mv "$tmp" "$file"
}

get_kv_from_file() {
    local file="$1"
    local key="$2"
    [ -f "$file" ] || return 1
    grep -E "^${key}=" "$file" 2>/dev/null | tail -1 | sed -E "s/^${key}=//"
}

load_env_file() {
    if [ -f "$ENV_FILE" ]; then
        set -a
        # shellcheck disable=SC1090
        source "$ENV_FILE"
        set +a
    fi
}

# Helper: ensure the current shell exports a KEY=value pair.
set_exported_env_var() {
    local key="$1"
    local value="$2"
    if [ -z "$key" ]; then
        return 1
    fi
    export "$key=$value"
}

# Helper: resync a specific variable from the env file when it changes on disk.
sync_env_var_with_file() {
    local key="$1"
    [ -n "$key" ] || return 0
    local env_file="${ENV_FILE:-.env}"
    local file_value
    file_value=$(get_kv_from_file "$env_file" "$key" || true)
    if [ -z "$file_value" ]; then
        return 0
    fi
    local current_value
    current_value=$(printenv "$key" 2>/dev/null || true)
    if [ "$current_value" != "$file_value" ]; then
        print_info "Resyncing $key from $ENV_FILE to avoid stale shell overrides"
        set_exported_env_var "$key" "$file_value"
    fi
}

ensure_database_passwords() {
    local provider="$(echo "${DB_PROVIDER:-}" | tr '[:upper:]' '[:lower:]')"
    local env_pw=""
    env_pw=$(get_kv_from_file "$ENV_FILE" "POSTGRES_PASSWORD" || true)

    case "$provider" in
        postgres)
            if [ -n "$env_pw" ]; then
                POSTGRES_PASSWORD="$env_pw"
                return 0
            fi

            print_warning "Detected empty POSTGRES_PASSWORD after loading $ENV_FILE; regenerating secure password."
            local new_pw="${DB_PASSWORD:-}"
            if [ -z "$new_pw" ]; then
                new_pw=$(generate_random_password)
            fi

            POSTGRES_PASSWORD="$new_pw"
            DB_PASSWORD="$new_pw"

            update_kv_file "$ENV_FILE" "POSTGRES_PASSWORD" "$POSTGRES_PASSWORD"
            update_kv_file "$ENV_FILE" "DB_PASSWORD" "$DB_PASSWORD"

            local conn="Host=postgres;Database=${POSTGRES_DB:-printfarmer};Username=${POSTGRES_USER:-postgres};Password=$POSTGRES_PASSWORD"
            update_kv_file "$ENV_FILE" "ConnectionStrings__Default" "$conn"
            set_exported_env_var "ConnectionStrings__Default" "$conn"

            if [ -f "$CONFIG_FILE" ]; then
                update_kv_file "$CONFIG_FILE" "POSTGRES_PASSWORD" "$POSTGRES_PASSWORD"
                update_kv_file "$CONFIG_FILE" "DB_PASSWORD" "$DB_PASSWORD"
                local escaped_conn
                escaped_conn=$(printf '%q' "$conn")
                update_kv_file "$CONFIG_FILE" "CONNECTION_STRING" "$escaped_conn"
            fi

            print_success "Repaired missing PostgreSQL password (saved to $ENV_FILE)"
            ;;
        sqlserver)
            if [ -n "${SQLSERVER_PASSWORD:-}${MSSQL_SA_PASSWORD:-}" ]; then
                return 0
            fi
            print_error "SQL Server password is empty. Update SQLSERVER_PASSWORD in $ENV_FILE and rerun."
            exit 3
            ;;
        mysql)
            if [ -n "${MYSQL_PASSWORD:-}${MYSQL_ROOT_PASSWORD:-}" ]; then
                return 0
            fi
            print_error "MySQL password is empty. Update MYSQL_PASSWORD in $ENV_FILE and rerun."
            exit 3
            ;;
    esac
}

mask_secret_short() {
    local s="$1"
    local len=${#s}
    if [ $len -le 4 ]; then
        printf '%s' "$s"
        return
    fi
    printf '%s****%s' "${s:0:2}" "${s: -2}"
}

ensure_connection_string_password() {
    local provider="$(echo "${DB_PROVIDER:-}" | tr '[:upper:]' '[:lower:]')"
    
    # SQLite doesn't require a password
    if [ "$provider" = "sqlite" ]; then
        return 0
    fi
    
    local conn
    conn=$(get_kv_from_file "$ENV_FILE" "ConnectionStrings__Default" || true)
    if [ -z "$conn" ]; then
        print_warning "ConnectionStrings__Default missing from $ENV_FILE; API will reconstruct its own default connection string at runtime."
        return 0
    fi

    local current_password
    current_password=$(extract_conn_setting "Password" "$conn")
    if [ -n "$current_password" ]; then
        return 0
    fi
    local fallback_pw=""
    case "$provider" in
        postgres)
            fallback_pw=$(get_kv_from_file "$ENV_FILE" "POSTGRES_PASSWORD" || true)
            ;;
        sqlserver)
            fallback_pw=$(get_kv_from_file "$ENV_FILE" "SQLSERVER_PASSWORD" || true)
            if [ -z "$fallback_pw" ]; then
                fallback_pw=$(get_kv_from_file "$ENV_FILE" "MSSQL_SA_PASSWORD" || true)
            fi
            ;;
        mysql)
            fallback_pw=$(get_kv_from_file "$ENV_FILE" "MYSQL_PASSWORD" || true)
            if [ -z "$fallback_pw" ]; then
                fallback_pw=$(get_kv_from_file "$ENV_FILE" "MYSQL_ROOT_PASSWORD" || true)
            fi
            ;;
    esac

    if [ -z "$fallback_pw" ]; then
        print_error "ConnectionStrings__Default is missing Password= and no provider password could be found in $ENV_FILE."
        print_error "Update the environment file with the correct password and rerun the deployment."
        exit 3
    fi

    local rebuilt=""
    local added_password=false
    IFS=';' read -ra conn_parts <<< "$conn"
    for part in "${conn_parts[@]}"; do
        if [ -z "$part" ]; then
            continue
        fi
        local key="${part%%=*}"
        local value="${part#*=}"
        local key_lower=$(echo "$key" | tr '[:upper:]' '[:lower:]')
        if [ "$key_lower" = "password" ]; then
            value="$fallback_pw"
            added_password=true
        fi
        if [ -n "$rebuilt" ]; then
            rebuilt+=";"
        fi
        rebuilt+="$key=$value"
    done

    if [ "$added_password" = false ]; then
        if [ -n "$rebuilt" ]; then
            rebuilt+=";"
        fi
        rebuilt+="Password=$fallback_pw"
    fi

    update_kv_file "$ENV_FILE" "ConnectionStrings__Default" "$rebuilt"
    set_exported_env_var "ConnectionStrings__Default" "$rebuilt"
    print_info "ConnectionStrings__Default had no password; patched using provider credentials ($(mask_secret_short "$fallback_pw"))."
}

# Helper: generate a random strong password for database SA user
generate_random_password() {
    # Try openssl first for portability
    if command -v openssl >/dev/null 2>&1; then
        pw=$(openssl rand -base64 18 | tr -d '/+' | cut -c1-16)
    else
        pw=$(tr -dc 'A-Za-z0-9!@#$%&*()-_=+' </dev/urandom 2>/dev/null | head -c 16 || echo "Pfarm$(date +%s)")
    fi

    # Ensure basic complexity: at least one upper, one lower, one digit, one symbol
    if ! echo "$pw" | grep -q '[A-Z]'; then
        pw="A$pw"
    fi
    if ! echo "$pw" | grep -q '[a-z]'; then
        pw="${pw}a"
    fi
    if ! echo "$pw" | grep -q '[0-9]'; then
        pw="${pw}1"
    fi
    # Symbol = any non-alphanumeric
    if ! echo "$pw" | grep -q '[^A-Za-z0-9]'; then
        pw="${pw}!"
    fi

    echo "$pw"
}

# Helper: generate a secure API key for slicer worker registration
# Returns a URL-safe Base64-encoded string suitable for API authentication
generate_slicer_api_key() {
    # Generate a 32-byte random value and encode as URL-safe Base64 (removing padding)
    if command -v openssl >/dev/null 2>&1; then
        # openssl rand outputs binary, encode with base64 and remove padding/special chars
        openssl rand -base64 32 | tr -d '/+=\n' | cut -c1-32
    else
        # Fallback: use /dev/urandom with tr to create base64-like characters
        tr -dc 'A-Za-z0-9_-' </dev/urandom 2>/dev/null | head -c 32 || echo "apikey$(date +%s%N | md5sum | awk '{print $1}' | cut -c1-24)"
    fi
}

generate_deployment_config() {
    local architecture="$1"
    local include_monitoring="${2:-false}"
    local include_telemetry="${3:-false}"
    local include_security="${4:-false}"
    local include_registry="${5:-false}"
    local include_discovery="${6:-false}"
    local output_dir="${7:-$(pwd)}"
    
    print_info "Generating deployment configuration for $architecture architecture..."
    
    # Use the compose generator
    local generator_cmd="$SCRIPT_DIR/docker/compose-generator.sh"
    local generator_args=("--architecture" "$architecture")
    
    # Add optional services based on configuration
    if [ "$include_monitoring" = "true" ]; then
        generator_args+=("--include-monitoring")
    fi
    if [ "$include_telemetry" = "true" ]; then
        generator_args+=("--include-telemetry")
    fi
    if [ "$include_security" = "true" ]; then
        generator_args+=("--include-security")
    fi
    if [ "$include_registry" = "true" ]; then
        generator_args+=("--include-registry")
    fi
    if [ "$include_discovery" = "true" ]; then
        generator_args+=("--include-discovery")
    fi
    
    # Add worker configuration
    if [ -n "${ENABLE_ORCA_WORKER:-}" ]; then
        generator_args+=("--enable-orca-worker" "$ENABLE_ORCA_WORKER")
    fi

    
    # Add database provider configuration
    if [ -n "${DB_PROVIDER:-}" ]; then
        generator_args+=("--db-provider" "$DB_PROVIDER")
    fi
    
    # Set output directory
    generator_args+=("--output-dir" "$output_dir")
    
    if [ "$DRY_RUN" = "true" ]; then
        generator_args+=("--dry-run")
    fi

    if [ "${KEEP_GENERATED:-true}" = "false" ]; then
        generator_args+=("--cleanup-generated")
    fi
    
    # Check if the generator exists
    if [ ! -f "$generator_cmd" ]; then
        print_error "Compose generator not found: $generator_cmd"
        print_info "Falling back to legacy compose file generation..."
        return 1
    fi
    
    # Run the generator
    local elastic_env_value="${ENABLE_ELASTIC_STACK:-false}"

    if ENABLE_ELASTIC_STACK="$elastic_env_value" "$generator_cmd" "${generator_args[@]}"; then
        print_success "Deployment configuration generated successfully"
        
        # Set the compose file path for the rest of the deployment script
        COMPOSE_FILE="docker-compose.yml"

        # Helper: consistently invoke docker compose with the active env file and compose file
        # Usage: dc <docker-compose subcommand and args...>
        dc() {
            # forward all args to docker compose while ensuring env and compose file are applied
            docker compose --env-file "${ENV_FILE:-.env}" -f "${COMPOSE_FILE:-docker-compose.yml}" "$@"
        }
        
        # Record generated files for cleanup
        GENERATED_FILES=(
            "docker-compose.yml"
            "docker-entrypoint-config.sh"
        )
        
        # Add Dockerfiles based on architecture - all use multi-stage builds now
        case "$architecture" in
            "monolithic")
                GENERATED_FILES+=("Dockerfile.multistage")
                ;;
            "microservices")
                GENERATED_FILES+=("Dockerfile.multistage")
                ;;
            "host-network")
                GENERATED_FILES+=("Dockerfile.multistage")
                ;;
        esac

        # Ensure Dockerfile.multistage is available in the output directory. Many compose templates
        # expect this file at the compose context root (docker compose build with context: .).
        # Prefer the repo-local dockerfiles/ copy if present; copy it into the output dir so builds
        # that use context '.' can find it.
        if [ -f "$REPO_ROOT/dockerfiles/Dockerfile.multistage" ]; then
            if [ ! -f "$output_dir/Dockerfile.multistage" ]; then
                print_info "Copying dockerfiles/Dockerfile.multistage -> $output_dir/Dockerfile.multistage"
                cp "$REPO_ROOT/dockerfiles/Dockerfile.multistage" "$output_dir/Dockerfile.multistage" || true
            fi
            # Ensure it's listed for potential cleanup
            case " ${GENERATED_FILES[*]} " in
                *" Dockerfile.multistage "*) : ;;
                *) GENERATED_FILES+=("Dockerfile.multistage");;
            esac
        fi
        
        # Add optional config files
        [ "$include_monitoring" = "true" ] && GENERATED_FILES+=("prometheus.yml")
        [ "$include_telemetry" = "true" ] && GENERATED_FILES+=("otel-collector-config.yaml")
        [ "$include_security" = "true" ] && GENERATED_FILES+=("security-config.json")
        [ "$include_registry" = "true" ] && GENERATED_FILES+=("registry-config.yml")
        
        return 0
    else
        print_error "Failed to generate deployment configuration"
        return 1
    fi
}

# Cleanup generated deployment files
cleanup_generated_files() {
    if [ "${KEEP_GENERATED:-true}" = "true" ]; then
        print_info "Keeping generated deployment files (set KEEP_GENERATED=false or use --cleanup-generated to auto-remove)"
        return 0
    fi

    if [ -n "${GENERATED_FILES:-}" ]; then
        print_info "Cleaning up generated deployment files..."
        for file in "${GENERATED_FILES[@]}"; do
            if [ -f "$file" ]; then
                rm -f "$file"
                print_info "  • Removed $file"
            fi
        done
        audit_log "cleanup" "removed generated files: ${GENERATED_FILES[*]}"
    fi
}

# Helper: read a value from generated environment file (if present)
get_env_value() {
    local key="$1"
    [ -f "$ENV_FILE" ] || return 0
    # shellcheck disable=SC2002
    cat "$ENV_FILE" 2>/dev/null | grep -E "^${key}=" | tail -1 | cut -d= -f2- | tr -d '\r'
}

# Helper: extract a key from semicolon-delimited connection string (case-insensitive)
extract_conn_setting() {
    local target_key="$1"
    local conn_string="$2"
    [ -n "$conn_string" ] || return 0
    echo "$conn_string" | tr ';' '\n' | while IFS= read -r segment; do
        local key=$(echo "$segment" | cut -d= -f1 | tr '[:upper:]' '[:lower:]')
        local value=$(echo "$segment" | cut -d= -f2-)
        if [ "$key" = "$(echo "$target_key" | tr '[:upper:]' '[:lower:]')" ]; then
            echo "$value"
            return 0
        fi
    done
}

run_api_diagnostics() {
    local title="${1:-🩺 API Diagnostics}"
    print_header "$title"

    if [ ! -f "$ENV_FILE" ]; then
        print_warning "Environment file $ENV_FILE not found; skipping configuration diagnostics."
    else
        local conn_string
        conn_string=$(get_env_value "ConnectionStrings__Default")
        if [ -n "$conn_string" ]; then
            print_info "ConnectionStrings__Default: $conn_string"

            local host_network_enabled="false"
            if [ "${NETWORK_MODE:-bridge}" = "host" ] || ${SYSTEM_HOST_NETWORK:-false}; then
                host_network_enabled="true"
            fi

            if [ "$host_network_enabled" = "true" ]; then
                if echo "$conn_string" | grep -qiE 'host=(database|postgres|sqlserver|mysql)'; then
                    print_warning "Host network mode detected but connection string uses Docker service name. Use Host=localhost when the API is on the host network."
                fi
            else
                if echo "$conn_string" | grep -qiE 'host=(localhost|127\.0\.0\.1)'; then
                    print_warning "Bridge network detected but connection string points to localhost. Use the service name (e.g., Host=database)."
                fi
            fi
        else
            print_warning "ConnectionStrings__Default not found in $ENV_FILE."
        fi
    fi

    print_info "Container status snapshot:"
    dc ps || true

    if [ "$ARCHITECTURE" = "microservices" ]; then
    print_info "Database container status:"
    dc ps database || true
    fi

    local provider=$(echo "${DB_PROVIDER:-postgres}" | tr '[:upper:]' '[:lower:]')

    case "$provider" in
        postgres)
            if dc --format json ps --services 2>/dev/null | grep -q '^database$'; then
                local pg_user pg_db pg_password
                pg_user=$(get_env_value "POSTGRES_USER"); pg_user=${pg_user:-postgres}
                pg_db=$(get_env_value "POSTGRES_DB"); pg_db=${pg_db:-printfarmer}
                pg_password=$(get_env_value "POSTGRES_PASSWORD")

                if [ -n "$pg_password" ]; then
                    print_info "PostgreSQL readiness (pg_isready):"
                    dc exec -T database sh -c "PGPASSWORD=\"$pg_password\" pg_isready -U \"$pg_user\" -d \"$pg_db\"" || true
                    print_info "Sample database tables:"
                    dc exec -T database sh -c "PGPASSWORD=\"$pg_password\" psql -U \"$pg_user\" -d \"$pg_db\" -At -c \"SELECT table_name FROM information_schema.tables WHERE table_schema='public' ORDER BY table_name LIMIT 5;\"" || true
                else
                    print_warning "POSTGRES_PASSWORD not set; skipping Postgres connectivity diagnostics."
                fi
            fi
            ;;
        sqlserver)
            if dc --format json ps --services 2>/dev/null | grep -q '^database$'; then
                local sql_password sql_db
                sql_password=$(get_env_value "SQLSERVER_PASSWORD"); sql_password=${sql_password:-$(get_env_value "DB_PASSWORD")}
                sql_db=$(get_env_value "SQLSERVER_DB"); sql_db=${sql_db:-printfarmer}

                    if [ -n "$sql_password" ]; then
                    print_info "SQL Server ping (sqlcmd):"
                    dc exec -T database /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P "$sql_password" -Q "SELECT name FROM sys.databases;" || true
                else
                    print_warning "SQLSERVER_PASSWORD not found; skipping SQL Server diagnostics."
                fi
            fi
            ;;
        mysql)
            if dc --format json ps --services 2>/dev/null | grep -q '^database$'; then
                local mysql_user mysql_password mysql_db
                mysql_user=$(get_env_value "MYSQL_USER"); mysql_user=${mysql_user:-root}
                mysql_password=$(get_env_value "MYSQL_ROOT_PASSWORD")
                if [ -z "$mysql_password" ]; then
                    mysql_password=$(get_env_value "MYSQL_PASSWORD")
                fi
                mysql_db=$(get_env_value "MYSQL_DB"); mysql_db=${mysql_db:-printfarmer}

                    if [ -n "$mysql_password" ]; then
                    print_info "MySQL ping (mysqladmin):"
                    dc exec -T database sh -c "mysqladmin ping -h 127.0.0.1 -u \"$mysql_user\" --password=\"$mysql_password\"" || true
                    print_info "Sample database tables:"
                    dc exec -T database sh -c "mysql -u \"$mysql_user\" --password=\"$mysql_password\" $mysql_db -e \"SHOW TABLES;\" | head -n 10" || true
                else
                    print_warning "MySQL password not found; skipping MySQL diagnostics."
                fi
            fi
            ;;
        *)
            if [ "$ARCHITECTURE" = "microservices" ]; then
                print_warning "No diagnostics defined for DB provider '$provider'."
            fi
            ;;
    esac

    local conn_string
    conn_string=$(get_env_value "ConnectionStrings__Default")
    if [ -n "$conn_string" ]; then
        local host_network_enabled="false"
        if [ "${NETWORK_MODE:-bridge}" = "host" ] || ${SYSTEM_HOST_NETWORK:-false}; then
            host_network_enabled="true"
        fi

        local db_host
        db_host=$(extract_conn_setting "Host" "$conn_string")
        if [ -z "$db_host" ]; then
            db_host=$(extract_conn_setting "Server" "$conn_string")
        fi
        if [ -z "$db_host" ]; then
            db_host=$(extract_conn_setting "Data Source" "$conn_string")
        fi

        if [ -n "$db_host" ] && [ "$host_network_enabled" = "false" ]; then
            local api_running
            api_running=$(dc ps --format '{{.Name}} {{.State}}' 2>/dev/null | grep 'api ' || true)
            if echo "$api_running" | grep -qi 'running'; then
                print_info "DNS reachability from API container (ping $db_host):"
                docker compose --env-file "$ENV_FILE" exec -T api sh -c "ping -c 1 -W 1 $db_host" || true
            fi
        fi
    fi

    print_info "Recent API logs (last 40 lines):"
    dc logs api --tail 40 || true
}

# Note: force_remove_matching_containers is now provided by docker-utils.sh
# as docker_force_remove_matching_containers()


# Function to prompt user with default value
prompt_with_default() {
    local prompt="$1"
    local default="$2"
    local var_name="$3"
    
    # If variable already set (from env or loaded config), use it as default
    if [ -n "${!var_name:-}" ]; then
        default="${!var_name}"
    fi
    
    if [ "$NON_INTERACTIVE" = "true" ]; then
        # In non-interactive mode, keep existing value or use default
        if [ -z "${!var_name:-}" ]; then
            eval "$var_name=\"$default\""
        fi
    else
        echo -e "${YELLOW}$prompt${NC}"
        echo -e "${BLUE}Default: $default${NC}"
        read -r input || true
        if [ -z "$input" ]; then
            eval "$var_name=\"$default\""
        else
            eval "$var_name=\"$input\""
        fi
    fi
}

# Function to prompt yes/no with default
prompt_yes_no() {
    local prompt="$1"
    local default="$2"
    local var_name="$3"
    
    # If variable already set (from env or loaded config), use it as default
    if [ -n "${!var_name:-}" ]; then
        default="${!var_name}"
    fi
    
    local default_text="y/N"
    if [ "$default" = "y" ] || [ "$default" = "yes" ]; then
        default_text="Y/n"
    fi
    
    if [ "$NON_INTERACTIVE" = "true" ]; then
        # In non-interactive mode, keep existing value or use default
        if [ -z "${!var_name:-}" ]; then
            if [ "$default" = "y" ] || [ "$default" = "yes" ]; then
                eval "$var_name=\"yes\""
            else
                eval "$var_name=\"no\""
            fi
        fi
    else
        echo -e "${YELLOW}$prompt${NC}"
        echo -e "${BLUE}Default: $default_text${NC}"
        read -r input || true
        if [ -z "$input" ]; then
            input="$default"
        fi
        case "$input" in
            [Yy]|[Yy]es) eval "$var_name=\"yes\"" ;;
            *) eval "$var_name=\"no\"" ;;
        esac
    fi
}

# ============================================================================
# IMAGE MANAGEMENT FUNCTIONS
# ============================================================================

# Pull all base images from registry
pull_base_images() {
    print_header "📥 Pulling Base Container Images"
    
    print_info "Pulling essential images for PrintFarmer core services:"
    print_info "  - .NET SDK 9.0 (multi-stage builds during deployment)"
    print_info "  - .NET ASP.NET 9.0 (API runtime)"
    print_info "  - Node.js 22 Alpine (React frontend)"
    print_info "  - PostgreSQL 16 Alpine (database)"
    print_info "  - Nginx Alpine (reverse proxy/load balancer for microservices)"
    print_info ""
    print_info "Download size: approximately 450-700MB"
    print_info "Note: Images already present locally will be checked for updates"
    echo
    
    local success_count=0
    local fail_count=0
    
    for image in "${DOCKER_BASE_IMAGES[@]}"; do
        # Check if image already exists locally
        if docker images --quiet "$image" >/dev/null 2>&1; then
            print_info "Image already present locally: $image"
            print_info "Checking for updates..."
        else
            print_info "Pulling $image..."
        fi
        
        if docker pull "$image" 2>&1; then
            print_success "✓ $image"
            ((success_count++))
        else
            print_warning "✗ Failed to pull $image"
            ((fail_count++))
        fi
    done
    
    echo
    print_header "Pull Summary"
    print_success "Successfully processed: $success_count/${#DOCKER_BASE_IMAGES[@]}"
    
    if [ $fail_count -gt 0 ]; then
        print_warning "Failed to pull: $fail_count images"
        print_info "Check your internet connection and try again"
        return 1
    fi
    
    print_success "All base images processed successfully!"
    return 0
}

# Save images to TAR files
save_images_to_tar() {
    local target_dir="${1:-.}"
    
    print_header "💾 Exporting Images to TAR Files"
    
    if [ ! -d "$target_dir" ]; then
        mkdir -p "$target_dir"
        print_info "Created directory: $target_dir"
    fi
    
    local success_count=0
    local fail_count=0
    local total_size=0
    local exported_images=()
    
    # Export upgraded base images first (preferred)
    print_info "Exporting pre-upgraded base images (with tools pre-installed)..."
    for image in "${DOCKER_UPGRADED_IMAGES[@]}"; do
        # Check if upgraded image exists
        if ! docker images --quiet "$image" >/dev/null 2>&1; then
            print_info "  Skipping $image (not built)"
            continue
        fi
        
        # Replace special characters in image name for filename
        local safe_name
        safe_name=$(echo "$image" | sed 's|[:/ ]|-|g')
        local tar_file="$target_dir/$safe_name.tar"
        
        print_info "Exporting $image to $tar_file..."
        if docker save -o "$tar_file" "$image" > /dev/null 2>&1; then
            local file_size
            file_size=$(stat -f%z "$tar_file" 2>/dev/null || stat -c%s "$tar_file" 2>/dev/null)
            local file_size_mb=$((file_size / 1048576))
            total_size=$((total_size + file_size))
            
            print_success "✓ Exported: $image - Size: ${file_size_mb} MB"
            exported_images+=("$image")
            ((success_count++))
        else
            print_warning "✗ Failed to export $image"
            ((fail_count++))
        fi
    done
    
    # Export standard base images only if upgraded version wasn't exported
    print_info "Checking for fallback base images..."
    for i in "${!DOCKER_BASE_IMAGES[@]}"; do
        local base_image="${DOCKER_BASE_IMAGES[$i]}"
        local upgraded_image="${DOCKER_UPGRADED_IMAGES[$i]}"
        
        # Skip if upgraded version was already exported
        if [[ " ${exported_images[*]} " =~ " ${upgraded_image} " ]]; then
            print_info "  Skipping $base_image (using upgraded version)"
            continue
        fi
        
        # Check if base image exists
        if ! docker images --quiet "$base_image" >/dev/null 2>&1; then
            print_info "  Skipping $base_image (not present)"
            continue
        fi
        
        # Replace special characters in image name for filename
        local safe_name
        safe_name=$(echo "$base_image" | sed 's|[:/ ]|-|g')
        local tar_file="$target_dir/$safe_name.tar"
        
        print_info "Exporting $base_image to $tar_file..."
        if docker save -o "$tar_file" "$base_image" > /dev/null 2>&1; then
            local file_size
            file_size=$(stat -f%z "$tar_file" 2>/dev/null || stat -c%s "$tar_file" 2>/dev/null)
            local file_size_mb=$((file_size / 1048576))
            total_size=$((total_size + file_size))
            
            print_success "✓ Exported: $base_image - Size: ${file_size_mb} MB"
            exported_images+=("$base_image")
            ((success_count++))
        else
            print_warning "✗ Failed to export $base_image"
            ((fail_count++))
        fi
    done
    
    # Export OrcaSlicer binaries image if it exists
    for image in "${DOCKER_LOCAL_IMAGES[@]}"; do
        # Check if image exists locally
        if ! docker images --quiet "$image" >/dev/null 2>&1; then
            print_info "Skipping $image (not built locally)"
            continue
        fi
        
        # Replace special characters in image name for filename
        local safe_name
        safe_name=$(echo "$image" | sed 's|[:/ ]|-|g')
        local tar_file="$target_dir/$safe_name.tar"
        
        print_info "Exporting $image to $tar_file..."
        if docker save -o "$tar_file" "$image" > /dev/null 2>&1; then
            local file_size
            file_size=$(stat -f%z "$tar_file" 2>/dev/null || stat -c%s "$tar_file" 2>/dev/null)
            local file_size_mb=$((file_size / 1048576))
            total_size=$((total_size + file_size))
            
            print_success "✓ Exported: $image - Size: ${file_size_mb} MB"
            exported_images+=("$image")
            ((success_count++))
        else
            print_warning "⚠ Failed to export $image (optional)"
        fi
    done
    
    local total_size_mb=$((total_size / 1048576))
    local total_size_gb=$((total_size / 1073741824))
    
    echo
    print_header "Export Summary"
    print_success "Successfully exported: $success_count images"
    print_success "Total size: ${total_size_gb} GB - ${total_size_mb} MB"
    
    if [ $fail_count -gt 0 ]; then
        print_warning "Failed to export: $fail_count images"
        # Don't fail completely if some export fails
    fi
    
    print_success "Images exported successfully!"
    print_info "TAR files location: $target_dir"
    print_info "You can now transfer this folder to offline machines"
    
    # Create manifest with actually exported images
    local manifest_path="$target_dir/manifest.txt"
    {
        printf "%s\n" "${exported_images[@]}"
    } > "$manifest_path"
    print_info "Created manifest file: $manifest_path"
    
    return 0
}

# Load images from TAR files
# Load images from TAR files (for --load-images flag)
load_images_from_tar() {
    local source_dir="${1:-.}"
    
    print_header "📤 Loading Images from TAR Files"
    
    if [ ! -d "$source_dir" ]; then
        print_error "Images directory not found: $source_dir"
        print_info "Use --pull-images --save-images first to download and export images"
        return 1
    fi
    
    # Use the core loading function
    if _load_tar_images "$source_dir"; then
        echo
        print_success "All images loaded successfully!"
        print_info "Images are now available in local Docker daemon"
        
        # Check if orcaslicer-binaries was loaded and set ORCA_ASSET_IMAGE
        # This enables the build system to skip downloading OrcaSlicer from GitHub
        for local_image in "${DOCKER_LOCAL_IMAGES[@]}"; do
            if docker image inspect "$local_image" >/dev/null 2>&1; then
                if [[ "$local_image" == orcaslicer-binaries:* ]]; then
                    export ORCA_ASSET_IMAGE="$local_image"
                    print_info "🚀 Using prebuilt OrcaSlicer binaries: $local_image (skipping GitHub download)"
                fi
            fi
        done
        
        return 0
    else
        return 1
    fi
}

# Download and cache OrcaSlicer AppImage
cache_orcaslicer() {
    local target_dir="${1:-.}"
    local version="${2:-latest}"
    
    print_header "⬇️ Caching OrcaSlicer Linux AppImage"
    
    if [ ! -d "$target_dir" ]; then
        mkdir -p "$target_dir"
        print_success "Created cache directory: $target_dir"
    fi
    
    print_info "Looking up OrcaSlicer v${version} release information..."
    
    # Construct the GitHub API URL
    local release_url
    if [ "$version" = "latest" ]; then
        release_url="https://api.github.com/repos/SoftFever/OrcaSlicer/releases/latest"
    else
        release_url="https://api.github.com/repos/SoftFever/OrcaSlicer/releases/tags/v${version}"
    fi
    
    print_info "Fetching release info from GitHub API..."
    
    # Use curl to get release information
    local release_json
    if ! release_json=$(curl -s -L "$release_url" 2>/dev/null); then
        print_error "Failed to fetch release information from GitHub"
        return 1
    fi
    
    # Extract the AppImage download URL using multiple fallback strategies
    # Try jq first if available (cleaner parsing), fall back to grep/cut if not
    local download_url=""
    
    # Check if jq is available
    if command -v jq >/dev/null 2>&1; then
        # First try: Linux AppImage excluding aarch64 (prefer generic/x86_64)
        download_url=$(echo "$release_json" | jq -r '.assets[] | select(.name | contains("AppImage") and contains("Linux") and (contains("aarch64") | not)) | .browser_download_url' 2>/dev/null | head -1)
        
        if [ -z "$download_url" ]; then
            # Second try: any AppImage excluding aarch64
            download_url=$(echo "$release_json" | jq -r '.assets[] | select(.name | contains("AppImage") and (contains("aarch64") | not)) | .browser_download_url' 2>/dev/null | head -1)
        fi
    else
        # Fallback: use grep (less reliable but works without jq)
        # First try: Ubuntu AppImage variant
        download_url=$(echo "$release_json" | grep -o '"browser_download_url": "[^"]*Ubuntu[^"]*AppImage[^"]*"' | head -1 | cut -d'"' -f4 2>/dev/null)
        
        if [ -z "$download_url" ]; then
            # Second try: any Linux AppImage not aarch64
            download_url=$(echo "$release_json" | grep "Linux.*AppImage\|AppImage.*Linux" | grep -v "aarch64" | grep -o 'https://[^"]*AppImage[^"]*' | head -1 2>/dev/null)
        fi
        
        if [ -z "$download_url" ]; then
            # Last try: extract any AppImage URL not aarch64
            download_url=$(echo "$release_json" | grep -o 'https://[^"]*\.AppImage[^"]*' | grep -v "aarch64" | head -1 2>/dev/null)
        fi
    fi
    
    if [ -z "$download_url" ]; then
        print_error "Could not find AppImage asset for Linux in release"
        print_info "Alternative solutions:"
        print_info "1. Manual download from GitHub releases:"
        print_info "   https://github.com/SoftFever/OrcaSlicer/releases"
        print_info ""
        print_info "2. Download using browser and save to:"
        print_info "   $target_dir/"
        print_info ""
        print_info "3. Set environment variable for manual file:"
        print_info "   export ORCA_ASSET_PATH='$target_dir'"
        return 1
    fi
    
    local file_name
    file_name=$(basename "$download_url" | cut -d'?' -f1)
    local app_image_path="$target_dir/$file_name"
    
    # Check if already cached and valid (>50MB)
    if [ -f "$app_image_path" ]; then
        local file_size
        file_size=$(stat -f%z "$app_image_path" 2>/dev/null || stat -c%s "$app_image_path" 2>/dev/null)
        if [ $file_size -gt 52428800 ]; then  # 50MB in bytes
            local size_mb=$((file_size / 1048576))
            print_success "OrcaSlicer AppImage already cached: $app_image_path ($size_mb MB)"
            return 0
        else
            print_warning "Found corrupted/incomplete AppImage ($((file_size / 1048576)) MB), deleting and re-downloading..."
            rm -f "$app_image_path"
        fi
    fi
    
    print_info "Found asset: $file_name"
    print_info "Download URL: $download_url"
    print_info ""
    print_info "Downloading OrcaSlicer v${version} Linux AppImage..."
    print_info "This may take several minutes depending on internet speed"
    
    # Download the file
    if ! curl -L -o "$app_image_path" "$download_url" 2>&1; then
        print_error "Failed to download OrcaSlicer AppImage"
        rm -f "$app_image_path"
        return 1
    fi
    
    # Verify file size
    local file_size
    file_size=$(stat -f%z "$app_image_path" 2>/dev/null || stat -c%s "$app_image_path" 2>/dev/null)
    local size_mb=$((file_size / 1048576))
    
    if [ $file_size -lt 52428800 ]; then  # 50MB
        print_error "Downloaded file too small ($size_mb MB), likely invalid"
        rm -f "$app_image_path"
        return 1
    fi
    
    # Verify ELF magic number
    local magic
    magic=$(xxd -p -l 4 "$app_image_path" 2>/dev/null || head -c 4 "$app_image_path" | od -An -tx1 | tr -d ' ')
    if [ "$magic" != "7f454c46" ] && [ -n "$magic" ]; then
        print_warning "Could not verify ELF magic bytes, but file size looks reasonable"
    elif [ "$magic" = "7f454c46" ]; then
        print_success "File verified as valid ELF binary (AppImage)"
    fi
    
    print_success "OrcaSlicer AppImage cached successfully"
    print_info "Location: $app_image_path"
    print_info "Size: $size_mb MB"
    print_info ""
    print_info "OrcaSlicer AppImage will be automatically detected during deployment"
    print_info "No environment variables or additional arguments needed!"
    
    return 0
}

# Build pre-upgraded base images with apt/apk updates baked in
build_base_images() {
    local target_dir="${1:-.}"
    
    print_header "🏗️  BUILDING PRE-UPGRADED BASE IMAGES & ORCASLICER BINARIES"
    print_info "Building base images with apt/apk updates + OrcaSlicer binary layer for offline deployment"
    echo
    
    local docker_dir="scripts/docker/dockerfiles"
    
    # Array of base images to build: "base-tag|dockerfile|new-tag"
    declare -a BASE_IMAGES=(
        "ubuntu:24.04|Dockerfile.base-ubuntu|ubuntu:24.04-upgraded"
        "node:22-alpine|Dockerfile.base-node|node:22-alpine-upgraded"
        "mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim|Dockerfile.base-aspnet|mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim-upgraded"
        "mcr.microsoft.com/dotnet/sdk:9.0|Dockerfile.base-sdk|mcr.microsoft.com/dotnet/sdk:9.0-upgraded"
        "postgres:16-alpine|Dockerfile.base-postgres|postgres:16-alpine-upgraded"
        "nginx:alpine|Dockerfile.base-nginx|nginx:alpine-upgraded"
    )
    
    local total_images=${#BASE_IMAGES[@]}
    local successful=0
    local failed=0
    
    # Generate cache-bust timestamp to force fresh package updates
    local cache_bust
    cache_bust=$(date +%s)
    
    for image_config in "${BASE_IMAGES[@]}"; do
        IFS='|' read -r base_image dockerfile new_tag <<< "$image_config"
        
        print_info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
        print_info "Building: $new_tag"
        print_info "  Base: $base_image"
        print_info "  Dockerfile: $dockerfile"
        print_info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
        
        if docker build \
            -f "$docker_dir/$dockerfile" \
            -t "$new_tag" \
            --label="printfarmer-precache=true" \
            --build-arg "CACHE_BUST=$cache_bust" \
            . > /dev/null 2>&1; then
            
            print_success "✓ Build successful: $new_tag"
            ((successful++))
        else
            print_error "✗ Build failed: $new_tag"
            ((failed++))
        fi
        echo
    done
    
    # Build OrcaSlicer binary layer
    print_info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    print_info "Building: orcaslicer-binaries:2.3.1"
    print_info "  Extracts OrcaSlicer Linux AppImage for caching"
    print_info "  Dockerfile: Dockerfile.base-orcaslicer-binaries"
    print_info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    
    if docker build \
        -f "$docker_dir/Dockerfile.base-orcaslicer-binaries" \
        -t "orcaslicer-binaries:2.3.1" \
        --label="printfarmer-precache=true" \
        --build-arg ORCASLICER_VERSION=2.3.1 \
        --build-arg "CACHE_BUST=$cache_bust" \
        . > /dev/null 2>&1; then
        
        print_success "✓ Build successful: orcaslicer-binaries:2.3.1"
        ((successful++))
    else
        print_warning "⚠ Build failed: orcaslicer-binaries:2.3.1 (optional, continuing)"
        # Don't count as failure - OrcaSlicer binaries are optional
    fi
    echo
    
    # Pull BuildKit Dockerfile frontend (required for # syntax=docker/dockerfile:1)
    print_info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    print_info "Pulling: docker/dockerfile:1"
    print_info "  BuildKit Dockerfile frontend parser"
    print_info "  Required for advanced Dockerfile syntax features"
    print_info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    
    if docker pull docker/dockerfile:1 > /dev/null 2>&1; then
        print_success "✓ Pulled: docker/dockerfile:1"
        ((successful++))
    else
        print_warning "⚠ Failed to pull docker/dockerfile:1 (builds may require network)"
    fi
    echo
    
    print_info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    print_header "Base Image Build Summary"
    print_info "Successful: $successful/$((total_images + 2))"
    print_info "Failed: $failed/$total_images"
    
    if [ "$failed" -gt 0 ]; then
        print_warning "Some base images failed to build. Continuing with standard images."
        return 1
    fi
    
    print_success "All base images and OrcaSlicer binaries built successfully!"
    return 0
}

# Prepare offline deployment - comprehensive preparation
prepare_offline_deployment() {
    local target_dir="${1:-.}"
    
    print_header "🚀 OFFLINE DEPLOYMENT PREPARATION"
    print_info "This process prepares all materials needed for offline deployment:"
    print_info "  1. Build pre-upgraded base images with apt/apk updates (300-500MB)"
    print_info "  2. Build OrcaSlicer binary layer (100-200MB)"
    print_info "  3. Export all images to TAR files (450-700MB)"
    print_info "  4. Download and cache OrcaSlicer AppImage (100-200MB)"
    print_info ""
    print_info "Total time: ~25-35 minutes (depends on internet speed and system performance)"
    echo
    
    local start_time
    start_time=$(date +%s)
    local succeeded=true
    local upgraded_built=false
    
    # Step 1: Build pre-upgraded base images (including OrcaSlicer binaries)
    print_header "STEP 1/4: Building Pre-Upgraded Base Images & OrcaSlicer Binary Layer"
    if build_base_images "$target_dir"; then
        upgraded_built=true
        print_success "Pre-upgraded base images built successfully"
    else
        print_warning "Some base images failed to build, will use standard images as fallback"
    fi
    
    # Step 2: Only pull standard base images if upgraded versions weren't built
    if [ "$upgraded_built" = true ]; then
        echo
        print_header "STEP 2/4: Skipping Base Image Pull (Using Pre-Upgraded Images)"
        print_info "Pre-upgraded base images are available, no need to pull standard images"
        print_success "✓ Using pre-upgraded images with tools pre-installed"
    else
        echo
        print_header "STEP 2/4: Pulling Base Container Images (Fallback)"
        if ! pull_base_images; then
            print_error "Failed to pull base images"
            succeeded=false
        fi
    fi
    
    if [ "$succeeded" = true ]; then
        echo
        print_header "STEP 3/4: Exporting All Images (including OrcaSlicer binaries) to TAR Files"
        if ! save_images_to_tar "$target_dir"; then
            print_error "Failed to export images to TAR"
            succeeded=false
        fi
        
        if [ "$succeeded" = true ]; then
            echo
            print_header "STEP 4/4: Caching OrcaSlicer AppImage"
            if ! cache_orcaslicer "$target_dir/orcaslicer" "latest"; then
                print_warning "Failed to cache OrcaSlicer AppImage (optional)"
                # Don't fail overall if OrcaSlicer download fails
            fi
        fi
        
        # Cleanup: Remove dangling images and original base images to save disk space
        if [ "$succeeded" = true ]; then
            echo
            print_header "CLEANUP: Removing Redundant Images"
            print_info "Removing dangling images and original base images (upgraded versions exported)..."
            
            # Remove dangling images (leftover from builds)
            local dangling_count
            dangling_count=$(docker images -f "dangling=true" -q | wc -l)
            if [ "$dangling_count" -gt 0 ]; then
                docker image prune -f > /dev/null 2>&1
                print_success "✓ Removed $dangling_count dangling image(s)"
            fi
            
            # Remove original base images (we have upgraded versions exported)
            local removed_count=0
            for image in "${DOCKER_BASE_IMAGES[@]}"; do
                if docker images --quiet "$image" 2>/dev/null | grep -q .; then
                    if docker rmi "$image" > /dev/null 2>&1; then
                        print_info "  Removed: $image"
                        ((removed_count++))
                    fi
                fi
            done
            
            if [ "$removed_count" -gt 0 ]; then
                print_success "✓ Removed $removed_count original base image(s)"
            fi
            
            print_success "✓ Cleanup complete - only upgraded images remain"
        fi
    fi
    
    local end_time
    end_time=$(date +%s)
    local elapsed=$((end_time - start_time))
    local elapsed_min=$((elapsed / 60))
    
    echo
    print_header "OFFLINE PREPARATION SUMMARY"
    
    if [ "$succeeded" = true ]; then
        print_success "✓ Offline deployment materials prepared successfully!"
        print_info "Location: $target_dir"
        print_info "Contents:"
        print_info "  - Pre-upgraded base images with apt/apk updates (TAR files)"
        print_info "  - OrcaSlicer binary layer (extracted and cached)"
        print_info "  - OrcaSlicer AppImage for distributed slicing (optional)"
        print_info "  - Manifest files for offline loading"
        print_info "Total time: ${elapsed_min} minutes"
        echo
        print_info "Next steps:"
        print_info "  1. Transfer the '$target_dir' folder to your offline machine"
        print_info "  2. Run: ./scripts/deploy-docker.sh --deploy-offline --images-dir <path-to-images-folder>"
        print_info "     (The script will auto-detect and load cached images + OrcaSlicer binaries)"
        echo
        return 0
    else
        print_error "✗ Offline preparation failed. Check errors above and retry."
        print_info "Total time: ${elapsed_min} minutes"
        return 1
    fi
}

# Deploy using cached offline materials
deploy_offline_mode() {
    local source_dir="${1:-.}"
    
    print_header "🔌 OFFLINE DEPLOYMENT MODE"
    print_info "Loading pre-cached container images and preparing deployment"
    echo
    
    # Check if images directory exists
    if [ ! -d "$source_dir" ]; then
        print_error "Images directory not found: $source_dir"
        print_info "Run with --prepare-offline first to download and cache all materials"
        return 1
    fi
    
    # Load cached images from TAR files
    print_header "STEP 1/2: Loading Cached Container Images"
    if ! load_images_from_tar "$source_dir"; then
        print_error "Failed to load cached images"
        return 1
    fi
    
    # Auto-load OrcaSlicer if available
    echo
    print_header "STEP 2/2: Loading OrcaSlicer AppImage (Optional)"
    local orca_dir="$source_dir/orcaslicer"
    if [ -d "$orca_dir" ]; then
        auto_load_orcaslicer "$orca_dir"
    else
        print_info "OrcaSlicer cache not found - distributed slicing will be disabled"
    fi
    
    echo
    print_success "✓ Offline images loaded successfully!"
    print_info "Proceeding with deployment configuration..."
    return 0
}

# Tear down existing deployment
tear_down_deployment() {
    print_header "🧹 Tearing Down PrintFarmer Deployment"
    
    print_warning "This will:"
    echo "  1. Stop and remove ALL Docker containers"
    echo "  2. Remove ALL Docker volumes (⚠️  ALL DATA WILL BE DELETED!)"
    echo "  3. Remove all PrintFarmer Docker images"
    echo "  4. Prune dangling images (base images are preserved)"
    echo "  5. Clear Docker builder cache"
    echo "  6. Clean up generated configuration files"
    echo
    
    if [ "$NON_INTERACTIVE" = "false" ]; then
        echo -e "${RED}⚠️  WARNING: This is a destructive operation!${NC}"
        echo -e "${RED}   All database data and uploaded files will be permanently deleted.${NC}"
        echo
        read -p "Are you sure you want to continue? Type 'yes' to confirm: " confirm
        
        if [ "$confirm" != "yes" ]; then
            print_info "Tear-down cancelled."
            exit 0
        fi
    fi
    
    echo
    print_info "Starting tear-down process..."

    # First attempt: bring down compose-managed stacks so containers created by compose
    # are removed with the correct project name and associated volumes/networks.
    print_info "Attempting to stop compose stacks..."
    
    # Improved tear-down: stop services in a safe order with retries and a kill fallback
    stop_compose_services() {
        local env_file="$1"; shift
        local compose_file="$1"; shift

        print_info "Tearing down compose project: env_file='${env_file:-<none>}' compose_file='$compose_file'"

        # Preferred ordered list: stop frontends and API first, then workers, then monitoring/telemetry, then database
        local ordered_services=(frontend api orcaslicer-worker orcaslicer-worker-multistage worker prometheus grafana jaeger otel-collector database registry)

        # If an env file was provided, load its variables and pass it to docker compose commands
        local env_arg=( )
        if [ -n "${env_file:-}" ] && [ -f "$env_file" ]; then
            # Source the env file to export variables into the current shell context
            # This prevents "variable is not set. Defaulting to a blank string" warnings from docker compose
            set -a
            # shellcheck source=/dev/null
            source "$env_file"
            set +a
            env_arg=(--env-file "$env_file")
        fi

        for svc in "${ordered_services[@]}"; do
            # Check if the service exists in this compose file
            if docker compose "${env_arg[@]:-}" -f "$compose_file" ps --services 2>/dev/null | grep -qx "$svc"; then
                print_info "Stopping service: $svc"
                # Attempt a graceful stop first
                docker compose "${env_arg[@]:-}" -f "$compose_file" stop -t 20 "$svc" || true

                # Wait up to 20s for container(s) to exit
                for i in $(seq 1 10); do
                    running=$(docker compose "${env_arg[@]:-}" -f "$compose_file" ps --format '{{.Name}} {{.State}}' 2>/dev/null | grep -E "${svc}" || true)
                    if [ -z "$running" ]; then
                        print_success "Service $svc stopped"
                        break
                    fi
                    sleep 2
                done

                # If still present, attempt docker kill then rm -f
                running_now=$(docker compose "${env_arg[@]:-}" -f "$compose_file" ps --format '{{.Name}} {{.State}}' 2>/dev/null | grep -E "${svc}" || true)
                if [ -n "$running_now" ]; then
                    print_warning "Service $svc did not stop cleanly; killing container(s)"
                    # Get container ids for the service (compose project-scoped names)
                    docker compose "${env_arg[@]:-}" -f "$compose_file" ps --quiet "$svc" | xargs -r docker kill || true
                    docker compose "${env_arg[@]:-}" -f "$compose_file" rm -f -v "$svc" || true
                else
                    # Remove the stopped service to clean up networks/volumes when possible
                    docker compose "${env_arg[@]:-}" -f "$compose_file" rm -f -v "$svc" 2>/dev/null || true
                fi
            fi
        done

        # Finally, bring remaining services down and remove volumes
        # Use --rmi local to only remove locally-built images, preserving base images
        print_info "Running: docker compose ${env_file:+--env-file $env_file} -f $compose_file down --volumes --rmi local"
        # shellcheck disable=SC2086
        if [ -n "${env_file:-}" ] && [ -f "$env_file" ]; then
            docker compose --env-file "$env_file" -f "$compose_file" down --volumes --rmi local || true
        else
            docker compose -f "$compose_file" down --volumes --rmi local || true
        fi
    }

    # Run tear-down with appropriate env file and compose file
    if [ -f docker-compose.yml ]; then
        # Use .env if available, otherwise proceed without env file
        if [ -f .env ]; then
            stop_compose_services ".env" "docker-compose.yml"
        else
            stop_compose_services "" "docker-compose.yml"
        fi
    fi

    # Ensure standalone host-mode nginx proxy (if created by script) is removed
    if docker ps -a --format '{{.Names}}' | grep -q '^printfarmer-nginx-proxy$'; then
        print_info "Removing standalone host-mode nginx proxy container: printfarmer-nginx-proxy"
        docker rm -f printfarmer-nginx-proxy || true
        print_success "Removed printfarmer-nginx-proxy"
    fi

    # 1. Stop all remaining running containers (fallback)
    print_info "Step 1/7: Stopping any remaining running Docker containers..."
    local running_containers
    running_containers=$(timeout 10 docker ps -q 2>/dev/null || true)
    if [ -n "$running_containers" ]; then
        timeout 15 docker stop $running_containers 2>/dev/null || true
        print_success "Stopped running containers (attempted)"
    else
        print_info "No running containers found"
    fi

    # 2. Remove all remaining containers (force remove any leftovers)
    print_info "Step 2/7: Removing any remaining Docker containers..."
    local all_containers
    all_containers=$(timeout 10 docker ps -aq 2>/dev/null || true)
    if [ -n "$all_containers" ]; then
        # Try normal remove first, then force-remove to handle odd states
        timeout 15 docker rm $all_containers 2>/dev/null || true
        # Re-query and force remove stubborn containers
        remaining=$(timeout 10 docker ps -aq 2>/dev/null || true)
        if [ -n "$remaining" ]; then
            print_warning "Some containers remain after normal removal. Attempting force remove..."
            timeout 15 docker rm -f $remaining 2>/dev/null || true
        fi

        # Final check
        final_check=$(timeout 10 docker ps -aq 2>/dev/null || true)
        if [ -z "$final_check" ]; then
            print_success "Containers removed"
        else
            print_warning "Some containers could not be removed. Run 'docker ps -a' to inspect and remove manually."
            # Attempt best-effort force removal of commonly-named PrintFarmer containers
            print_info "Attempting force removal of known PrintFarmer containers (best-effort)..."
            docker_force_remove_matching_containers || true
        fi
    else
        print_info "No containers to remove"
    fi

    # Additionally, ensure any supported database containers are removed explicitly
    # This helps on systems where compose project names or previous runs left DB containers behind
    print_info "Ensuring PrintFarmer database containers are removed"
    # Look for both generic database containers and provider-specific legacy containers
    containers=$(docker ps -a --format '{{.Names}}' | grep -E "printfarmer-database|pfarm-(postgres|sqlserver|mysql)" || true)
    if [ -n "$containers" ]; then
        print_warning "Found database containers to remove: $containers"
        docker rm -f $containers 2>/dev/null || true
        print_success "Removed database containers: $containers"
        audit_log "remove" "teardown: removed database containers: $containers"
    fi
    
    # 3. Remove all volumes
    print_info "Step 3/7: Removing all Docker volumes..."
    local vol_list
    vol_list=$(timeout 10 docker volume ls -q 2>/dev/null || true)
    if [ -n "$vol_list" ]; then
        # Remove volumes with force flag (-f) to handle in-use volumes
        # Also handle per-project volumes explicitly (e.g., pfarm_printfarmer-database)
        echo "$vol_list" | while read -r vol; do
            timeout 5 docker volume rm -f "$vol" 2>/dev/null && echo "  • Removed $vol" || echo "  ⚠ Failed to remove $vol (may be in use)"
        done
        print_success "Volumes removal attempted"
    else
        print_info "No volumes to remove"
    fi
    
    # 4. Remove PrintFarmer images
    docker_cleanup_printfarmer_images force
    
    # 5-6. Docker system cleanup (preserve base images for faster rebuilds)
    docker_system_cleanup preserve-base
    
    # Clear Docker builder cache for next build
    print_info "Step 6/7: Clearing Docker builder cache..."
    docker builder prune -af 2>/dev/null || true
    print_success "Builder cache cleared"
    
    # 7. Remove generated files
    print_info "Step 7/7: Removing generated configuration and build artifacts..."
    local files_removed=0
    
    # Remove docker-compose files
    if [ -f docker-compose.yml ]; then
        rm -f docker-compose.yml
        echo "  • Removed docker-compose.yml"
        ((files_removed++))
    fi
    
    if [ -f docker-compose.override.yml ]; then
        rm -f docker-compose.override.yml
        echo "  • Removed docker-compose.override.yml"
        ((files_removed++))
    fi
    
    # Remove environment file
    if [ -f .env ]; then
        rm -f .env
        echo "  • Removed .env"
        ((files_removed++))
    fi
    
    # Ask about .deploy-config separately
    if [ -f .deploy-config ]; then
        if [ "$NON_INTERACTIVE" = "false" ]; then
            echo
            print_warning "Found .deploy-config (saved deployment preferences)"
            read -p "Do you want to keep this file? (y/n) [y]: " keep_config
            if [ "$keep_config" = "n" ] || [ "$keep_config" = "N" ]; then
                rm -f .deploy-config
                echo "  • Removed .deploy-config"
                ((files_removed++))
            else
                print_info "Kept .deploy-config (your preferences will be remembered)"
            fi
        else
            # In non-interactive mode, keep the config by default
            print_info "Kept .deploy-config (use --non-interactive to auto-remove)"
        fi
    fi
    
    if [ $files_removed -gt 0 ]; then
        print_success "Configuration files cleaned"
    else
        print_info "No configuration files to remove"
    fi
    
    # Remove external storage paths (data persistence directories)
    print_info "Step 8/8: Removing external storage directories..."
    local external_paths_removed=0
    
    # Load current config to find external paths
    local external_models_path=""
    local external_gcode_path=""
    local external_profiles_path=""
    local external_app_data_path=""
    local external_database_path=""
    
    if [ -f ".deploy-config" ]; then
        # Extract paths directly from config file without sourcing (safer)
        external_models_path=$(grep "^EXTERNAL_MODELS_PATH=" ./.deploy-config 2>/dev/null | cut -d= -f2- | tr -d '\r' || true)
        external_gcode_path=$(grep "^EXTERNAL_GCODE_PATH=" ./.deploy-config 2>/dev/null | cut -d= -f2- | tr -d '\r' || true)
        external_profiles_path=$(grep "^EXTERNAL_PROFILES_PATH=" ./.deploy-config 2>/dev/null | cut -d= -f2- | tr -d '\r' || true)
        external_app_data_path=$(grep "^EXTERNAL_APP_DATA_PATH=" ./.deploy-config 2>/dev/null | cut -d= -f2- | tr -d '\r' || true)
        external_database_path=$(grep "^EXTERNAL_DATABASE_PATH=" ./.deploy-config 2>/dev/null | cut -d= -f2- | tr -d '\r' || true)
    fi
    
    # Array of paths and descriptions for display
    local paths_to_remove=()
    if [ -n "$external_models_path" ] && [ -d "$external_models_path" ]; then
        paths_to_remove+=("$external_models_path:Models")
    fi
    if [ -n "$external_gcode_path" ] && [ -d "$external_gcode_path" ]; then
        paths_to_remove+=("$external_gcode_path:G-code")
    fi
    if [ -n "$external_profiles_path" ] && [ -d "$external_profiles_path" ]; then
        paths_to_remove+=("$external_profiles_path:Profiles")
    fi
    if [ -n "$external_app_data_path" ] && [ -d "$external_app_data_path" ]; then
        paths_to_remove+=("$external_app_data_path:App Data")
    fi
    if [ -n "$external_database_path" ] && [ -d "$external_database_path" ]; then
        paths_to_remove+=("$external_database_path:Database")
    fi
    
    if [ "$NON_INTERACTIVE" = "false" ]; then
        if [ ${#paths_to_remove[@]} -gt 0 ]; then
            echo
            print_warning "External storage directories found:"
            for path_entry in "${paths_to_remove[@]}"; do
                local path="${path_entry%:*}"
                local desc="${path_entry#*:}"
                echo "  • [$desc] $path"
            done
            echo
            read -p "Do you want to remove all external storage data? (y/n) [n]: " remove_storage
        else
            remove_storage="n"
        fi
        
        if [ "$remove_storage" = "y" ] || [ "$remove_storage" = "Y" ]; then
            for path_entry in "${paths_to_remove[@]}"; do
                local path="${path_entry%:*}"
                local desc="${path_entry#*:}"
                if [ -d "$path" ]; then
                    rm -rf "$path"
                    echo "  • Removed [$desc] $path"
                    ((external_paths_removed++))
                    audit_log "remove" "teardown: removed external storage: $path"
                fi
            done
            if [ $external_paths_removed -gt 0 ]; then
                print_success "External storage directories removed"
            fi
        else
            print_info "Kept external storage directories (data preserved)"
        fi
    else
        # In non-interactive mode with --tear-down, remove all external storage
        if [ ${#paths_to_remove[@]} -gt 0 ]; then
            print_warning "Removing external storage directories:"
            for path_entry in "${paths_to_remove[@]}"; do
                local path="${path_entry%:*}"
                local desc="${path_entry#*:}"
                if [ -d "$path" ]; then
                    rm -rf "$path"
                    echo "  • Removed [$desc] $path"
                    ((external_paths_removed++))
                    audit_log "remove" "teardown: removed external storage: $path"
                fi
            done
            print_success "External storage directories removed"
        else
            print_info "No external storage directories to remove"
        fi
    fi
    
    echo
    print_success "✨ Tear-down complete!"
    echo
    print_info "You can now run './scripts/deploy-docker.sh' to start a fresh deployment."
    
    exit 0
}

# Show help message
show_help() {
    cat << EOF
PrintFarmer Docker Deployment Script

USAGE:
    ./scripts/deploy-docker.sh [OPTIONS]

OPTIONS:
    -h, --help              Show this help message
    -n, --dry-run           Validate configuration without starting containers
    -b, --batch             Run in non-interactive mode (uses defaults/env vars)
        --non-interactive   Same as --batch
        --redeploy          Rebuild and restart using existing configuration
                            (detects previous deployment automatically)
    --tear-down             Tear down existing deployment (stops containers, removes
        --teardown          volumes, cleans up). Useful for starting fresh.
        --clean             Same as --tear-down
    --build-verbosity LEVEL Set Docker build verbosity: quiet (default), minimal, normal, detailed
        --verbose-build     Shorthand for --build-verbosity detailed
        --cleanup-generated Remove generated Docker files after deployment (default keeps them)
        --keep-generated    Preserve generated files (default; retained for compatibility)

SMART IMAGE CACHING - Automatic offline support:
    * Downloaded images are automatically cached for offline use
    * Subsequent deployments use cached images when available (NO arguments needed)
    * Automatically searches: ./docker-images, ~/docker-images, /mnt/usb/docker-images
    * Cache location: ~/.printfarmer/images-cache.json

ORCASLICER AUTO-DISCOVERY - Automatic offline support:
    * Cached OrcaSlicer AppImage is automatically discovered and used
    * NO arguments needed - searches the same cache locations as Docker images
    * Automatically searches: ./docker-images/orcaslicer, ~/docker-images/orcaslicer, etc.

SIMPLIFIED OFFLINE DEPLOYMENT (RECOMMENDED):
    Single command prepares ALL offline materials (pre-upgraded base images + OrcaSlicer):
    
    On machine WITH internet:
        ./scripts/deploy-docker.sh --prepare-offline
        
        This will:
          - Build pre-upgraded base images with apt/apk updates included
          - Build OrcaSlicer binary layer (extracted and cached)
          - Pull and export all Docker images to TAR files
          - Download OrcaSlicer AppImage
        
        Total size: ~2-2.5GB (depending on system)
        Total time: ~25-35 minutes
    
    Transfer ./docker-images folder to offline machine, then:
    
    On machine WITHOUT internet:
        ./scripts/deploy-docker.sh --deploy-offline
        
        This will:
          - Auto-detect and load cached Docker images
          - Auto-detect and load OrcaSlicer binary layer
          - Load cached OrcaSlicer AppImage (if available)
          - Proceed with normal deployment

MANUAL IMAGE MANAGEMENT OPTIONS (Advanced):
    --prepare-offline            Comprehensive prep: builds pre-upgraded base images + OrcaSlicer binaries, pulls images, exports TAR, caches OrcaSlicer AppImage
    --deploy-offline             Load cached images + OrcaSlicer binaries and proceed with deployment
    --pull-images                Download all base container images from registry
    --save-images                Export downloaded images to TAR files for offline use
    --load-images                Manually load saved images from TAR files
    --images-dir PATH            Directory for storing image TAR files (default: ./docker-images)
    --cache-orcaslicer           Download OrcaSlicer Linux AppImage for offline use
    --load-cached-orcaslicer     Show cached OrcaSlicer AppImage info

COMPOSE GENERATOR OPTIONS:
        --architecture ARCH Architecture to deploy (monolithic|microservices)
        --include-monitoring Include monitoring stack (Prometheus, Grafana)
        --include-telemetry Include telemetry/observability (OpenTelemetry)
        --include-security  Include security configurations
        --include-registry  Include local Docker registry
        --include-discovery Include network printer discovery service (microservices only)
        --output-dir DIR    Output directory for generated files (default: repository root)

VERIFY / UTILITY OPTIONS:
    --verify-deployment   Run verification steps only against an existing deployment (no generation/start)
    --env-file FILE       Use a specific .env file for verification or compose commands
    --config-file FILE    Use a specific deployment config file instead of $REPO_ROOT/.deploy-config

INITIAL ADMIN SETUP OPTIONS:
    --auto-admin                Create initial admin user automatically after deployment
    --auto-admin-config FILE    Load auto-admin settings from config file (searches: ~/.auto-admin-config, ~/.config/printfarmer/auto-admin-config, ./.auto-admin-config)
    --auto-admin-username USER  Set admin username (default: admin)
    --auto-admin-password PASS  Set admin password (default: auto-generated)
    --auto-admin-email EMAIL    Set admin email (default: admin@printfarmer.local)

EXAMPLES:
    # Interactive deployment (recommended for first-time setup)
    ./scripts/deploy-docker.sh

    # Quick redeploy with rebuild (uses existing configuration)
    ./scripts/deploy-docker.sh --redeploy

    # Tear down existing deployment and clean up
    ./scripts/deploy-docker.sh --tear-down
    # Force tear down (skip typing 'yes' prompt)
    ./scripts/deploy-docker.sh --tear-down --non-interactive

    # Validate configuration without deploying
    ./scripts/deploy-docker.sh --dry-run

    # Non-interactive deployment (for automation/CI)
    ./scripts/deploy-docker.sh --non-interactive

    # Verify-only mode against an existing deployment (useful in CI)
    ./scripts/deploy-docker.sh --verify-deployment --env-file .env --config-file .deploy-config

    # Deploy with automatic initial admin setup (skip setup wizard)
    ./scripts/deploy-docker.sh --non-interactive --auto-admin

    # === OFFLINE DEPLOYMENT ===
    
    # Prepare ALL offline materials to auto-discoverable location (RECOMMENDED)
    ./scripts/deploy-docker.sh --prepare-offline --images-dir ./docker-images
    ./scripts/deploy-docker.sh --prepare-offline --images-dir ~/docker-images
    
    # Prepare to USB drive (specify path when deploying)
    ./scripts/deploy-docker.sh --prepare-offline --images-dir /media/usb/docker-images
    
    # Deploy from cache (auto-discovers ./docker-images or ~/docker-images)
    ./scripts/deploy-docker.sh --deploy-offline
    
    # Deploy from a specific cache location (e.g., USB drive)
    ./scripts/deploy-docker.sh --deploy-offline --images-dir /media/usb/docker-images
    
    # Manual image management
    ./scripts/deploy-docker.sh --pull-images                    # Download images
    ./scripts/deploy-docker.sh --pull-images --save-images      # Download and export TAR
    ./scripts/deploy-docker.sh --save-images --images-dir ~/docker-images  # Export to auto-discoverable path
    ./scripts/deploy-docker.sh --load-images                    # Load from auto-discovered path
    ./scripts/deploy-docker.sh --cache-orcaslicer --images-dir ~/docker-images  # Cache to auto-discoverable path
    
    # Deploy specific architecture with additional services
    ./scripts/deploy-docker.sh --architecture microservices --include-monitoring
    
    # Deploy with full observability stack
    ./scripts/deploy-docker.sh --architecture microservices --include-monitoring --include-telemetry
    
    # Deploy monolithic with security and registry
    ./scripts/deploy-docker.sh --architecture monolithic --include-security --include-registry
    
    # Deploy microservices with printer discovery service
    ./scripts/deploy-docker.sh --architecture microservices --include-discovery
    
    # Deploy with monitoring + discovery + auto-admin
    ./scripts/deploy-docker.sh --architecture microservices --include-monitoring --include-discovery --auto-admin
    
    # Non-interactive deployment with all options
    ./scripts/deploy-docker.sh --non-interactive --architecture microservices --include-monitoring --include-telemetry --include-security --include-discovery

DEPLOYMENT MODES:
    1. Monolithic      - All services in one container (simplest)
    2. Microservices   - Separate API, frontend, workers (recommended)

DATABASE OPTIONS:
    1. PostgreSQL      - Open source, recommended for most users
    2. SQL Server      - Microsoft SQL Server (choose edition during setup)
                         • Developer: Free, full-featured (dev/test only)
                         • Express: Free, production-ready (10GB limit)
                         • Standard/Enterprise: Requires commercial license
    3. MySQL           - Popular open source database
    4. External        - Use your own database server

NETWORK MODES:
    1. Bridge          - Standard Docker networking (default)
    2. Host            - Direct host network access (for printer discovery)

DATA PERSISTENCE (P0 Requirement):
    During interactive deployment, you'll be prompted to configure external storage
    for critical data that must survive container recreation:
    
    • 3D Model Storage   - Maps to host directory (default: /var/lib/printfarmer/models)
    • Generated G-code   - Maps to host directory (default: /var/lib/printfarmer/gcode)
    • Slicer Profiles    - Maps to host directory (default: /var/lib/printfarmer/slicer-profiles)
    
    With external storage enabled:
    ✅ Data persists across container recreation (docker-compose down/up)
    ✅ Data survives image rebuild
    ✅ Data only deleted when explicitly removing the host directory
    ✅ Database deletion does NOT affect these directories
    ✅ Can easily backup/restore files from host filesystem
    
    To enable external storage in non-interactive mode, set:
        export USE_EXTERNAL_STORAGE=yes
        export EXTERNAL_MODELS_PATH=/path/to/models
        export EXTERNAL_GCODE_PATH=/path/to/gcode
        export EXTERNAL_PROFILES_PATH=/path/to/profiles
        ./scripts/deploy-docker.sh --non-interactive

PRINTER DISCOVERY (MICROSERVICES ONLY):
    The network printer discovery service automatically scans your local network 
    to find compatible 3D printers (Moonraker, PrusaLink, OctoPrint, SDCP).
    - Enabled by default in microservices deployments
    - Runs in host network mode to access local network
    - Scans configurable IP ranges periodically
    - Supports both automatic push and manual pull discovery modes
    - Accessible via API endpoint: POST /api/discovery/scan

For more information, see:
    - DOCKER_DEPLOYMENT.md
    - LOCAL_DEVELOPMENT.md
    - docs/PRINTER_DISCOVERY_ARCHITECTURE.md
    - README.md
    - OFFLINE_DEPLOYMENT_GUIDE.md

EOF
    exit 0
}

# Configuration file location (always stored in the repository root)
# Use an absolute path so the script loads the same config regardless of CWD
CONFIG_FILE="$REPO_ROOT/.deploy-config"

# Load previous configuration if it exists
load_previous_config() {
    if [ -f "$CONFIG_FILE" ]; then
        print_info "Found previous deployment configuration"
        
        # Source the config file to load variables
        # shellcheck disable=SC1090
        source "$CONFIG_FILE"

        # Mark that we loaded values from disk so downstream logic can
        # treat redacted placeholders as "not set" when necessary.
        LOADED_DEPLOY_CONFIG=true
        
        print_success "Loaded configuration from $CONFIG_FILE"
        
        # Display key settings that will be used as defaults
        if [ -n "${ARCHITECTURE:-}" ]; then
            echo -e "  ${BLUE}Architecture:${NC} $ARCHITECTURE"
        fi
        if [ -n "${DB_PROVIDER:-}" ]; then
            echo -e "  ${BLUE}Database:${NC} $DB_PROVIDER"
        fi
        if [ -n "${NETWORK_MODE:-}" ]; then
            echo -e "  ${BLUE}Network Mode:${NC} $NETWORK_MODE"
        fi
        if [ "${AUTO_ADMIN:-false}" = "true" ]; then
            echo -e "  ${BLUE}Auto-Admin Setup:${NC} Enabled (${AUTO_ADMIN_USERNAME:-admin})"
        fi
        
        # Display external storage paths if configured
        if [ -n "${EXTERNAL_MODELS_PATH:-}" ] || [ -n "${EXTERNAL_GCODE_PATH:-}" ] || [ -n "${EXTERNAL_PROFILES_PATH:-}" ] || [ -n "${EXTERNAL_APP_DATA_PATH:-}" ] || [ -n "${EXTERNAL_DATABASE_PATH:-}" ]; then
            echo -e "  ${BLUE}External Storage:${NC}"
            if [ -n "${EXTERNAL_MODELS_PATH:-}" ]; then
                echo -e "    • Models:    $EXTERNAL_MODELS_PATH"
            fi
            if [ -n "${EXTERNAL_GCODE_PATH:-}" ]; then
                echo -e "    • G-code:    $EXTERNAL_GCODE_PATH"
            fi
            if [ -n "${EXTERNAL_PROFILES_PATH:-}" ]; then
                echo -e "    • Profiles:  $EXTERNAL_PROFILES_PATH"
            fi
            if [ -n "${EXTERNAL_APP_DATA_PATH:-}" ]; then
                echo -e "    • App Data:  $EXTERNAL_APP_DATA_PATH"
            fi
            if [ -n "${EXTERNAL_DATABASE_PATH:-}" ]; then
                echo -e "    • Database:  $EXTERNAL_DATABASE_PATH"
            fi
        fi
        
        print_info "Previous settings will be used as defaults (press Enter to accept)"
        echo
        return 0
    fi
    return 1
}

# Save current configuration for future use
save_deployment_config() {
    print_header "💾 Saving Deployment Configuration"
    
    print_info "Saving configuration to $CONFIG_FILE for future deployments"
    
    # Decide which DB include flags to persist. Only persist flags for the
    # actively selected DB provider to avoid accidentally saving unrelated
    # database credentials or enabling other DB containers in future runs.
    SAVE_INCLUDE_POSTGRES=${INCLUDE_POSTGRES:-no}
    SAVE_INCLUDE_SQLSERVER=${INCLUDE_SQLSERVER:-no}
    SAVE_INCLUDE_MYSQL=${INCLUDE_MYSQL:-no}
    case "${DB_PROVIDER:-}" in
        postgres)
            SAVE_INCLUDE_POSTGRES=yes
            SAVE_INCLUDE_SQLSERVER=no
            SAVE_INCLUDE_MYSQL=no
            ;;
        sqlserver)
            SAVE_INCLUDE_POSTGRES=no
            SAVE_INCLUDE_SQLSERVER=yes
            SAVE_INCLUDE_MYSQL=no
            ;;
        mysql)
            SAVE_INCLUDE_POSTGRES=no
            SAVE_INCLUDE_SQLSERVER=no
            SAVE_INCLUDE_MYSQL=yes
            ;;
        *)
            # Leave provided values as-is for unknown/external providers
            ;;
    esac

    cat > "$CONFIG_FILE" << EOF
# PrintFarmer Deployment Configuration
# Generated on $(date)
# This file can be used for non-interactive deployments or as defaults for interactive mode
#
# Usage:
#   Interactive (uses these as defaults): ./scripts/deploy-docker.sh
#   Non-interactive (uses these exactly):  ./scripts/deploy-docker.sh --non-interactive
#   Dry-run:                               ./scripts/deploy-docker.sh --dry-run

# Architecture
ARCHITECTURE=$ARCHITECTURE
COMPOSE_FILE=$COMPOSE_FILE

# Database Configuration
DB_PROVIDER=$DB_PROVIDER
DB_PASSWORD=${DB_PASSWORD:-}

# Persist include flags only for the selected provider to avoid leaking
# unrelated DB credentials or enabling unintended DB containers later.
INCLUDE_POSTGRES=$SAVE_INCLUDE_POSTGRES
INCLUDE_SQLSERVER=$SAVE_INCLUDE_SQLSERVER
INCLUDE_MYSQL=$SAVE_INCLUDE_MYSQL
# Connection string (generic)
CONNECTION_STRING=$(printf '%q' "$CONNECTION_STRING")
# Network Configuration
ENABLE_DISCOVERY=$ENABLE_DISCOVERY
ALLOW_LOCAL_NETWORK=$ALLOW_LOCAL_NETWORK
NETWORK_RANGES=$(printf '%q' "$NETWORK_RANGES")
NETWORK_MODE=${NETWORK_MODE:-bridge}
HTTP_PORT=$HTTP_PORT
SERVER_HOST=${SERVER_HOST:-localhost}

# Application Settings - Pre-populate Setup Wizard  
PFARM__NetworkDiscovery__EnableDiscovery=${ENABLE_DISCOVERY}
PFARM__NetworkDiscovery__DiscoverySubnets=$(printf '%q' "$NETWORK_RANGES")
EOF

    # Persist provider-specific DB variables only for the selected provider
    case "${DB_PROVIDER:-}" in
        postgres)
            cat >> "$CONFIG_FILE" << EOF

# PostgreSQL Configuration
POSTGRES_DB=${POSTGRES_DB:-printfarmer}
POSTGRES_USER=${POSTGRES_USER:-postgres}
POSTGRES_PASSWORD=${POSTGRES_PASSWORD:-}
EOF
            ;;
        sqlserver)
            cat >> "$CONFIG_FILE" << EOF

# SQL Server Configuration
SQLSERVER_DB=${SQLSERVER_DB:-printfarmer}
SQLSERVER_PASSWORD=${SQLSERVER_PASSWORD:-}
SQLSERVER_PORT=${SQLSERVER_PORT:-1433}
SQLSERVER_EDITION=${SQLSERVER_EDITION:-Developer}
EOF
            ;;
        mysql)
            cat >> "$CONFIG_FILE" << EOF

# MySQL Configuration
MYSQL_DB=${MYSQL_DB:-printfarmer}
MYSQL_USER=${MYSQL_USER:-root}
MYSQL_PASSWORD=${MYSQL_PASSWORD:-}
EOF
            ;;
        *)
            # external or unknown provider: persist the generic connection string and leave provider-specifics out
            ;;
    esac

    if [ "$ARCHITECTURE" = "microservices" ]; then
        echo "API_PORT=$API_PORT" >> "$CONFIG_FILE"
    fi

    cat >> "$CONFIG_FILE" << EOF

# Application Settings
ENVIRONMENT=$ENVIRONMENT
ENABLE_SWAGGER=$ENABLE_SWAGGER
ENABLE_DETAILED_LOGGING=$ENABLE_DETAILED_LOGGING

# Observability & Monitoring Configuration
INCLUDE_MONITORING=${INCLUDE_MONITORING:-false}
INCLUDE_TELEMETRY=${INCLUDE_TELEMETRY:-false}
INCLUDE_SECURITY=${INCLUDE_SECURITY:-false}
INCLUDE_REGISTRY=${INCLUDE_REGISTRY:-false}
INCLUDE_DISCOVERY=${INCLUDE_DISCOVERY:-false}

# Distributed Slicing
ENABLE_DISTRIBUTED_SLICING=$ENABLE_DISTRIBUTED_SLICING
ENABLE_ORCA_WORKER=${ENABLE_ORCA_WORKER:-no}
ORCA_WORKER_COUNT=${ORCA_WORKER_COUNT:-0}
ORCA_HOST_PORT=${ORCA_HOST_PORT:-8081}
ORCASLICER_VERSION=${ORCASLICER_VERSION:-2.3.1}

EOF

    # (Prusa worker support removed) — no Prusa defaults are written for modern deployments

    if [ "$ARCHITECTURE" = "microservices" ] && [ "${OVERRIDE_WORKER_ENDPOINTS:-no}" = "yes" ]; then
        cat >> "$CONFIG_FILE" << EOF

# Worker Endpoints (Advanced)
OVERRIDE_WORKER_ENDPOINTS=yes
EOF
        [ "${ENABLE_ORCA_WORKER}" = "yes" ] && echo "ORCA_WORKER_ENDPOINT=${ORCA_WORKER_ENDPOINT}" >> "$CONFIG_FILE"

    fi

    if [ "${ENABLE_SPOOLMAN:-no}" = "yes" ]; then
        cat >> "$CONFIG_FILE" << EOF

# Spoolman Integration
ENABLE_SPOOLMAN=yes
SPOOLMAN_BASE_URL=$SPOOLMAN_BASE_URL
SPOOLMAN_PORT=${SPOOLMAN_PORT:-7912}

# Application Settings - Pre-populate Setup Wizard
PFARM__Spoolman__BaseUrl=$SPOOLMAN_BASE_URL
EOF
    else
        echo -e "\n# Spoolman Integration\nENABLE_SPOOLMAN=no" >> "$CONFIG_FILE"
    fi

    # Save external storage configuration (P0 Data Persistence)
    cat >> "$CONFIG_FILE" << EOF

# External Storage Configuration (P0 - Critical Data Persistence)
# Ensures 3D models and G-code files persist independently from container lifecycle
# Data only deleted when explicitly removing these directories
USE_EXTERNAL_STORAGE=${USE_EXTERNAL_STORAGE:-no}
EXTERNAL_MODELS_PATH=${EXTERNAL_MODELS_PATH:-}
EXTERNAL_GCODE_PATH=${EXTERNAL_GCODE_PATH:-}
EXTERNAL_PROFILES_PATH=${EXTERNAL_PROFILES_PATH:-}
EXTERNAL_APP_DATA_PATH=${EXTERNAL_APP_DATA_PATH:-}
EXTERNAL_DATABASE_PATH=${EXTERNAL_DATABASE_PATH:-}
EOF

    cat >> "$CONFIG_FILE" << EOF

# Initial Admin Setup (Optional)
# Set AUTO_ADMIN=true to automatically create admin user on deployment
# This skips the setup wizard in the UI
AUTO_ADMIN=${AUTO_ADMIN:-false}
AUTO_ADMIN_USERNAME=${AUTO_ADMIN_USERNAME:-admin}
AUTO_ADMIN_PASSWORD=${AUTO_ADMIN_PASSWORD:-}
AUTO_ADMIN_EMAIL=${AUTO_ADMIN_EMAIL:-admin@printfarmer.local}

# Operating System (detected)
OS=$OS

# Note: To use this configuration:
# 1. For interactive mode with these defaults: ./scripts/deploy-docker.sh
# 2. For non-interactive deployment:          ./scripts/deploy-docker.sh --non-interactive
# 3. To override specific values:             export VARIABLE=value && ./scripts/deploy-docker.sh --non-interactive
EOF

    chmod 600 "$CONFIG_FILE"
    print_success "Configuration saved to $CONFIG_FILE"
    print_info "Re-run script to use these settings, or edit file to customize"
}

# Detect OS and Docker environment
detect_environment() {
    print_header "🔍 Environment Detection"
    
    # Detect OS
    if [[ "$OSTYPE" == "linux-gnu"* ]]; then
        OS="linux"
        print_info "Detected Linux - Full Docker networking support available"
    elif [[ "$OSTYPE" == "darwin"* ]]; then
        OS="macos"
        print_warning "Detected macOS - Limited WiFi device access in Docker"
        print_warning "Consider using local development for active development"
    elif [[ "$OSTYPE" == "msys" ]] || [[ "$OSTYPE" == "win32" ]]; then
        OS="windows"
        print_info "Detected Windows - Good Docker support"
    else
        OS="unknown"
        print_warning "Unknown OS detected"
    fi
    
    # Check Docker
    if command -v docker &> /dev/null; then
        DOCKER_VERSION=$(docker --version | cut -d' ' -f3 | cut -d',' -f1)
        print_success "Docker found: $DOCKER_VERSION"
    else
        print_error "Docker not found! Please install Docker first."
        print_info "Visit: https://docs.docker.com/get-docker/"
        exit 1
    fi
    
    # Check Docker Compose
    if docker compose version &> /dev/null; then
        COMPOSE_VERSION=$(docker compose version | head -n1 | cut -d' ' -f4)
        print_success "Docker Compose found: $COMPOSE_VERSION"
    else
        print_error "Docker Compose not found! Please install Docker Compose."
        exit 1
    fi
    
    # Check if Docker is running
    if docker ps &> /dev/null; then
        print_success "Docker daemon is running"
    else
        print_error "Docker daemon is not running! Please start Docker."
        exit 1
    fi
}

# Check for .NET SDK and offer installation
check_dotnet_sdk() {
    echo
    print_info "Checking for .NET SDK..."
    
    if command -v dotnet &> /dev/null; then
        DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo "unknown")
        print_success ".NET SDK found: $DOTNET_VERSION"
        
        # Check if version meets minimum requirement (9.0)
        if [[ "$DOTNET_VERSION" =~ ^9\. ]] || [[ "$DOTNET_VERSION" =~ ^[1-9][0-9]+\. ]]; then
            print_success ".NET SDK version is compatible"
        else
            print_warning ".NET SDK version $DOTNET_VERSION detected"
            print_warning "PrintFarmer requires .NET 9.0 or later"
            print_info "Docker builds will still work, but local development may have issues"
        fi
    else
        print_warning ".NET SDK not found"
        print_info "While Docker deployment doesn't require .NET SDK on the host,"
        print_info "having it installed allows for local development and debugging."
        echo
        
        if [ "$NON_INTERACTIVE" = "true" ]; then
            print_info "Skipping .NET SDK installation in non-interactive mode"
            print_info "To install manually, visit: https://dotnet.microsoft.com/download"
            return 0
        fi
        
        prompt_yes_no "Would you like to install .NET SDK now?" "no" "INSTALL_DOTNET"
        
        if [ "$INSTALL_DOTNET" = "yes" ]; then
            install_dotnet_sdk
        else
            print_info "Continuing without .NET SDK installation"
            print_info "You can install it later from: https://dotnet.microsoft.com/download"
        fi
    fi
}

# Install .NET SDK using official installation script
install_dotnet_sdk() {
    print_header "📦 Installing .NET SDK"
    
    local install_script="dotnet-install.sh"
    local install_url="https://dot.net/v1/dotnet-install.sh"
    
    # Download installation script
    print_info "Downloading .NET installation script..."
    if command -v curl &> /dev/null; then
        curl -fsSL "$install_url" -o "$install_script"
    elif command -v wget &> /dev/null; then
        wget -q "$install_url" -O "$install_script"
    else
        print_error "Neither curl nor wget found. Cannot download .NET installer."
        print_info "Please install .NET manually: https://dotnet.microsoft.com/download"
        return 1
    fi
    
    if [ ! -f "$install_script" ]; then
        print_error "Failed to download .NET installation script"
        return 1
    fi
    
    chmod +x "$install_script"
    print_success "Installation script downloaded"
    
    # Install .NET SDK 9.0 (required version)
    print_info "Installing .NET SDK 9.0..."
    print_info "This may take a few minutes..."
    
    if [ "$OS" = "windows" ]; then
        print_warning "Automated .NET installation not supported on Windows"
        print_info "Please download and install from: https://dotnet.microsoft.com/download"
        print_info "After installation, re-run this script"
        rm -f "$install_script"
        exit 1
    fi
    
    # Run installation script
    if ./"$install_script" --channel 9.0 --install-dir "$HOME/.dotnet"; then
        print_success ".NET SDK 9.0 installed successfully"
        
        # Add to PATH for current session
        export PATH="$HOME/.dotnet:$PATH"
        export DOTNET_ROOT="$HOME/.dotnet"
        
        # Provide instructions for permanent PATH setup
        echo
        print_info "To make .NET available in future sessions, add to your shell profile:"
        echo
        if [ "$OS" = "macos" ]; then
            echo "  echo 'export PATH=\"\$HOME/.dotnet:\$PATH\"' >> ~/.zshrc"
            echo "  echo 'export DOTNET_ROOT=\"\$HOME/.dotnet\"' >> ~/.zshrc"
        else
            echo "  echo 'export PATH=\"\$HOME/.dotnet:\$PATH\"' >> ~/.bashrc"
            echo "  echo 'export DOTNET_ROOT=\"\$HOME/.dotnet\"' >> ~/.bashrc"
        fi
        echo
        
        # Verify installation
        if command -v dotnet &> /dev/null; then
            DOTNET_VERSION=$(dotnet --version)
            print_success "Verified: .NET SDK $DOTNET_VERSION is now available"
        else
            print_warning "Installation completed but 'dotnet' command not found in PATH"
            print_info "You may need to start a new terminal session"
        fi
        
        # Clean up
        rm -f "$install_script"
    else
        print_error ".NET SDK installation failed"
        print_info "Please install manually: https://dotnet.microsoft.com/download"
        rm -f "$install_script"
        return 1
    fi
}

# Choose deployment architecture
choose_architecture() {
    # Check if architecture was specified via CLI
    if [ -n "${CLI_ARCHITECTURE:-}" ]; then
        case "$CLI_ARCHITECTURE" in
            monolithic|mono)
                ARCHITECTURE="monolithic"
                ENV_FILE=".env"
                COMPOSE_FILE="docker-compose.yml"
                print_success "Using CLI option: Monolithic deployment"
                check_dotnet_sdk
                return 0
                ;;
            microservices|micro)
                ARCHITECTURE="microservices"
                ENV_FILE=".env"
                COMPOSE_FILE="docker-compose.yml"
                print_success "Using CLI option: Microservices deployment"
                return 0
                ;;
            *)
                print_error "Invalid architecture: $CLI_ARCHITECTURE"
                print_info "Valid options: monolithic, microservices"
                exit 1
                ;;
        esac
    fi
    
    # In non-interactive mode, use defaults if architecture already loaded from config
    if [ "$NON_INTERACTIVE" = "true" ] && [ -n "${ARCHITECTURE:-}" ]; then
        print_info "Using configured architecture: $ARCHITECTURE"
        return 0
    fi
    
    print_header "🏗️  Deployment Architecture"
    
    echo -e "${BLUE}PrintFarmer supports two deployment architectures:${NC}"
    echo
    echo -e "${GREEN}1. Monolithic (Recommended)${NC}"
    echo "   • Single container with API + Web frontend"
    echo "   • Simpler configuration and networking"
    echo "   • Good for most deployments"
    echo "   • Uses SQLite database by default"
    echo "   • Built with multi-stage Docker builds for efficiency"
    echo
    echo -e "${GREEN}2. Microservices (Advanced)${NC}"
    echo "   • Separate containers for API, Web, Database"
    echo "   • Enhanced networking capabilities"
    echo "   • Better for large-scale deployments"
    echo "   • Supports PostgreSQL, SQL Server, MySQL"
    echo "   • Built with multi-stage Docker builds for efficiency"
    echo
    
    # Use previous architecture as default, or "1" for new deployments
    local default_choice="1"
    if [ "${ARCHITECTURE:-}" = "microservices" ]; then
        default_choice="2"
    fi
    
    prompt_with_default "Choose architecture [1=Monolithic, 2=Microservices]:" "$default_choice" "ARCH_CHOICE"
    
    case "$ARCH_CHOICE" in
        1|monolithic|mono)
            ARCHITECTURE="monolithic"
            ENV_FILE=".env"
            COMPOSE_FILE="docker-compose.yml"
            print_success "Selected: Monolithic deployment (with multi-stage builds)"
            
            # Check .NET SDK for monolithic (optional but recommended for local builds)
            check_dotnet_sdk
            ;;
        2|microservices|micro)
            ARCHITECTURE="microservices"
            ENV_FILE=".env"
            COMPOSE_FILE="docker-compose.yml"
            print_success "Selected: Microservices deployment (with multi-stage builds)"
            ;;
        *)
            print_error "Invalid choice. Please run the script again."
            exit 1
            ;;
    esac
}

# Utility: check if a string is a positive integer
is_positive_int() {
    [[ "$1" =~ ^[0-9]+$ ]] && [ "$1" -ge 0 ]
}

# Utility: check if TCP port is already in use on host
port_in_use() {
    local port=$1
    # Try lsof first, fallback to netstat / ss
    if command -v lsof >/dev/null 2>&1; then
        lsof -Pi :"$port" -sTCP:LISTEN -t >/dev/null 2>&1 && return 0 || return 1
    elif command -v ss >/dev/null 2>&1; then
        ss -ltn | awk '{print $4}' | grep -E "(:|\.)$port$" >/dev/null 2>&1 && return 0 || return 1
    else
        netstat -an 2>/dev/null | grep -E "LISTEN|TCP" | grep -E "[:\.]$port[[:space:]]" >/dev/null 2>&1 && return 0 || return 1
    fi
}

# Find next free port starting from given number
find_next_free_port() {
    local start=$1
    local p=$start
    local limit=$((start+200)) # safeguard loop
    while [ $p -le $limit ]; do
        if ! port_in_use "$p"; then
            echo "$p"
            return 0
        fi
        p=$((p+1))
    done
    echo "$start" # fallback
    return 1
}

# Validate configuration & enforce safe constraints (ports, scaling, numeric values)
validate_configuration() {
    print_header "🧪 Validating Configuration"

    # Validate numeric worker counts
    for var in ORCA_WORKER_COUNT; do
        val=${!var:-0}
        if ! is_positive_int "$val"; then
            print_warning "Invalid value '$val' for $var. Resetting to 1."
            eval "$var=1"
        fi
    done

    # If distributed slicing disabled, zero out counts
    if [ "${ENABLE_DISTRIBUTED_SLICING:-false}" != "true" ]; then
        ORCA_WORKER_COUNT=0
    fi

    # Monolithic constraints: host networking -> only one instance per worker due to fixed ports 8081/8082
    if [ "$ARCHITECTURE" = "monolithic" ]; then
        if [ "$ORCA_WORKER_COUNT" -gt 1 ]; then
            print_warning "Monolithic mode: Cannot scale OrcaSlicer workers (host networking / fixed port 8081). For scaling, use microservices. Forcing count=1."
            ORCA_WORKER_COUNT=1
        fi

    fi

    # Automatic port suggestion helper
    suggest_port_replacement() {
        local var_name=$1
        local current_val=$2
        local description=$3
        local new_port
        new_port=$(find_next_free_port $((current_val+1)))
        if [ "$new_port" != "$current_val" ]; then
            print_warning "$description port $current_val is in use. Suggested free port: $new_port"
            if [ "$NON_INTERACTIVE" = "true" ]; then
                # Auto-accept suggestion in non-interactive mode
                eval "$var_name=$new_port"
                print_info "[non-interactive] $description port remapped $current_val -> $new_port"
            else
                prompt_yes_no "Use suggested port $new_port instead of $current_val?" "yes" USE_REPLACEMENT
                if [ "$USE_REPLACEMENT" = "yes" ]; then
                    eval "$var_name=$new_port"
                    print_success "$description port changed to $new_port"
                else
                    print_warning "Keeping original $description port $current_val (may fail on startup)."
                fi
            fi
        else
            print_warning "$description port $current_val is in use and no alternative found within range."
        fi
    }

    # Port availability checks with optional remapping
    if [ -n "${HTTP_PORT:-}" ] && port_in_use "$HTTP_PORT"; then
        suggest_port_replacement HTTP_PORT "$HTTP_PORT" "HTTP"
    fi
    if [ "$ARCHITECTURE" = "microservices" ] && [ -n "${API_PORT:-}" ] && port_in_use "$API_PORT"; then
        suggest_port_replacement API_PORT "$API_PORT" "API"
    fi

    # Worker ports in monolithic (8081 / 8082). Only warn if corresponding worker enabled.
    # Worker port handling
    ORCA_HOST_PORT=${ORCA_HOST_PORT:-8081}
    if [ "$ARCHITECTURE" = "monolithic" ]; then
        # Only warn; cannot remap easily due to fixed host network & static internal ports
        if [ "$ENABLE_ORCA_WORKER" = "yes" ] && port_in_use "$ORCA_HOST_PORT"; then
            print_warning "Monolithic: Orca worker port $ORCA_HOST_PORT in use; startup may fail."
        fi

    else
        # Allow remap for microservices (we will rely on variable interpolation in compose file)
        if [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -gt 0 ] && port_in_use "$ORCA_HOST_PORT"; then
            suggest_port_replacement ORCA_HOST_PORT "$ORCA_HOST_PORT" "Orca worker"
        fi

    fi

    # Logical consistency: worker enabled but count 0 -> adjust to 1
    if [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -eq 0 ]; then
        print_warning "ENABLE_ORCA_WORKER=yes but ORCA_WORKER_COUNT=0. Setting count=1."
        ORCA_WORKER_COUNT=1
    fi


    # If distributed slicing disabled but workers were enabled by mistake
    if [ "$ENABLE_DISTRIBUTED_SLICING" != "true" ] && [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
        print_warning "Workers enabled but distributed slicing disabled. Forcing workers off."
        ENABLE_ORCA_WORKER=no
        ORCA_WORKER_COUNT=0
    fi

    print_success "Validation complete."
}

# Configure database settings
configure_database() {
    # In non-interactive mode, use pre-loaded config if available
    if [ "$NON_INTERACTIVE" = "true" ] && [ -n "${DB_PROVIDER:-}" ]; then
        # Validate SQLite is only used with monolithic architecture
        if [ "${DB_PROVIDER:-}" = "sqlite" ] && [ "$ARCHITECTURE" != "monolithic" ]; then
            print_error "SQLite can only be used with monolithic architecture, not $ARCHITECTURE"
            print_error "Please use postgres, sqlserver, or mysql for $ARCHITECTURE deployments"
            exit 1
        fi
        print_info "Using configured database: $DB_PROVIDER"
        return 0
    fi
    
    print_header "💾 Database Configuration"
    
    if [ "$ARCHITECTURE" = "monolithic" ]; then
        echo -e "${BLUE}Monolithic deployment supports:${NC}"
        echo "1. SQLite (recommended) - No additional setup"
        echo "2. External database - Requires separate setup"
        echo
        
        # Map DB_PROVIDER to menu choice number for default
        local default_choice="1"
        case "${DB_PROVIDER:-sqlite}" in
            sqlite) default_choice="1" ;;
            postgres|sqlserver|mysql) default_choice="2" ;;
        esac
        
        prompt_with_default "Choose database [1=SQLite, 2=External]:" "$default_choice" "DB_CHOICE"
        
        case "$DB_CHOICE" in
            1|sqlite|SQLite)
                DB_PROVIDER="sqlite"
                CONNECTION_STRING="Data Source=/data/farm.db"
                print_success "Using SQLite - Data will persist in Docker volume"
                ;;
            2|external|External|postgres|sqlserver|mysql)
                # If user selected 2 but we don't have a previous provider, ask which one
                if [ "$DB_CHOICE" = "2" ] || [ "$DB_CHOICE" = "external" ] || [ "$DB_CHOICE" = "External" ]; then
                    local prev_external="${DB_PROVIDER:-postgres}"
                    [ "$prev_external" = "sqlite" ] && prev_external="postgres"
                    prompt_with_default "External database type [postgres/sqlserver/mysql]:" "$prev_external" "DB_PROVIDER"
                fi
                
                case "$DB_PROVIDER" in
                    postgres)
                        prompt_with_default "PostgreSQL connection string:" "Host=your-postgres-host;Database=printfarmer;Username=postgres;Password=your-password" "CONNECTION_STRING"
                        ;;
                    sqlserver)
                        prompt_with_default "SQL Server connection string:" "Server=your-sql-server;Database=printfarmer;User Id=sa;Password=YourStrong!Password;TrustServerCertificate=True;" "CONNECTION_STRING"
                        ;;
                    mysql)
                        prompt_with_default "MySQL connection string:" "Server=your-mysql-host;Database=printfarmer;User=root;Password=your-password;" "CONNECTION_STRING"
                        ;;
                    *)
                        print_warning "Unknown database type, using PostgreSQL as fallback"
                        DB_PROVIDER="postgres"
                        prompt_with_default "PostgreSQL connection string:" "Host=your-postgres-host;Database=printfarmer;Username=postgres;Password=your-password" "CONNECTION_STRING"
                        ;;
                esac
                ;;
            *)
                print_warning "Unknown choice, using PostgreSQL as fallback"
                DB_PROVIDER="postgres"
                prompt_with_default "PostgreSQL connection string:" "Host=your-postgres-host;Database=printfarmer;Username=postgres;Password=your-password" "CONNECTION_STRING"
                ;;
        esac
    else
        echo -e "${BLUE}Microservices deployment supports:${NC}"
        echo "1. PostgreSQL (recommended) - Included container"
        echo "2. SQL Server - Included container"
        echo "3. MySQL - Included container"
        echo "4. External database - Your own database server"
        echo
        
        # Map DB_PROVIDER to menu choice number for default
        local default_choice="1"
        case "${DB_PROVIDER:-postgres}" in
            postgres) default_choice="1" ;;
            sqlserver) default_choice="2" ;;
            mysql) default_choice="3" ;;
            external) default_choice="4" ;;
        esac
        
        prompt_with_default "Choose database [1=PostgreSQL, 2=SQL Server, 3=MySQL, 4=External]:" "$default_choice" "DB_CHOICE"
        
        case "$DB_CHOICE" in
            1|postgres|PostgreSQL)
                DB_PROVIDER="postgres"
                prompt_with_default "PostgreSQL database name:" "${POSTGRES_DB:-printfarmer}" "POSTGRES_DB"
                prompt_with_default "PostgreSQL username:" "${POSTGRES_USER:-postgres}" "POSTGRES_USER"
                # If no password provided yet, generate a secure random password so interactive prompt shows it as the default
                if [ -z "${POSTGRES_PASSWORD:-}" ]; then
                    POSTGRES_PASSWORD=$(generate_random_password)
                    # Do not overwrite an explicitly provided DB_PASSWORD variable
                    DB_PASSWORD=${DB_PASSWORD:-$POSTGRES_PASSWORD}
                fi
                prompt_with_default "PostgreSQL password:" "${POSTGRES_PASSWORD:-postgres}" "POSTGRES_PASSWORD"
                DB_PASSWORD="$POSTGRES_PASSWORD"
                CONNECTION_STRING="Host=postgres;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
                INCLUDE_POSTGRES="yes"
                ;;
            2|sqlserver|"SQL Server")
                DB_PROVIDER="sqlserver"
                echo
                echo -e "${BLUE}SQL Server Edition:${NC}"
                echo "1. Developer - Free, full-featured (recommended for development/testing)"
                echo "2. Express - Free, limited features (10GB max, production-ready)"
                echo "3. Standard - Commercial license required"
                echo "4. Enterprise - Commercial license required"
                echo
                prompt_with_default "Choose SQL Server edition [1=Developer, 2=Express, 3=Standard, 4=Enterprise]:" "${SQLSERVER_EDITION:-1}" "SQLSERVER_EDITION_CHOICE"
                
                case "$SQLSERVER_EDITION_CHOICE" in
                    1|developer|Developer)
                        SQLSERVER_EDITION="Developer"
                        ;;
                    2|express|Express)
                        SQLSERVER_EDITION="Express"
                        ;;
                    3|standard|Standard)
                        SQLSERVER_EDITION="Standard"
                        print_warning "Standard edition requires a valid SQL Server license"
                        ;;
                    4|enterprise|Enterprise)
                        SQLSERVER_EDITION="Enterprise"
                        print_warning "Enterprise edition requires a valid SQL Server license"
                        ;;
                    *)
                        SQLSERVER_EDITION="Developer"
                        print_info "Using Developer edition as default"
                        ;;
                esac
                
                print_info "Using SQL Server $SQLSERVER_EDITION edition"
                echo
                prompt_with_default "SQL Server database name:" "${SQLSERVER_DB:-printfarmer}" "SQLSERVER_DB"
                # Pre-generate SQL Server SA password if none exists so interactive prompt shows a secure default
                if [ -z "${SQLSERVER_PASSWORD:-}" ]; then
                    SQLSERVER_PASSWORD=$(generate_random_password)
                    DB_PASSWORD=${DB_PASSWORD:-$SQLSERVER_PASSWORD}
                fi
                prompt_with_default "SQL Server SA password:" "${SQLSERVER_PASSWORD:-YourStrong!Password123}" "SQLSERVER_PASSWORD"
                prompt_with_default "SQL Server host port (1433 is default, use different if port conflict):" "${SQLSERVER_PORT:-1433}" "SQLSERVER_PORT"
                DB_PASSWORD="$SQLSERVER_PASSWORD"
                CONNECTION_STRING="Server=sqlserver;Database=$SQLSERVER_DB;User Id=sa;Password=$SQLSERVER_PASSWORD;TrustServerCertificate=True;"
                INCLUDE_SQLSERVER="yes"
                ;;
            3|mysql|MySQL)
                DB_PROVIDER="mysql"
                prompt_with_default "MySQL database name:" "${MYSQL_DB:-printfarmer}" "MYSQL_DB"
                prompt_with_default "MySQL username:" "${MYSQL_USER:-root}" "MYSQL_USER"
                # Pre-generate MySQL password if none exists so interactive prompt shows a secure default
                if [ -z "${MYSQL_PASSWORD:-}" ]; then
                    MYSQL_PASSWORD=$(generate_random_password)
                    DB_PASSWORD=${DB_PASSWORD:-$MYSQL_PASSWORD}
                fi
                prompt_with_default "MySQL password:" "${MYSQL_PASSWORD:-example}" "MYSQL_PASSWORD"
                DB_PASSWORD="$MYSQL_PASSWORD"
                CONNECTION_STRING="Server=mysql;Database=$MYSQL_DB;User=$MYSQL_USER;Password=$MYSQL_PASSWORD;"
                INCLUDE_MYSQL="yes"
                ;;
            4|external|External)
                prompt_with_default "External database provider [postgres/sqlserver/mysql]:" "postgres" "EXT_DB_TYPE"
                prompt_with_default "Database host:" "your-host" "EXT_DB_HOST"
                prompt_with_default "Database name:" "printfarmer" "EXT_DB_NAME"
                prompt_with_default "Database username:" "user" "EXT_DB_USER"
                prompt_with_default "Database password:" "password" "EXT_DB_PASSWORD"
                
                case "$EXT_DB_TYPE" in
                    postgres)
                        CONNECTION_STRING="Host=$EXT_DB_HOST;Database=$EXT_DB_NAME;Username=$EXT_DB_USER;Password=$EXT_DB_PASSWORD"
                        ;;
                    sqlserver)
                        CONNECTION_STRING="Server=$EXT_DB_HOST;Database=$EXT_DB_NAME;User Id=$EXT_DB_USER;Password=$EXT_DB_PASSWORD;TrustServerCertificate=True;"
                        ;;
                    mysql)
                        CONNECTION_STRING="Server=$EXT_DB_HOST;Database=$EXT_DB_NAME;User=$EXT_DB_USER;Password=$EXT_DB_PASSWORD;"
                        ;;
                esac
                DB_PROVIDER="$EXT_DB_TYPE"
                ;;
            *)
                print_warning "Unknown choice, using PostgreSQL as fallback"
                DB_PROVIDER="postgres"
                POSTGRES_DB="printfarmer"
                POSTGRES_USER="postgres"
                POSTGRES_PASSWORD="postgres"
                DB_PASSWORD="postgres"
                CONNECTION_STRING="Host=postgres;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
                INCLUDE_POSTGRES="yes"
                ;;
        esac
    fi
}

# Configure networking
configure_networking() {
    # In non-interactive mode, use pre-loaded config if available
    if [ "$NON_INTERACTIVE" = "true" ] && [ -n "${NETWORK_MODE:-}" ]; then
        print_info "Using configured network mode: $NETWORK_MODE"
        return 0
    fi
    
    print_header "🌐 Network Configuration"
    
    # For microservices, all services run on the docker bridge network for service discovery by hostname
    # Printer discovery runs on host network to enable local network scanning
    if [ "$ARCHITECTURE" = "microservices" ]; then
        print_success "Microservices architecture: all services on bridge network with service discovery"
        NETWORK_MODE="bridge"
        print_info "API will be accessible at http://api:5245 within the docker network"
        print_info "Printer discovery service runs on host network for local network scanning"
    else
        # For monolithic, allow user to choose network mode
        echo -e "${BLUE}Network Mode for Container:${NC}"
        echo -e "  ${BLUE}1.${NC} Bridge (default) - Works on all platforms, limited broadcast/multicast"
        echo -e "  ${BLUE}2.${NC} Host (advanced) - Direct host network access, full discovery support"
        echo
        
        if [ "$OS" != "linux" ]; then
            print_warning "Host network mode only works on Linux."
            print_warning "Current OS: $OS (detected)"
            echo
            prompt_yes_no "Are you deploying to a Linux server (not this machine)?" "no" "DEPLOYING_TO_LINUX"
            
            if [ "$DEPLOYING_TO_LINUX" = "yes" ]; then
                print_info "Generating configuration for Linux target deployment"
                echo -e "${YELLOW}Host mode provides optimal network discovery (broadcast/multicast).${NC}"
                echo -e "${YELLOW}Bridge mode works for known IP addresses but may miss auto-discovery.${NC}"
                echo
                prompt_with_default "Network mode [1=Bridge, 2=Host]:" "2" "NETWORK_MODE_CHOICE"
                
                case "$NETWORK_MODE_CHOICE" in
                    2|host|Host)
                        NETWORK_MODE="host"
                        print_success "Using host network mode for full discovery support"
                        print_info "Container will bind to port ${API_PORT:-5245} on the host"
                        ;;
                    *)
                        NETWORK_MODE="bridge"
                        print_info "Using bridge mode (cross-platform compatible)"
                        ;;
                esac
            else
                print_info "Forcing bridge mode for $OS deployment"
                NETWORK_MODE="bridge"
            fi
        else
            echo -e "${YELLOW}Host mode provides optimal network discovery (broadcast/multicast).${NC}"
            echo -e "${YELLOW}Bridge mode works for known IP addresses but may miss auto-discovery.${NC}"
            echo
            prompt_with_default "Network mode [1=Bridge, 2=Host]:" "2" "NETWORK_MODE_CHOICE"
            
            case "$NETWORK_MODE_CHOICE" in
                2|host|Host)
                    NETWORK_MODE="host"
                    print_success "Using host network mode for full discovery support"
                    print_info "Container will bind to port ${API_PORT:-5245} on the host"
                    ;;
                *)
                    NETWORK_MODE="bridge"
                    print_info "Using bridge mode (cross-platform compatible)"
                    ;;
            esac
        fi
    fi
    
    echo
    echo -e "${BLUE}Configure external access:${NC}"
    prompt_with_default "HTTP port for web access:" "8080" "HTTP_PORT"
    
    # Warn about port 80 requiring elevated privileges
    if [ "$HTTP_PORT" = "80" ] && [ "$OS" = "linux" ]; then
        print_warning "Port 80 requires elevated privileges. Docker must be running with proper permissions."
        print_info "If containers fail to start, consider using port 8080 or run with: sudo docker compose ..."
    fi
    
    if [ "$ARCHITECTURE" = "microservices" ]; then
        prompt_with_default "API port (for direct API access):" "5245" "API_PORT"
    fi
}

# Adjust connection strings for network mode
adjust_connection_strings_for_network_mode() {
    # In host network mode, services need to connect to localhost instead of service names
    if [ "$NETWORK_MODE" = "host" ]; then
        print_header "🔧 Adjusting Configuration for Host Network Mode"
        
        print_info "Host network mode requires using localhost for database connections"
        
        # Adjust connection string based on database provider
        case "$DB_PROVIDER" in
            postgres)
                # PostgreSQL: Change from "Host=postgres" to "Host=localhost"
                CONNECTION_STRING="Host=localhost;Database=${POSTGRES_DB:-printfarmer};Username=${POSTGRES_USER:-postgres};Password=${POSTGRES_PASSWORD:-}"
                print_success "PostgreSQL connection string updated for host networking"
                ;;
            sqlserver)
                # SQL Server: Change from "Server=sqlserver" to "Server=localhost,PORT"
                CONNECTION_STRING="Server=localhost,${SQLSERVER_PORT:-1433};Database=${SQLSERVER_DB:-printfarmer};User Id=sa;Password=${SQLSERVER_PASSWORD:-};TrustServerCertificate=True;"
                print_success "SQL Server connection string updated for host networking (port ${SQLSERVER_PORT:-1433})"
                ;;
            mysql)
                # MySQL: Change from "Server=mysql" to "Server=localhost"
                CONNECTION_STRING="Server=localhost;Database=${MYSQL_DB:-printfarmer};User=${MYSQL_USER:-root};Password=${MYSQL_PASSWORD:-};"
                print_success "MySQL connection string updated for host networking"
                ;;
        esac
        
        print_info "Database will be accessible at localhost:${SQLSERVER_PORT:-5432}"
        
        # Also generate a custom Nginx config for frontend to proxy to localhost API
        generate_host_network_nginx_config
    fi
}

# Generate Nginx config and Dockerfile for host network mode
# In host mode, frontend (bridge network) must proxy to host.docker.internal:API_PORT instead of api:5001
generate_host_network_nginx_config() {
    print_info "Generating Nginx config for host network mode..."
    
    mkdir -p deploy/nginx/conf.d.host
    
    # Create the custom Nginx config with host.docker.internal and actual API port
    cat > deploy/nginx/conf.d.host/frontend-app.conf << NGINXEOF
server {
    listen ${HTTP_PORT:-8080};
    server_name localhost;
    root /usr/share/nginx/html;
    index index.html;

    # Cache static assets (immutable build output)
    location ~* \.(js|css|png|jpg|jpeg|gif|ico|svg|woff|woff2|ttf|eot)$ {
        expires 1y;
        add_header Cache-Control "public, immutable";
    }

    # Dedicated health check endpoint
    location /health {
        access_log off;
        default_type text/plain;
        add_header Cache-Control "no-cache, no-store, must-revalidate" always;
        return 200 "OK\n";
    }

    # Explicit index.html handling
    location = /index.html {
        add_header Cache-Control "no-cache, no-store, must-revalidate" always;
        add_header Pragma "no-cache" always;
        add_header Expires "0" always;
        try_files /index.html =404;
    }

    # SPA routing fallback
    location / {
        try_files \$uri \$uri/ /index.html;
        add_header Cache-Control "no-cache, no-store, must-revalidate" always;
        add_header Pragma "no-cache" always;
        add_header Expires "0" always;
    }

    # Proxy API requests (HOST MODE: API is on host network, accessible via host.docker.internal)
    location ^~ /api/ {
        proxy_pass http://host.docker.internal:${API_PORT:-5245};
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host \$host;
        proxy_set_header X-Forwarded-Port \$server_port;
    }

    # Proxy SignalR hub (WebSockets & long polling)
    location ^~ /hubs/ {
        proxy_pass http://host.docker.internal:${API_PORT:-5245};
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "Upgrade";
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host \$host;
        proxy_set_header X-Forwarded-Port \$server_port;
        proxy_read_timeout 600s;
        proxy_send_timeout 600s;
    }

    # Security headers
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-XSS-Protection "1; mode=block" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;
}
# Close the here-doc for the generated Nginx config
NGINXEOF

# Start an nginx proxy container in host network mode using the generated host config
start_host_mode_nginx_proxy() {
    print_info "Ensuring host-mode nginx proxy is running..."

    # If a container with the expected name exists and is running, nothing to do
    if docker ps --format '{{.Names}}' | grep -q '^printfarmer-nginx-proxy$'; then
        print_info "Host-mode nginx proxy container already running: printfarmer-nginx-proxy"
        return 0
    fi

    # Build a lightweight nginx image that uses the generated host configs
    # Use a temporary Dockerfile to reference the generated confs
    local tmp_dockerfile=".tmp.Dockerfile.nginx.host"
    cat > "$tmp_dockerfile" <<EOF
FROM nginx:alpine
COPY deploy/nginx/nginx-frontend.conf /etc/nginx/nginx.conf
COPY deploy/nginx/conf.d.host/*.conf /etc/nginx/conf.d/
RUN rm -f /etc/nginx/conf.d/default.conf || true
EOF

    local image_name="printfarmer-nginx-host:latest"
    if docker build -t "$image_name" -f "$tmp_dockerfile" .; then
        print_success "Built host-mode nginx image: $image_name"
    else
        print_warning "Failed to build host-mode nginx image; attempting to run nginx:alpine with mounted config"
        image_name="nginx:alpine"
    fi

    rm -f "$tmp_dockerfile" || true

    # Ensure host HTTP port is available before attempting to start nginx.
    local http_port="${HTTP_PORT:-8080}"
    # If the port is bound, try to identify a Docker container that owns it and stop it (with prompt)
    if ss -ltn "sport = :${http_port}" >/dev/null 2>&1; then
        # Try to find a container that exposes this host port
        occupier=$(docker ps --format '{{.Names}}\t{{.Ports}}' | grep ":${http_port}->" | awk -F'\t' '{print $1}' | head -1 || true)
        if [ -n "${occupier}" ]; then
            print_warning "Host port ${http_port} is already bound by container: ${occupier}"
            # If non-interactive, stop it automatically; otherwise ask the user
            if [ "${NON_INTERACTIVE:-false}" = "true" ]; then
                print_info "Non-interactive mode: stopping container ${occupier} to free port ${http_port}"
                docker stop "${occupier}" || true
                docker rm -f "${occupier}" || true
            else
                prompt_yes_no "Host port ${http_port} is in use by container ${occupier}. Stop it so nginx can bind ${http_port}?" "n" "STOP_FRONTEND_CONFIRM"
                if [ "${STOP_FRONTEND_CONFIRM:-no}" = "yes" ]; then
                    print_info "Stopping container ${occupier} as requested"
                    docker stop "${occupier}" || true
                    docker rm -f "${occupier}" || true
                else
                    print_error "Cannot start nginx proxy because port ${http_port} is in use by ${occupier}. Aborting start_host_mode_nginx_proxy."
                    return 1
                fi
            fi
        else
            print_error "Host port ${http_port} appears in use by a non-container process. Please free it and retry."
            return 1
        fi
    fi

    # Remove an existing container with the same name if present but not running
    if docker ps -a --format '{{.Names}}' | grep -q '^printfarmer-nginx-proxy$'; then
        print_info "Removing stale nginx proxy container"
        docker rm -f printfarmer-nginx-proxy || true
    fi

    # Run the container in host network mode so it binds the requested HTTP port on the host
    # Use --network host for Linux; on macOS/Docker Desktop this is a no-op and will fall back to bridge
    # Wait for the API to be available on the host before starting nginx
    if ! wait_for_host_api; then
        print_warning "API did not become available; continuing to attempt starting nginx but proxy may fail."
    fi

    if docker run -d --name printfarmer-nginx-proxy --network host \
        -v "${PWD}/deploy/nginx/conf.d.host:/etc/nginx/conf.d:ro" \
        -v "${PWD}/deploy/nginx/nginx-frontend.conf:/etc/nginx/nginx.conf:ro" \
        "$image_name" >/dev/null; then
        print_success "Started host-mode nginx proxy: printfarmer-nginx-proxy"
        if validate_nginx_proxy; then
            return 0
        else
            print_error "Nginx proxy started but failed validation"
            return 1
        fi
    else
        print_warning "Failed to start host-mode nginx proxy container"
        print_info "Attempting fallback: start nginx image with port mapping"
        # Fallback: start default nginx with explicit port mapping (best-effort)
        # Wait again for API before starting fallback mapping
        if ! wait_for_host_api; then
            print_warning "API did not become available; fallback nginx may fail to proxy."
        fi

        if docker run -d --name printfarmer-nginx-proxy --add-host=host.docker.internal:host-gateway -p "${HTTP_PORT:-8080}:80" \
            -v "${PWD}/deploy/nginx/conf.d.host:/etc/nginx/conf.d:ro" \
            -v "${PWD}/deploy/nginx/nginx-frontend.conf:/etc/nginx/nginx.conf:ro" \
            nginx:alpine >/dev/null; then
            print_success "Started nginx-proxy (fallback port mapped): printfarmer-nginx-proxy"
            if validate_nginx_proxy; then
                return 0
            else
                print_error "Nginx proxy (fallback) started but failed validation"
                return 1
            fi
        else
            print_error "Unable to start an nginx-proxy container in host or mapped mode. Please start one manually or check Docker permissions."
            return 1
        fi
    fi
}

# Helper function: Wait for the host API port to be ready before launching nginx
# This prevents nginx startup failures and 502s when proxying to an unreachable upstream
wait_for_host_api() {
    local host_port=${API_PORT:-5245}
    local timeout_seconds=${API_WAIT_TIMEOUT:-60}
    local interval=2
    local waited=0
    print_info "Waiting up to ${timeout_seconds}s for API to accept connections on host port ${host_port}..."
    while ! ss -ltn "sport = :${host_port}" >/dev/null 2>&1; do
        if [ "$waited" -ge "$timeout_seconds" ]; then
            print_warning "Timeout waiting for API on host port ${host_port} after ${timeout_seconds}s"
            return 1
        fi
        sleep $interval
        waited=$((waited + interval))
    done
    print_success "API is listening on host port ${host_port}"
    return 0
}

# Helper function: Validate that the nginx proxy is correctly proxying to the API
# Queries the health endpoint through the proxy. Returns 0 on success.
validate_nginx_proxy() {
    local port=${HTTP_PORT:-8080}
    local timeout=${API_WAIT_TIMEOUT:-60}
    local interval=2
    local waited=0
    print_info "Validating nginx proxy is responding at http://localhost:${port}/healthz ..."
    while true; do
        # Use --max-time to avoid long hangs; accept 200 OK
        if curl -sS --max-time 5 -f "http://localhost:${port}/healthz" >/dev/null 2>&1; then
            print_success "Nginx proxy validated: /healthz returned 200 via proxy"
            return 0
        fi
        if [ "$waited" -ge "$timeout" ]; then
            print_error "Nginx proxy validation failed after ${timeout}s"
            # Dump some useful debug info
            print_info "--- nginx-proxy logs (last 200 lines) ---"
            docker logs printfarmer-nginx-proxy --tail 200 || true
            print_info "--- nginx config (if container running) ---"
            if docker ps --format '{{.Names}}' | grep -q '^printfarmer-nginx-proxy$'; then
                docker exec printfarmer-nginx-proxy nginx -T 2>/dev/null | sed -n '1,200p' || true
            fi
            print_info "--- docker ps snapshot ---"
            docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}' || true
            return 1
        fi
        sleep $interval
        waited=$((waited + interval))
    done
}
    
    print_success "Created host-network Nginx config at deploy/nginx/conf.d.host/frontend-app.conf"
    
    # Also create a custom Dockerfile for frontend that uses this config
    cat > Dockerfile.frontend-host << 'DOCKEREOF'
# Host Network Mode Frontend Dockerfile
# Uses custom Nginx config that proxies to host.docker.internal
FROM node:18-alpine AS build

ARG VITE_API_BASE_URL=http://localhost:5245/api
ARG VITE_SIGNALR_PRINTERS_URL=http://localhost:5245/hubs/printers
ARG VITE_SIGNALR_HARVEST_URL=http://localhost:5245/hubs/harvest
ENV VITE_API_BASE_URL=${VITE_API_BASE_URL} \
    VITE_SIGNALR_PRINTERS_URL=${VITE_SIGNALR_PRINTERS_URL} \
    VITE_SIGNALR_HARVEST_URL=${VITE_SIGNALR_HARVEST_URL}

WORKDIR /app

COPY src/Web/ReactApp/package*.json ./
RUN npm install --silent

COPY src/Web/ReactApp/ ./
RUN echo "Building with VITE_API_BASE_URL=$VITE_API_BASE_URL" && npm run build

# Production stage with Nginx
FROM nginx:alpine

COPY --from=build /app/dist /usr/share/nginx/html
COPY deploy/nginx/nginx-frontend.conf /etc/nginx/nginx.conf

# USE HOST MODE CONFIG - proxies to host.docker.internal instead of 'api' service
COPY deploy/nginx/conf.d.host/*.conf /etc/nginx/conf.d/

RUN rm -f /etc/nginx/conf.d/default.conf || true

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
    CMD curl -f http://localhost:80/ || exit 1

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
DOCKEREOF
    
    print_success "Created host-network Dockerfile at Dockerfile.frontend-host"
}

# Configure additional settings
configure_external_storage() {
    # In non-interactive mode, use pre-loaded config if available
    if [ "$NON_INTERACTIVE" = "true" ] && [ -n "${USE_EXTERNAL_STORAGE:-}" ]; then
        print_info "Using configured external storage: $USE_EXTERNAL_STORAGE"
        return 0
    fi
    
    print_header "💾 External Storage Configuration (P0 Data Persistence)"
    
    echo -e "${BLUE}3D Model Storage & G-Code Library${NC}"
    echo "These critical data files should persist independently from container lifecycles."
    echo "They will only be deleted when database files are removed explicitly."
    echo
    
    # Check if external storage was already configured
    if [ -z "${USE_EXTERNAL_STORAGE:-}" ]; then
        prompt_yes_no "Use external host directories for model uploads and G-code? (Required for data persistence)" "yes" "USE_EXTERNAL_STORAGE"
    else
        if [ "${USE_EXTERNAL_STORAGE}" = "true" ] || [ "${USE_EXTERNAL_STORAGE}" = "yes" ]; then
            USE_EXTERNAL_STORAGE="yes"
        else
            USE_EXTERNAL_STORAGE="no"
        fi
    fi
    
    if [ "$USE_EXTERNAL_STORAGE" = "yes" ]; then
        print_success "External storage enabled - data will persist on host filesystem"
        echo
        
        # Model storage directory (defaults to user's home directory - no sudo needed)
        local default_models_path="${EXTERNAL_MODELS_PATH:-$HOME/.printfarmer/models}"
        prompt_with_default "Host directory for 3D model storage (all uploaded models):" "$default_models_path" "EXTERNAL_MODELS_PATH"
        
        # Ensure directory exists
        if ! mkdir -p "$EXTERNAL_MODELS_PATH" 2>/dev/null; then
            print_error "Failed to create models directory: $EXTERNAL_MODELS_PATH"
            print_info "Please ensure the directory path is writable or change the path above"
            return 1
        fi
        print_success "Models directory ready: $EXTERNAL_MODELS_PATH"
        
        # G-code storage directory (defaults to user's home directory)
        local default_gcode_path="${EXTERNAL_GCODE_PATH:-$HOME/.printfarmer/gcode}"
        prompt_with_default "Host directory for generated G-code:" "$default_gcode_path" "EXTERNAL_GCODE_PATH"
        
        # Ensure directory exists
        if ! mkdir -p "$EXTERNAL_GCODE_PATH" 2>/dev/null; then
            print_error "Failed to create G-code directory: $EXTERNAL_GCODE_PATH"
            print_info "Please ensure the directory path is writable or change the path above"
            return 1
        fi
        print_success "G-code directory ready: $EXTERNAL_GCODE_PATH"
        
        # Slicer profiles directory (defaults to user's home directory, optional)
        local default_profiles_path="${EXTERNAL_PROFILES_PATH:-$HOME/.printfarmer/slicer-profiles}"
        prompt_with_default "Host directory for slicer profiles (optional):" "$default_profiles_path" "EXTERNAL_PROFILES_PATH"
        
        # Ensure directory exists
        if ! mkdir -p "$EXTERNAL_PROFILES_PATH" 2>/dev/null; then
            print_error "Failed to create slicer profiles directory: $EXTERNAL_PROFILES_PATH"
            print_info "Please ensure the directory path is writable or change the path above"
            return 1
        fi
        print_success "Slicer profiles directory ready: $EXTERNAL_PROFILES_PATH"
        
        # Prompt for appropriate database/app data path based on architecture
        if [ "$ARCHITECTURE" = "monolithic" ]; then
            # Application data storage (SQLite database for monolithic)
            local default_app_data_path="${EXTERNAL_APP_DATA_PATH:-$HOME/.printfarmer/data}"
            prompt_with_default "Host directory for application data (monolithic SQLite database):" "$default_app_data_path" "EXTERNAL_APP_DATA_PATH"
            
            # Ensure directory exists
            if ! mkdir -p "$EXTERNAL_APP_DATA_PATH" 2>/dev/null; then
                print_error "Failed to create app data directory: $EXTERNAL_APP_DATA_PATH"
                print_info "Please ensure the directory path is writable or change the path above"
                return 1
            fi
            print_success "Application data directory ready: $EXTERNAL_APP_DATA_PATH"
            
            # Clear database path for monolithic
            EXTERNAL_DATABASE_PATH=""
        else
            # Database storage directory (PostgreSQL/MySQL/SQL Server for microservices)
            local default_database_path="${EXTERNAL_DATABASE_PATH:-$HOME/.printfarmer/database}"
            prompt_with_default "Host directory for database storage (PostgreSQL/MySQL/SQL Server):" "$default_database_path" "EXTERNAL_DATABASE_PATH"
            
            # Ensure directory exists
            if ! mkdir -p "$EXTERNAL_DATABASE_PATH" 2>/dev/null; then
                print_error "Failed to create database directory: $EXTERNAL_DATABASE_PATH"
                print_info "Please ensure the directory path is writable or change the path above"
                return 1
            fi
            print_success "Database directory ready: $EXTERNAL_DATABASE_PATH"
            
            # Clear app data path for microservices
            EXTERNAL_APP_DATA_PATH=""
        fi
        
        print_success "External storage directories configured:"
        echo "  • Models:       $EXTERNAL_MODELS_PATH"
        echo "  • G-code:       $EXTERNAL_GCODE_PATH"
        echo "  • Profiles:     $EXTERNAL_PROFILES_PATH"
        if [ "$ARCHITECTURE" = "monolithic" ]; then
            echo "  • App Data:     $EXTERNAL_APP_DATA_PATH (Monolithic SQLite)"
        else
            echo "  • Database:     $EXTERNAL_DATABASE_PATH (Microservices PostgreSQL/MySQL/SQL Server)"
        fi
        echo
        print_info "⚠️  Data Persistence Guarantee:"
        echo "  • Data survives container recreation (docker-compose down/up)"
        echo "  • Data survives image rebuild"
        echo "  • Data only deleted if you explicitly remove these directories"
        
    else
        print_warning "Docker-managed volumes will be used - data may be lost if volumes are removed"
        print_warning "⚠️  WARNING: Uploaded models and G-code will NOT persist across container recreation"
        print_warning "To preserve user data, use external storage (answer 'yes' on next deployment)"
        print_info "For development/testing only: Recommended to use external storage for any persistent deployment"
        USE_EXTERNAL_STORAGE="no"
        EXTERNAL_MODELS_PATH=""
        EXTERNAL_GCODE_PATH=""
        EXTERNAL_PROFILES_PATH=""
        EXTERNAL_APP_DATA_PATH=""
        EXTERNAL_DATABASE_PATH=""
    fi
}

configure_additional() {
    # In non-interactive mode, use pre-loaded config if available
    if [ "$NON_INTERACTIVE" = "true" ] && [ -n "${ENVIRONMENT:-}" ]; then
        print_info "Using configured environment: $ENVIRONMENT"
        return 0
    fi
    
    print_header "⚙️  Additional Configuration"
    
    # Initialize monitoring/observability variables with defaults if not already set
    INCLUDE_MONITORING=${INCLUDE_MONITORING:-false}
    INCLUDE_TELEMETRY=${INCLUDE_TELEMETRY:-false}
    INCLUDE_SECURITY=${INCLUDE_SECURITY:-false}
    INCLUDE_REGISTRY=${INCLUDE_REGISTRY:-false}
    ENABLE_ELASTIC_STACK=${ENABLE_ELASTIC_STACK:-}
    local elastic_stack_from_env=""
    if [ -n "$ENABLE_ELASTIC_STACK" ]; then
        elastic_stack_from_env="true"
    fi
    
    prompt_with_default "Environment [Development/Production]:" "Development" "ENVIRONMENT"
    
    if [ "$ENVIRONMENT" = "Development" ]; then
        ENABLE_SWAGGER="true"
        ENABLE_DETAILED_LOGGING="true"
        print_info "Development mode: Swagger UI and detailed logging enabled"
    else
        ENABLE_SWAGGER="false"
        ENABLE_DETAILED_LOGGING="false"
        print_info "Production mode: Swagger UI and detailed logging disabled"
    fi
    
    echo
    echo -e "${BLUE}Observability & Monitoring Configuration${NC}"
    echo "PrintFarmer supports optional monitoring and telemetry stacks for production deployments."
    
    # Only offer monitoring/telemetry prompts if not already set by CLI flags
    if [ "${CLI_INCLUDE_MONITORING:-false}" = "false" ]; then
        # If INCLUDE_MONITORING was previously set (e.g., from env/.env), use it to seed the interactive choice
        if [ -z "${INCLUDE_MONITORING_CHOICE:-}" ] && [ -n "${INCLUDE_MONITORING:-}" ]; then
            if [[ "${INCLUDE_MONITORING}" =~ ^(true|yes|1)$ ]]; then
                INCLUDE_MONITORING_CHOICE="yes"
            else
                INCLUDE_MONITORING_CHOICE="no"
            fi
        fi
        prompt_yes_no "Enable monitoring stack (Prometheus, Grafana)?" "no" "INCLUDE_MONITORING_CHOICE"
        if [ "$INCLUDE_MONITORING_CHOICE" = "yes" ]; then
            INCLUDE_MONITORING="true"
        else
            INCLUDE_MONITORING="false"
        fi
    else
        print_info "Monitoring stack enabled via CLI flag"
        INCLUDE_MONITORING="true"
    fi

    if [ "$INCLUDE_MONITORING" = "true" ]; then
        local system_arch
        system_arch=$(uname -m 2>/dev/null || echo "unknown")
        local elastic_supported="true"
        case "$system_arch" in
            arm*|aarch64)
                elastic_supported="false"
                ;;
        esac

        local normalized_elastic=""
        if [ -n "$ENABLE_ELASTIC_STACK" ]; then
            normalized_elastic=$(printf '%s' "$ENABLE_ELASTIC_STACK" | tr '[:upper:]' '[:lower:]')
        fi

        if [ "$elastic_supported" != "true" ]; then
            if [[ "$normalized_elastic" =~ ^(true|yes|1)$ ]]; then
                print_warning "Elastic Stack is not supported on architecture $system_arch; ignoring ENABLE_ELASTIC_STACK request"
            fi
            ENABLE_ELASTIC_STACK="false"
            print_info "Elastic Stack not available on architecture $system_arch; monitoring will include Prometheus and Grafana only"
        else
            if [[ "$normalized_elastic" =~ ^(true|yes|1)$ ]]; then
                ENABLE_ELASTIC_STACK="true"
                print_info "Elastic Stack enabled via ENABLE_ELASTIC_STACK environment configuration"
            elif [[ "$normalized_elastic" =~ ^(false|no|0)$ ]]; then
                ENABLE_ELASTIC_STACK="false"
                if [ "$elastic_stack_from_env" = "true" ]; then
                    print_info "Elastic Stack disabled via ENABLE_ELASTIC_STACK environment configuration"
                fi
            elif [ "$NON_INTERACTIVE" = "true" ] || [ "${CLI_INCLUDE_MONITORING:-false}" = "true" ]; then
                ENABLE_ELASTIC_STACK="false"
            else
                echo
                echo -e "${BLUE}Monitoring Stack Options${NC}"
                echo "  1. Prometheus + Grafana (lightweight, recommended)"
                echo "  2. Prometheus + Grafana + Elastic Stack (adds Elasticsearch, Logstash, Kibana)"
                # Seed monitoring stack mode from an existing value if present so the prompt shows the previous selection
                if [ -z "${MONITORING_STACK_MODE:-}" ] && [ -n "${MONITORING_STACK_MODE:-}" ]; then
                    : # nothing to do (placeholder)
                fi
                if [ -n "${MONITORING_STACK_MODE:-}" ]; then
                    default_monitoring_mode="${MONITORING_STACK_MODE}"
                else
                    default_monitoring_mode="1"
                fi
                prompt_with_default "Select monitoring stack option [1-2]:" "$default_monitoring_mode" "MONITORING_STACK_MODE"
                case "$MONITORING_STACK_MODE" in
                    2|elastic|Elastic)
                        ENABLE_ELASTIC_STACK="true"
                        ;;
                    *)
                        ENABLE_ELASTIC_STACK="false"
                        ;;
                esac
            fi
        fi
    else
        ENABLE_ELASTIC_STACK="false"
    fi
    
    if [ "${CLI_INCLUDE_TELEMETRY:-false}" = "false" ]; then
        # Seed telemetry prompt from existing INCLUDE_TELEMETRY value if present
        if [ -z "${INCLUDE_TELEMETRY_CHOICE:-}" ] && [ -n "${INCLUDE_TELEMETRY:-}" ]; then
            if [[ "${INCLUDE_TELEMETRY}" =~ ^(true|yes|1)$ ]]; then
                INCLUDE_TELEMETRY_CHOICE="yes"
            else
                INCLUDE_TELEMETRY_CHOICE="no"
            fi
        fi
        prompt_yes_no "Enable telemetry/observability (OpenTelemetry)?" "no" "INCLUDE_TELEMETRY_CHOICE"
        if [ "$INCLUDE_TELEMETRY_CHOICE" = "yes" ]; then
            INCLUDE_TELEMETRY="true"
        fi
    else
        print_info "Telemetry/observability enabled via CLI flag"
        INCLUDE_TELEMETRY="true"
    fi

    # Persist selection early so re-running the interactive flow picks up previous choices
    persist_env_key() {
        local key="$1"; local val="$2"
        [ -n "$ENV_FILE" ] || return 0
        # Ensure file exists
        touch "$ENV_FILE"
        # If key exists, replace; otherwise append
        if grep -qE "^${key}=" "$ENV_FILE"; then
            # Use awk to reliably replace the first occurrence
            awk -v k="$key" -v v="$val" 'BEGIN{FS=OFS="="} $1==k {$2=v; print; next} {print}' "$ENV_FILE" > "${ENV_FILE}.tmp" && mv "${ENV_FILE}.tmp" "$ENV_FILE"
        else
            echo "${key}=${val}" >> "$ENV_FILE"
        fi
    }

    # Save monitoring/telemetry choices so the next interactive run uses them as defaults
    if [ -n "${INCLUDE_MONITORING:-}" ]; then
        persist_env_key "INCLUDE_MONITORING" "${INCLUDE_MONITORING}"
    fi
    if [ -n "${INCLUDE_TELEMETRY:-}" ]; then
        persist_env_key "INCLUDE_TELEMETRY" "${INCLUDE_TELEMETRY}"
    fi
    if [ -n "${MONITORING_STACK_MODE:-}" ]; then
        persist_env_key "MONITORING_STACK_MODE" "${MONITORING_STACK_MODE}"
    fi
    if [ -n "${INCLUDE_SECURITY:-}" ]; then
        persist_env_key "INCLUDE_SECURITY" "${INCLUDE_SECURITY}"
    fi
    if [ -n "${INCLUDE_REGISTRY:-}" ]; then
        persist_env_key "INCLUDE_REGISTRY" "${INCLUDE_REGISTRY}"
    fi
    if [ -n "${INCLUDE_DISCOVERY:-}" ]; then
        persist_env_key "INCLUDE_DISCOVERY" "${INCLUDE_DISCOVERY}"
    fi
    
    if [ "${CLI_INCLUDE_SECURITY:-false}" = "false" ]; then
        prompt_yes_no "Enable security configurations (enhanced security headers, HTTPS)?" "no" "INCLUDE_SECURITY_CHOICE"
        if [ "$INCLUDE_SECURITY_CHOICE" = "yes" ]; then
            INCLUDE_SECURITY="true"
        fi
    else
        print_info "Security configurations enabled via CLI flag"
        INCLUDE_SECURITY="true"
    fi
    
    if [ "${CLI_INCLUDE_REGISTRY:-false}" = "false" ]; then
        prompt_yes_no "Enable local Docker registry (for development/air-gapped deployments)?" "no" "INCLUDE_REGISTRY_CHOICE"
        if [ "$INCLUDE_REGISTRY_CHOICE" = "yes" ]; then
            INCLUDE_REGISTRY="true"
        fi
    else
        print_info "Local Docker registry enabled via CLI flag"
        INCLUDE_REGISTRY="true"
    fi
    
    if [ "${CLI_INCLUDE_DISCOVERY:-false}" = "false" ]; then
        echo -e "${BLUE}Network Discovery Configuration${NC}"
        echo "Network discovery allows PrintFarmer to find 3D printers on your network."
        echo
        
        if [ "$OS" = "macos" ] && [ "$ARCHITECTURE" = "docker" ]; then
            print_warning "macOS Docker has limited network access. Discovery may not work for all WiFi-connected printers."
        fi
        
        # Use previously configured value as default if available
        local default_discovery="no"
        if [ "${ENABLE_DISCOVERY:-}" = "true" ]; then
            default_discovery="yes"
        fi
        
        prompt_yes_no "Enable network printer discovery?" "$default_discovery" "INCLUDE_DISCOVERY_CHOICE"
        
        if [ "$INCLUDE_DISCOVERY_CHOICE" = "yes" ]; then
            INCLUDE_DISCOVERY="true"
            ALLOW_LOCAL_NETWORK="true"
            
            echo
            echo -e "${BLUE}Configure IP address ranges to scan for printers:${NC}"
            echo "Common ranges:"
            echo "  • 192.168.0.0/16 (Most home networks: 192.168.x.x)"
            echo "  • 10.0.0.0/8 (Corporate networks: 10.x.x.x)"
            echo "  • 172.16.0.0/12 (Docker networks: 172.16.x.x-172.31.x.x)"
            echo
            
            prompt_with_default "Network ranges to scan (comma-separated):" "192.168.0.0/16,10.0.0.0/8" "NETWORK_RANGES"
        else
            INCLUDE_DISCOVERY="false"
            ALLOW_LOCAL_NETWORK="false"
            NETWORK_RANGES=""
        fi
    else
        print_info "Network printer discovery enabled via CLI flag"
        INCLUDE_DISCOVERY="true"
        ALLOW_LOCAL_NETWORK="true"
    fi
    
    # Map INCLUDE_DISCOVERY to ENABLE_DISCOVERY for downstream use
    if [ "$INCLUDE_DISCOVERY" = "true" ]; then
        ENABLE_DISCOVERY="true"
    else
        ENABLE_DISCOVERY="false"
    fi
    


    echo
    echo -e "${BLUE}Distributed Slicing Configuration${NC}"
    prompt_yes_no "Enable distributed slicing (uses external slicer workers)?" "yes" "ENABLE_DIST_SLICING_CHOICE"
    if [ "$ENABLE_DIST_SLICING_CHOICE" = "yes" ]; then
        ENABLE_DISTRIBUTED_SLICING=true
    else
        ENABLE_DISTRIBUTED_SLICING=false
    fi

    # Worker enablement & scaling (only meaningful if distributed slicing enabled)
    if [ "$ENABLE_DISTRIBUTED_SLICING" = "true" ]; then
        echo
        echo -e "${BLUE}Configure slicer workers. You can enable OrcaSlicer workers and specify replica counts.${NC}"
    # Default to 'no' to avoid accidental enabling when slicer work is paused
    prompt_yes_no "Enable OrcaSlicer worker(s)?" "no" "ENABLE_ORCA_WORKER"
        if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
            prompt_with_default "OrcaSlicer version to deploy:" "${ORCASLICER_VERSION:-2.3.1}" "ORCASLICER_VERSION"
            prompt_with_default "Number of OrcaSlicer worker replicas:" "1" "ORCA_WORKER_COUNT"
        else
            ORCA_WORKER_COUNT=0
        fi

        # Allow endpoint override (advanced) only if microservices; monolithic uses host networking and localhost
        if [ "$ARCHITECTURE" = "microservices" ]; then
            prompt_yes_no "Override default worker service endpoints?" "no" "OVERRIDE_WORKER_ENDPOINTS"
            if [ "$OVERRIDE_WORKER_ENDPOINTS" = "yes" ]; then
                if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
                    prompt_with_default "OrcaSlicer worker endpoint (API reachable URL):" "http://orcaslicer-worker:8080" "ORCA_WORKER_ENDPOINT"
                fi
            fi
        fi
    else
        ENABLE_ORCA_WORKER=no
        ORCA_WORKER_COUNT=0
    fi

    echo
    echo -e "${BLUE}Spoolman Integration${NC}"
    echo "Spoolman provides centralized filament spool tracking. If you already run Spoolman you can point PrintFarmer at its base URL now (you can also configure later in the UI)."
    prompt_yes_no "Enable Spoolman integration?" "no" "ENABLE_SPOOLMAN"
    if [ "$ENABLE_SPOOLMAN" = "yes" ]; then
        prompt_with_default "Spoolman base URL (protocol + host[:port], no trailing slash):" "http://spoolman:7912" "SPOOLMAN_BASE_URL"
        # Derive port from URL (default 80 if none specified)
        _tmp=${SPOOLMAN_BASE_URL#*://}
        _hostport=${_tmp%%/*}
        if [[ "$_hostport" == *:* ]]; then
            SPOOLMAN_PORT=${_hostport##*:}
        else
            # Infer by scheme
            if [[ $SPOOLMAN_BASE_URL == https://* ]]; then SPOOLMAN_PORT=443; else SPOOLMAN_PORT=80; fi
        fi
    else
        SPOOLMAN_BASE_URL=""
        SPOOLMAN_PORT=""
    fi
}

# Generate and manage slicer worker API keys
# Creates a map of worker names to API keys for initial registration
generate_slicer_worker_api_keys() {
    if [ "$ENABLE_ORCA_WORKER" != "yes" ] || [ "$ORCA_WORKER_COUNT" -eq 0 ]; then
        # No workers enabled, skip key generation
        return 0
    fi

    print_header "🔑 Generating Slicer Worker API Keys"
    
    # Initialize arrays for storing worker info (without -g for cross-shell compatibility)
    SLICER_WORKER_API_KEYS=()
    SLICER_WORKER_NAMES=()
    
    # Generate unique API keys for each worker replica
    for ((i=1; i<=ORCA_WORKER_COUNT; i++)); do
        local worker_name="orcaslicer-worker"
        if [ "$ORCA_WORKER_COUNT" -gt 1 ]; then
            # For scaled workers, append replica number
            worker_name="${worker_name}-$i"
        fi
        
        local api_key
        api_key=$(generate_slicer_api_key)
        
        SLICER_WORKER_NAMES+=("$worker_name")
        SLICER_WORKER_API_KEYS+=("$api_key")
        
        print_info "Generated API key for worker replica $i: $(echo "$api_key" | cut -c1-8)..."
    done
    
    print_success "Generated ${#SLICER_WORKER_API_KEYS[@]} API keys for OrcaSlicer workers"
}

# Export slicer worker API keys to environment file
# Called from generate_env_file to inject keys that workers will use for registration
export_slicer_worker_api_keys() {
    if [ "$ENABLE_ORCA_WORKER" != "yes" ] || [ "$ORCA_WORKER_COUNT" -eq 0 ]; then
        return 0
    fi
    
    if [ -z "${SLICER_WORKER_API_KEYS[0]:-}" ]; then
        # Keys haven't been generated yet, generate them now
        generate_slicer_worker_api_keys
    fi
    
    # Export first worker's API key (primary/default for single worker or primary endpoint)
    # In microservices with scaling, each replica will get its own key via compose environment
    local primary_api_key="${SLICER_WORKER_API_KEYS[0]}"
    
    if [ -n "$primary_api_key" ]; then
        cat >> "$ENV_FILE" << EOF

# Slicer Worker API Keys - Generated for automatic worker registration
# Workers use these keys to authenticate with the API during registration
# Format: SlicerRegistry__ApiKey__<WorkerName> for individual workers
# Format: SlicerRegistry__ApiKey for default/primary worker
SlicerRegistry__ApiKey=$primary_api_key
EOF
        
        # For scaled workers, also export individual keys
        if [ "$ORCA_WORKER_COUNT" -gt 1 ]; then
            cat >> "$ENV_FILE" << EOF

# Individual API Keys for Scaled OrcaSlicer Workers
EOF
            for ((i=0; i<${#SLICER_WORKER_NAMES[@]}; i++)); do
                local worker_name="${SLICER_WORKER_NAMES[$i]}"
                local api_key="${SLICER_WORKER_API_KEYS[$i]}"
                # Export as environment variable following Docker compose naming
                # Replace hyphens with underscores for valid env var names
                local env_var_name="SlicerRegistry__ApiKey__${worker_name//-/_}"
                echo "$env_var_name=$api_key" >> "$ENV_FILE"
            done
        fi
        
        print_info "Exported worker API keys to $ENV_FILE"
    fi
}

# Generate the main environment file for docker deployment
generate_env_file() {
    print_header "📝 Generating Configuration"
    
    # Set default env file if not already set
    ENV_FILE="${ENV_FILE:-.env}"
    
    print_info "Creating environment file: $ENV_FILE"
    
    # Generate dynamic CORS origins based on configured ports
    CORS_ORIGINS="http://localhost:3000"
    
    if [ "$ARCHITECTURE" = "microservices" ]; then
        # Microservices: frontend on HTTP_PORT, API on API_PORT
        CORS_ORIGINS="${CORS_ORIGINS},http://localhost:${HTTP_PORT},http://localhost:${API_PORT}"
    else
        # Monolithic: everything on HTTP_PORT
        CORS_ORIGINS="${CORS_ORIGINS},http://localhost:${HTTP_PORT}"
    fi
    
    cat > "$ENV_FILE" << EOF
# PrintFarmer Docker Configuration
# Generated by deploy-docker.sh on $(date)

# Architecture
DEPLOYMENT_TYPE=$ARCHITECTURE

# Application Settings
ASPNETCORE_ENVIRONMENT=$ENVIRONMENT
ASPNETCORE_URLS=http://0.0.0.0:8080

# Database Configuration
DB_PROVIDER=$DB_PROVIDER
EOF
    
    # Clear provider include flags to avoid accidental emission of other DB secrets
    INCLUDE_POSTGRES=${INCLUDE_POSTGRES:-no}
    INCLUDE_SQLSERVER=${INCLUDE_SQLSERVER:-no}
    INCLUDE_MYSQL=${INCLUDE_MYSQL:-no}

    # Emit provider-specific database environment variables and derive canonical connection string
    # NOTE: do not default to 'postgres' here — if DB_PROVIDER is not explicitly set, skip provider-specific secrets
    case "${DB_PROVIDER:-}" in
        postgres)
            POSTGRES_DB=${POSTGRES_DB:-printfarmer}
            POSTGRES_USER=${POSTGRES_USER:-postgres}
            POSTGRES_PORT=${POSTGRES_PORT:-5432}
            # Generate a random password if none supplied
            POSTGRES_PASSWORD=${POSTGRES_PASSWORD:-}
            if [ -z "$POSTGRES_PASSWORD" ]; then
                POSTGRES_PASSWORD=$(generate_random_password)
                print_info "Generated random PostgreSQL password (saved to env file)"
            fi
            echo "POSTGRES_DB=$POSTGRES_DB" >> "$ENV_FILE"
            echo "POSTGRES_USER=$POSTGRES_USER" >> "$ENV_FILE"
            echo "POSTGRES_PASSWORD=$POSTGRES_PASSWORD" >> "$ENV_FILE"
            echo "POSTGRES_PORT=$POSTGRES_PORT" >> "$ENV_FILE"
            CONNECTION_STRING="Host=postgres;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
            ;;
        sqlserver)
            SQLSERVER_DB=${SQLSERVER_DB:-printfarmer}
            # Prefer an explicitly provided SQLSERVER_PASSWORD, then DB_PASSWORD, otherwise generate one
            SQLSERVER_PASSWORD=${SQLSERVER_PASSWORD:-${DB_PASSWORD:-}}
            SQLSERVER_PORT=${SQLSERVER_PORT:-1433}

            if [ -z "$SQLSERVER_PASSWORD" ]; then
                # Generate a random strong password for SA
                SQLSERVER_PASSWORD=$(generate_random_password)
                print_info "Generated random SQL Server SA password (saved to env file)"
            fi

            # Use MSSQL_SA_PASSWORD as canonical key used across templates
            MSSQL_SA_PASSWORD=${MSSQL_SA_PASSWORD:-$SQLSERVER_PASSWORD}

            echo "SQLSERVER_DB=$SQLSERVER_DB" >> "$ENV_FILE"
            echo "SQLSERVER_PASSWORD=$SQLSERVER_PASSWORD" >> "$ENV_FILE"
            echo "SQLSERVER_PORT=$SQLSERVER_PORT" >> "$ENV_FILE"
            echo "MSSQL_SA_PASSWORD=$MSSQL_SA_PASSWORD" >> "$ENV_FILE"
            CONNECTION_STRING="Server=sqlserver;Database=$SQLSERVER_DB;User Id=sa;Password=$SQLSERVER_PASSWORD;TrustServerCertificate=True;"
            ;;
        mysql)
            MYSQL_DB=${MYSQL_DB:-printfarmer}
            MYSQL_USER=${MYSQL_USER:-root}
            # Generate a random password if none supplied
            MYSQL_PASSWORD=${MYSQL_PASSWORD:-}
            if [ -z "$MYSQL_PASSWORD" ]; then
                MYSQL_PASSWORD=$(generate_random_password)
                print_info "Generated random MySQL password (saved to env file)"
            fi
            MYSQL_ROOT_PASSWORD=${MYSQL_ROOT_PASSWORD:-$MYSQL_PASSWORD}
            echo "MYSQL_DB=$MYSQL_DB" >> "$ENV_FILE"
            echo "MYSQL_USER=$MYSQL_USER" >> "$ENV_FILE"
            echo "MYSQL_PASSWORD=$MYSQL_PASSWORD" >> "$ENV_FILE"
            echo "MYSQL_ROOT_PASSWORD=$MYSQL_ROOT_PASSWORD" >> "$ENV_FILE"
            CONNECTION_STRING="Server=mysql;Database=$MYSQL_DB;User=$MYSQL_USER;Password=$MYSQL_PASSWORD;"
            ;;
        external)
            # External DB details were collected during configure_database() into EXT_DB_* variables
            EXT_DB_TYPE=${EXT_DB_TYPE:-postgres}
            echo "EXT_DB_TYPE=$EXT_DB_TYPE" >> "$ENV_FILE"
            echo "EXT_DB_HOST=${EXT_DB_HOST:-}" >> "$ENV_FILE"
            echo "EXT_DB_NAME=${EXT_DB_NAME:-}" >> "$ENV_FILE"
            echo "EXT_DB_USER=${EXT_DB_USER:-}" >> "$ENV_FILE"
            # Do not echo passwords to stdout here if empty; still include the var name for clarity
            echo "EXT_DB_PASSWORD=${EXT_DB_PASSWORD:-}" >> "$ENV_FILE"
            # Use connection string previously built in configure_database()
            CONNECTION_STRING=${CONNECTION_STRING:-}
            ;;
        sqlite)
            CONNECTION_STRING=${CONNECTION_STRING:-"Data Source=/data/farm.db"}
            ;;
        *)
            # Unknown provider: fall back to any pre-derived CONNECTION_STRING
            CONNECTION_STRING=${CONNECTION_STRING:-}
            ;;
    esac

    # Write unified default connection string key consumed by Program.cs
    # If we're deploying in host network mode, rewrite any Docker service hostnames
    # (e.g., 'database', 'postgres', 'mysql', 'sqlserver') to 'localhost' so the
    # API running in host network mode connects to the host services correctly.
    if [ "${NETWORK_MODE:-bridge}" = "host" ]; then
        # Use sed to conservatively replace common host keys while preserving the rest
        CONNECTION_STRING_TO_WRITE=$(printf '%s' "$CONNECTION_STRING" | sed -E \
            -e 's/([Hh]ost)=(database|postgres|postgresql)/\1=localhost/Ig' \
            -e 's/([Ss]erver)=(mysql|sqlserver)/\1=localhost/Ig')
    else
        CONNECTION_STRING_TO_WRITE="$CONNECTION_STRING"
    fi
    # IMPORTANT: Do NOT quote the connection string in the .env file - Docker Compose
    # includes literal quotes as part of the value, breaking connection string parsing.
    # The application reads only ConnectionStrings__Default and determines the provider
    # from the DB_PROVIDER environment variable. Provider-specific keys are not used.
    echo "ConnectionStrings__Default=$CONNECTION_STRING_TO_WRITE" >> "$ENV_FILE"
    set_exported_env_var "ConnectionStrings__Default" "$CONNECTION_STRING_TO_WRITE"
    
    # Generate monitoring service credentials
    GRAFANA_ADMIN_PASSWORD=${GRAFANA_ADMIN_PASSWORD:-$(generate_random_password)}
    VAULT_DEV_ROOT_TOKEN=${VAULT_DEV_ROOT_TOKEN:-$(generate_random_password)}
    
    cat >> "$ENV_FILE" << EOF

# Monitoring & Observability Credentials
GRAFANA_ADMIN_USER=admin
GRAFANA_ADMIN_PASSWORD=$GRAFANA_ADMIN_PASSWORD
VAULT_DEV_ROOT_TOKEN=$VAULT_DEV_ROOT_TOKEN

# Network Configuration
ALLOW_LOCAL_NETWORK=$ALLOW_LOCAL_NETWORK
ALLOWED_NETWORK_RANGES=$NETWORK_RANGES
NETWORK_MODE=${NETWORK_MODE:-bridge}
DOCKER_HOST_NETWORK=$([ "${NETWORK_MODE:-bridge}" = "host" ] && echo "true" || echo "false")

# CORS Configuration
CORS__AllowedOrigins=$CORS_ORIGINS

# Feature Flags  
ENABLE_SWAGGER=$ENABLE_SWAGGER
ENABLE_DETAILED_LOGGING=$ENABLE_DETAILED_LOGGING
ENABLE_DISTRIBUTED_SLICING=$ENABLE_DISTRIBUTED_SLICING
ORCA_WORKER_COUNT=$ORCA_WORKER_COUNT
ENABLE_ORCA_WORKER=$ENABLE_ORCA_WORKER
ORCA_HOST_PORT=$ORCA_HOST_PORT

# Slicer Versions
ORCASLICER_VERSION=${ORCASLICER_VERSION:-2.3.1}

# Spoolman
SPOOLMAN_ENABLED=${ENABLE_SPOOLMAN:-no}
SPOOLMAN_BASE_URL=${SPOOLMAN_BASE_URL:-}
SPOOLMAN_PORT=${SPOOLMAN_PORT:-7912}

# Application Settings - PFARM Configuration
PFARM__Spoolman__BaseUrl=${SPOOLMAN_BASE_URL:-}
PFARM__NetworkDiscovery__EnableDiscovery=$ENABLE_DISCOVERY
PFARM__NetworkDiscovery__DiscoverySubnets=$NETWORK_RANGES

# Port Configuration
HTTP_PORT=$HTTP_PORT
# API_URL for health checks (used by ComprehensiveHealthCheck to probe internal endpoints)
# This must be a valid loopback address that can be reached from within the container
API_URL=http://localhost:5245

# External Storage Configuration (P0 - Critical Data Persistence)
# Maps 3D models and G-code to persistent host directories
# Data persists across container recreation, only deleted when explicitly removing these paths
USE_EXTERNAL_STORAGE=${USE_EXTERNAL_STORAGE:-no}
EXTERNAL_MODELS_PATH=${EXTERNAL_MODELS_PATH:-}
EXTERNAL_GCODE_PATH=${EXTERNAL_GCODE_PATH:-}
EXTERNAL_PROFILES_PATH=${EXTERNAL_PROFILES_PATH:-}
EXTERNAL_APP_DATA_PATH=${EXTERNAL_APP_DATA_PATH:-}
EXTERNAL_DATABASE_PATH=${EXTERNAL_DATABASE_PATH:-}
EOF

    # Small summary for generated environment file: show which sensitive values were included
    mask_secret() {
        local s="$1"
        if [ -z "$s" ]; then
            echo "(not set)"
            return
        fi
        local len=${#s}
        if [ $len -le 8 ]; then
            echo "$s"
            return
        fi
        local head=${s:0:4}
        local tail=${s: -4}
        echo "${head}****${tail}"
    }

    print_header "📦 Environment file generated"
    print_info "Environment file: $ENV_FILE"
    print_info "Database provider: ${DB_PROVIDER:-postgres}"

    case "${DB_PROVIDER:-postgres}" in
        postgres)
            print_info "PostgreSQL credentials included (masked):"
            echo "  POSTGRES_USER=$(mask_secret "$POSTGRES_USER")"
            echo "  POSTGRES_PASSWORD=$(mask_secret "$POSTGRES_PASSWORD")"
            ;;
        sqlserver)
            print_info "SQL Server credentials included (masked):"
            echo "  SQLSERVER_DB=${SQLSERVER_DB:-printfarmer}"
            echo "  MSSQL_SA_PASSWORD=$(mask_secret "$MSSQL_SA_PASSWORD")"
            ;;
        mysql)
            print_info "MySQL credentials included (masked):"
            echo "  MYSQL_USER=$(mask_secret "$MYSQL_USER")"
            echo "  MYSQL_PASSWORD=$(mask_secret "$MYSQL_PASSWORD")"
            ;;
        external)
            print_info "External DB configuration included (credentials not displayed)."
            ;;
        sqlite)
            print_info "Using SQLite - no DB credentials included."
            ;;
    esac
    
    print_info "Monitoring & Observability credentials generated (masked):"
    echo "  GRAFANA_ADMIN_USER=admin"
    echo "  GRAFANA_ADMIN_PASSWORD=$(mask_secret "$GRAFANA_ADMIN_PASSWORD")"
    echo "  VAULT_DEV_ROOT_TOKEN=$(mask_secret "$VAULT_DEV_ROOT_TOKEN")"

    print_warning "Generated passwords are sensitive. Store .env files securely and restrict access (chmod 600)."
    print_info "To view all credentials, run: grep 'PASSWORD\|TOKEN' $ENV_FILE || true"
    
    if [ "$ARCHITECTURE" = "microservices" ]; then
        cat >> "$ENV_FILE" << EOF
API_PORT=$API_PORT

EOF
    fi
    
    # Emit SQL Server entries only if SQL Server is selected or explicitly requested
    if [ "${DB_PROVIDER:-}" = "sqlserver" ] || [ "${INCLUDE_SQLSERVER:-no}" = "yes" ]; then
        cat >> "$ENV_FILE" << EOF

# SQL Server Configuration
SQLSERVER_DB=${SQLSERVER_DB:-printfarmer}
SQLSERVER_PASSWORD=${SQLSERVER_PASSWORD:-$DB_PASSWORD}
SQLSERVER_PORT=${SQLSERVER_PORT:-1433}
MSSQL_SA_PASSWORD=${SQLSERVER_PASSWORD:-$DB_PASSWORD}
MSSQL_PID=${SQLSERVER_EDITION:-Developer}
ACCEPT_EULA=Y
EOF
    fi
    
    # Emit MySQL entries only if MySQL is selected or explicitly requested
    if [ "${DB_PROVIDER:-}" = "mysql" ] || [ "${INCLUDE_MYSQL:-no}" = "yes" ]; then
        cat >> "$ENV_FILE" << EOF

# MySQL Configuration
MYSQL_DB=${MYSQL_DB:-printfarmer}
MYSQL_USER=${MYSQL_USER:-root}
MYSQL_ROOT_PASSWORD=${MYSQL_PASSWORD:-$DB_PASSWORD}
MYSQL_DATABASE=${MYSQL_DB:-printfarmer}
EOF
    fi
    
    # Generate and export slicer worker API keys (if workers are enabled)
    # This ensures workers can authenticate with the API during registration
    generate_slicer_worker_api_keys
    export_slicer_worker_api_keys
    
    # Display generated slicer worker API keys (now that they're generated)
    if [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -gt 0 ]; then
        echo
        print_info "🔑 Slicer Worker API Keys (for automatic registration):"
        if [ "$ORCA_WORKER_COUNT" -eq 1 ]; then
            echo "  OrcaSlicer Worker 1: $(echo "${SLICER_WORKER_API_KEYS[0]}" | cut -c1-12)..."
        else
            for ((i=0; i<${#SLICER_WORKER_API_KEYS[@]}; i++)); do
                local replica_num=$((i+1))
                echo "  OrcaSlicer Worker $replica_num: $(echo "${SLICER_WORKER_API_KEYS[$i]}" | cut -c1-12)..."
            done
        fi
        echo
        print_info "Full API keys are available in: $ENV_FILE"
        print_info "Workers will automatically use these keys to register with the API on startup."
    fi
    
    print_success "Environment file created: $ENV_FILE"
    
    # Also create a standard .env file for docker-compose default behavior
    if [ "$ENV_FILE" != ".env" ]; then
        print_info "Creating standard .env file"
        cp "$ENV_FILE" .env
        print_success "Standard .env file created"
    fi
}

# Detect credential divergence between generated env and existing DB container
# Non-destructive by default: warns and exits in non-interactive mode if mismatch found.
detect_db_credential_divergence() {
    # Skip detection during dry-run or when DB provider is external/sqlite
    if [ "${DRY_RUN:-false}" = "true" ]; then
        return 0
    fi

    local provider="${DB_PROVIDER:-postgres}"
    if [ "$provider" = "external" ] || [ "$provider" = "sqlite" ]; then
        return 0
    fi

    # Helper to compare a container env var
    compare_container_env() {
        local container_name_pattern="$1"
        local env_key="$2"
        local generated_value="$3"

        # Find a matching container
        local cid
        cid=$(docker ps -aq --filter "name=$container_name_pattern" | head -n1 || true)
        if [ -z "$cid" ]; then
            return 1
        fi

        # Read env from container inspect
        local container_env
        container_env=$(docker inspect "$cid" --format '{{json .Config.Env}}' 2>/dev/null || true)
        if [ -z "$container_env" ]; then
            return 1
        fi

        # Extract value
        local val
        val=$(echo "$container_env" | tr -d '[]"' | tr ',' '\n' | sed -n "s/^${env_key}=\(.*\)/\1/p" | tail -1 || true)
        if [ -z "$val" ]; then
            return 2
        fi

        if [ "$val" != "$generated_value" ]; then
            echo "$cid:$env_key:$val"
            return 0
        fi
        return 3
    }

    # Get generated passwords from env file if set
    local gen_pg_pw gen_sql_pw gen_mysql_pw
    gen_pg_pw=$(get_env_value "POSTGRES_PASSWORD" || true)
    gen_sql_pw=$(get_env_value "SQLSERVER_PASSWORD" || get_env_value "MSSQL_SA_PASSWORD" || true)
    gen_mysql_pw=$(get_env_value "MYSQL_ROOT_PASSWORD" || get_env_value "MYSQL_PASSWORD" || true)

    local mismatch_info=""

    case "$provider" in
        postgres)
            if [ -n "$gen_pg_pw" ]; then
                # check typical container names
                for pattern in "printfarmer-database-postgres" "printfarmer-database" "pfarm-postgres"; do
                    local result
                    result=$(compare_container_env "$pattern" "POSTGRES_PASSWORD" "$gen_pg_pw" ) || true
                    if [ -n "$result" ]; then
                        mismatch_info="$result"
                        break
                    fi
                done
            fi
            ;;
        sqlserver)
            if [ -n "$gen_sql_pw" ]; then
                for pattern in "printfarmer-database-sqlserver" "printfarmer-database" "pfarm-sqlserver"; do
                    local result
                    result=$(compare_container_env "$pattern" "MSSQL_SA_PASSWORD" "$gen_sql_pw" ) || true
                    if [ -n "$result" ]; then
                        mismatch_info="$result"
                        break
                    fi
                done
            fi
            ;;
        mysql)
            if [ -n "$gen_mysql_pw" ]; then
                for pattern in "printfarmer-database-mysql" "printfarmer-database" "pfarm-mysql"; do
                    local result
                    result=$(compare_container_env "$pattern" "MYSQL_ROOT_PASSWORD" "$gen_mysql_pw" ) || true
                    if [ -n "$result" ]; then
                        mismatch_info="$result"
                        break
                    fi
                done
            fi
            ;;
        *)
            ;; 
    esac

    if [ -n "$mismatch_info" ]; then
        print_warning "Detected a mismatch between generated DB credentials and an existing DB container: $mismatch_info"
        print_warning "This usually means the DB volume was initialized earlier with a different password."
        print_info "Options:"
        echo "  1) Run ALTER USER inside DB to sync the password (non-destructive)"
        echo "  2) Backup DB, remove volume and recreate stack (destructive)"
        echo "  3) Abort deployment and investigate manually"

        if [ "${NON_INTERACTIVE:-false}" = "true" ]; then
            print_error "Non-interactive mode: aborting due to credential divergence. Use --force-sync-db-password or --recreate-db to auto-resolve."
            exit 1
        fi

        # Prompt user for action
        echo
        read -p "Choose action [1=ALTER USER, 2=Recreate DB (destructive), 3=Abort] (default 3): " action || true
        action=${action:-3}
        case "$action" in
            1)
                print_info "You chose ALTER USER. To proceed, run the following command manually or rerun with --force-sync-db-password to attempt automatic sync."
                ;;
            2)
                print_warning "You chose to recreate the DB: this will remove volumes and reinitialize the database."
                ;;
            *)
                print_info "Aborting deployment. No changes made."
                exit 1
                ;;
        esac
    fi
}

# Generate React .env.production file for Docker builds
generate_react_env_production() {
    local react_dir=""
    local candidates=(
        "$REPO_ROOT/src/Web/ReactApp"
        "$REPO_ROOT/Web/ReactApp"
        "./src/Web/ReactApp"
        "./Web/ReactApp"
    )

    for path in "${candidates[@]}"; do
        if [ -d "$path" ]; then
            react_dir="$path"
            break
        fi
    done
    if [ -z "$react_dir" ]; then
        print_warning "React app directory not found, skipping React environment setup"
        return 0
    fi

    print_info "Creating React production environment file"

    # If we're running in host network mode, build the frontend to call the API on localhost:API_PORT
    if [ "${NETWORK_MODE:-bridge}" = "host" ]; then
        local api_host_port=${API_PORT:-5245}
        cat > "$react_dir/.env.production" << EOF
# React Production Build Configuration (host network)
# Auto-generated by deploy-docker.sh
# Frontend will call the API on the host (localhost) when running in host network mode
VITE_API_BASE_URL=http://localhost:${api_host_port}/api

# SignalR hub URLs (point to host)
VITE_SIGNALR_PRINTERS_URL=http://localhost:${api_host_port}/hubs/printers
VITE_SIGNALR_HARVEST_URL=http://localhost:${api_host_port}/hubs/harvest
EOF
    else
        cat > "$react_dir/.env.production" << 'EOF'
# React Production Build Configuration
# Auto-generated by deploy-docker.sh
# These relative URLs work through the Nginx proxy in Docker deployment

# API base URL - relative path routes through Nginx
VITE_API_BASE_URL=/api

# SignalR hub URL - relative path routes through Nginx
VITE_SIGNALR_PRINTERS_URL=/hubs/printers
VITE_SIGNALR_HARVEST_URL=/hubs/harvest
EOF
    fi
    
    print_success "React production environment configured: $react_dir/.env.production"
}

# Generate docker-compose override if needed
generate_compose_override() {
    if [ "$ARCHITECTURE" = "microservices" ] && { [ "${INCLUDE_POSTGRES:-no}" = "yes" ] || [ "${INCLUDE_SQLSERVER:-no}" = "yes" ] || [ "${INCLUDE_MYSQL:-no}" = "yes" ]; }; then
        print_info "Creating docker-compose override for database services"
        
        cat > docker-compose.override.yml << EOF
# Auto-generated database services

services:
EOF
        
        if [ "${INCLUDE_POSTGRES:-no}" = "yes" ]; then
            cat >> docker-compose.override.yml << EOF
    postgres:
        image: postgres:15
    environment:
      - POSTGRES_DB=\${POSTGRES_DB}
      - POSTGRES_USER=\${POSTGRES_USER}
      - POSTGRES_PASSWORD=\${POSTGRES_PASSWORD}
    ports:
    - "${POSTGRES_PORT:-5432}:5432"
    volumes:
      - postgres_data:/var/lib/postgresql/data
      - ./scripts/docker/init-postgres.sh:/docker-entrypoint-initdb.d/01-init-auth.sh:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U \${POSTGRES_USER} -d \${POSTGRES_DB}"]
      interval: 30s
      timeout: 10s
      retries: 5
EOF
        fi
        
        if [ "${INCLUDE_SQLSERVER:-no}" = "yes" ]; then
            cat >> docker-compose.override.yml << EOF
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=\${MSSQL_SA_PASSWORD}
      - MSSQL_PID=\${MSSQL_PID:-Developer}
    ports:
      - "\${SQLSERVER_PORT:-1433}:1433"
    volumes:
      - sqlserver_data:/var/opt/mssql
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"\${MSSQL_SA_PASSWORD}\" -C -Q 'SELECT 1' || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 60s
EOF
        fi
        
        if [ "${INCLUDE_MYSQL:-no}" = "yes" ]; then
            cat >> docker-compose.override.yml << EOF
  mysql:
    image: mysql:8.0
    environment:
      - MYSQL_ROOT_PASSWORD=\${MYSQL_ROOT_PASSWORD}
      - MYSQL_DATABASE=\${MYSQL_DATABASE}
    ports:
      - "3306:3306"
    volumes:
      - mysql_data:/var/lib/mysql
    healthcheck:
      test: ["CMD", "mysqladmin", "ping", "-h", "localhost", "-u", "root", "-p\${MYSQL_ROOT_PASSWORD}"]
      interval: 30s
      timeout: 10s
      retries: 5
EOF
        fi
        
        cat >> docker-compose.override.yml << EOF

volumes:
EOF
        
        [ "${INCLUDE_POSTGRES:-no}" = "yes" ] && echo "  postgres_data:" >> docker-compose.override.yml
        [ "${INCLUDE_SQLSERVER:-no}" = "yes" ] && echo "  sqlserver_data:" >> docker-compose.override.yml
        [ "${INCLUDE_MYSQL:-no}" = "yes" ] && echo "  mysql_data:" >> docker-compose.override.yml
        
        print_success "Docker Compose override file created: docker-compose.override.yml"
        # Ensure no top-level `version:` key remains in generated override
        remove_version_keys "docker-compose.override.yml"
    else
        print_info "No database services needed - skipping override file generation"
    fi
}

# Generate host network override if needed
generate_host_network_override() {
    if [ "${NETWORK_MODE:-bridge}" = "host" ] && [ "$ARCHITECTURE" = "microservices" ]; then
        print_info "Creating complete host network compose file (standalone)"
        print_warning "This file includes ALL services with API configured for host networking"
        
        # Start the compose file
        cat > docker-compose.host-network.yml << 'MAINEOF'
# PrintFarmer Microservices Architecture - HOST NETWORK MODE
# Complete standalone compose file with API in host network mode
# DO NOT use with docker-compose.microservices.yml (conflicts due to network_mode)

services:


MAINEOF

        # Add the appropriate database service based on DB_PROVIDER
        case "${DB_PROVIDER:-postgres}" in
            postgres)
                cat >> docker-compose.host-network.yml << 'DBEOF'
  # PostgreSQL Database
    database:
        image: postgres:15
    environment:
      POSTGRES_DB: ${POSTGRES_DB:-printfarmer}
      POSTGRES_USER: ${POSTGRES_USER:-postgres}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-postgres}
    ports:
      - "5432:5432"
    networks:
      - printfarmer-network
    volumes:
      - postgres_data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-postgres} -d ${POSTGRES_DB:-printfarmer}"]
      interval: 10s
      timeout: 5s
      retries: 5

DBEOF
                ;;
            sqlserver)
                cat >> docker-compose.host-network.yml << 'DBEOF'
  # SQL Server Database
  database:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: ${SQLSERVER_PASSWORD}
      MSSQL_PID: ${MSSQL_PID:-Developer}
    ports:
      - "${SQLSERVER_PORT:-1433}:1433"
    networks:
      - printfarmer-network
    volumes:
      - sqlserver_data:/var/opt/mssql
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"${SQLSERVER_PASSWORD}\" -C -Q 'SELECT 1' || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 60s

DBEOF
                ;;
            mysql)
                cat >> docker-compose.host-network.yml << 'DBEOF'
  # MySQL Database
  database:
    image: mysql:8.0
    environment:
      MYSQL_ROOT_PASSWORD: ${MYSQL_PASSWORD}
      MYSQL_DATABASE: ${MYSQL_DB:-printfarmer}
      MYSQL_USER: ${MYSQL_USER:-printfarmer}
      MYSQL_PASSWORD: ${MYSQL_PASSWORD}
    ports:
      - "3306:3306"
    networks:
      - printfarmer-network
    volumes:
      - mysql_data:/var/lib/mysql
    healthcheck:
      test: ["CMD", "mysqladmin", "ping", "-h", "localhost"]
      interval: 30s
      timeout: 10s
      retries: 5

DBEOF
                ;;
        esac

        # Continue with the rest of the services (API, workers, frontend)
        cat >> docker-compose.host-network.yml << 'RESTEOF'
  # PrintFarmer API - Using HOST NETWORK MODE for full network discovery
  api:
    build:
      context: .
      dockerfile: Dockerfile.api
    image: printfarmer-api
    # HOST NETWORK MODE: Direct host network access (no ports/networks allowed)
    network_mode: "host"
    depends_on:
      database:
        condition: service_healthy
    restart: on-failure:5
    environment:
      - ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-Production}
      - ASPNETCORE_URLS=http://0.0.0.0:${API_PORT:-5245}
      - API_URL=http://localhost:${API_PORT:-5245}
      - DB_PROVIDER=${DB_PROVIDER:-Postgres}
      - ConnectionStrings__Default=${ConnectionStrings__Default}
      - CORS__AllowedOrigins=${CORS__AllowedOrigins:-http://localhost:3000,http://localhost:8080}
      - DOCKER_HOST_NETWORK=true
      - NETWORK_MODE=host
      - ALLOW_LOCAL_NETWORK=${ALLOW_LOCAL_NETWORK:-true}
      - ALLOWED_NETWORK_RANGES=${ALLOWED_NETWORK_RANGES:-192.168.0.0/16,10.0.0.0/8}
      - DEPLOYMENT_MODE=microservices
      - ModelStorage__Path=/app/models
      - Logging__LogLevel__Default=Information
      - Logging__LogLevel__Microsoft.AspNetCore=Warning
      - SlicerOrchestrator__EnableDistributedSlicing=${ENABLE_DISTRIBUTED_SLICING:-true}
      - SlicerOrchestrator__Workers__OrcaSlicer=${ORCA_WORKER_ENDPOINT:-http://localhost:8081}
      - PFARM__Spoolman__BaseUrl=${PFARM__Spoolman__BaseUrl:-}
      - PFARM__NetworkDiscovery__EnableDiscovery=${PFARM__NetworkDiscovery__EnableDiscovery:-true}
      - PFARM__NetworkDiscovery__DiscoverySubnets=${PFARM__NetworkDiscovery__DiscoverySubnets:-}
    volumes:
      - printfarmer-app-data:/data
      - printfarmer-model-storage:/app/models
      - printfarmer-gcode-storage:/app/gcode
      - printfarmer-slicer-profiles:/app/profiles
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:${API_PORT:-5245}/healthz"]
      interval: 30s
      timeout: 15s
      retries: 5
      start_period: 90s

  # OrcaSlicer Worker - Distributed slicing microservice
  orcaslicer-worker:
    build:
      context: .
      dockerfile: Dockerfile.multistage
      target: orcaslicer-worker
    profiles:
      - orca
    image: printfarmer-orcaslicer-worker
    ports:
      - "8081:8080"
    networks:
      - printfarmer-network
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:8080
      - Worker__StorageEndpoint=http://localhost:${API_PORT:-5245}
      - Worker__WorkingDirectory=/app/temp
      - Worker__OrcaSlicerPath=/usr/local/bin/orcaslicer
      - Worker__WorkerId=orcaslicer-worker-1
      - Worker__QueueName=orcaslicer-jobs
      - Logging__LogLevel__Default=Information
    volumes:
      - printfarmer-orcaslicer-temp:/app/temp
      - printfarmer-gcode-storage:/app/gcode
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 90s

  # React Frontend
  frontend:
    build:
      context: .
      dockerfile: Dockerfile.frontend-host  # Custom Dockerfile for host network mode
      args:
        VITE_API_BASE_URL: /api
        VITE_SIGNALR_PRINTERS_URL: /hubs/printers
        VITE_SIGNALR_HARVEST_URL: /hubs/harvest
    image: printfarmer-frontend-host
    ports:
      - "${HTTP_PORT:-8080}:80"
    networks:
      - printfarmer-network
    # CRITICAL for Linux: Map host.docker.internal to host gateway so Nginx can reach host-network API
    extra_hosts:
      - "host.docker.internal:host-gateway"
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:80/health"]
      interval: 30s
      timeout: 10s
      retries: 3
    # Nginx proxy (host-mode)
    nginx-proxy:
        image: printfarmer-nginx-host:latest
        container_name: printfarmer-nginx-proxy
        # Run in host network mode so it binds host ports directly
        network_mode: "host"
        # Use the generated host nginx config (bind mounts from repo when running compose)
        volumes:
            - ./deploy/nginx/nginx-frontend.conf:/etc/nginx/nginx.conf:ro
            - ./deploy/nginx/conf.d.host:/etc/nginx/conf.d:ro
        restart: unless-stopped
        healthcheck:
            test: ["CMD", "curl", "-f", "http://localhost/health"]
            interval: 30s
            timeout: 10s
            retries: 3

networks:
    printfarmer-network:
        driver: bridge

volumes:
    postgres_data:
    sqlserver_data:
    mysql_data:
    printfarmer-app-data:
    printfarmer-model-storage:
    printfarmer-gcode-storage:
    printfarmer-slicer-profiles:
    printfarmer-orcaslicer-temp:
RESTEOF
        
        print_success "Host network compose file created: docker-compose.host-network.yml"
        # Ensure no top-level `version:` key remains in generated host-network file
        remove_version_keys "docker-compose.host-network.yml"
        print_warning "API will bind directly to host port ${API_PORT:-5245}"
        print_warning "Database accessible on localhost (host networking)"
        print_info "Workers and frontend use bridge network, API uses host network"
        print_info "This file is standalone - do NOT combine with docker-compose.microservices.yml"
    fi
}

# Build and deploy
deploy_containers() {
    print_header "🚀 Building and Deploying Containers"
    
    # Source the .env file into the current shell so that environment variables
    # are available to the script and are properly passed to docker compose.
    # This ensures that health check commands can access variables like MSSQL_SA_PASSWORD.
    if [ -f "$ENV_FILE" ]; then
        print_info "Loading environment variables from $ENV_FILE"
        load_env_file
        ensure_database_passwords
        ensure_connection_string_password
        load_env_file
        sync_env_var_with_file "ConnectionStrings__Default"
        print_info "Environment loaded successfully"
    else
        print_warning "Environment file $ENV_FILE not found; some variables may be missing"
    fi
    
    print_info "Step 1/3: Building Docker images..."
    print_info "This may take several minutes on first run..."
    print_info "Build verbosity: $BUILD_VERBOSITY (set with --build-verbosity or --verbose-build)"
    # Always include selected compose file
    local compose_cmd=(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE")

    # For host network mode, we need special handling because networks and network_mode are mutually exclusive
    # We'll use ONLY the host-network compose file which has all services, skipping the base microservices file
    if [ -f docker-compose.host-network.yml ]; then
        # Use host-network file as the PRIMARY file (has all services with API in host mode)
        # DO NOT load override file - host-network.yml is standalone and already includes database
        compose_cmd=( docker compose --env-file "$ENV_FILE" -f docker-compose.host-network.yml )
        print_info "Using host network mode: docker-compose.host-network.yml (standalone, includes all services)"
    elif [ -f docker-compose.override.yml ]; then
        compose_cmd+=( -f docker-compose.override.yml )
    fi

    if [ "$DRY_RUN" = "true" ]; then
        print_info "Dry-run mode: skipping image build. (Would run: docker compose build)"
    else
        # ----- Prepare optional slicer assets ---------------------------------
        # ORCA_ASSET_IMAGE  -> name of a prebuilt assets image (registry or local)
        # ORCA_ASSET_PATH   -> local path containing extracted orcaslicer files (orca7z/ or orcaslicer-dist/)
        # ORCA_ASSET_URL    -> URL to download an asset (handled as needed)
        ORCA_ASSET_IMAGE=${ORCA_ASSET_IMAGE:-}
        ORCA_ASSET_PATH=${ORCA_ASSET_PATH:-}
        ORCA_ASSET_URL=${ORCA_ASSET_URL:-}

        # Prepare a temporary build_context folder that will be used by docker compose build
        BUILD_CTX_DIR="./.tmp_build_context"
        mkdir -p "$BUILD_CTX_DIR"

        # Ensure Dockerfile.multistage exists at repo root (primary source for all Docker builds)
        if [ ! -f "./Dockerfile.multistage" ]; then
            print_error "Dockerfile.multistage not found at repository root - required for consolidated OrcaSlicer builds"
            exit 1
        fi

        if [ -n "$ORCA_ASSET_IMAGE" ]; then
            print_info "Using Orca assets image: $ORCA_ASSET_IMAGE"
            # Only try to pull if image doesn't exist locally AND looks like a registry image
            if ! docker image inspect "$ORCA_ASSET_IMAGE" >/dev/null 2>&1; then
                # Check if it looks like a registry image (contains / or .)
                if [[ "$ORCA_ASSET_IMAGE" == *"/"* ]] || [[ "$ORCA_ASSET_IMAGE" == *"."* ]]; then
                    docker pull "$ORCA_ASSET_IMAGE" || print_warning "Failed to pull $ORCA_ASSET_IMAGE; continuing and hoping it's local"
                else
                    print_warning "Image $ORCA_ASSET_IMAGE not found locally and doesn't look like a registry image"
                fi
            else
                print_info "Image $ORCA_ASSET_IMAGE found locally (skipping pull)"
            fi
        elif [ -n "$ORCA_ASSET_PATH" ]; then
            if [ -d "$ORCA_ASSET_PATH" ]; then
                print_info "Copying Orca assets from $ORCA_ASSET_PATH into temporary build context"
                rm -rf "$BUILD_CTX_DIR/orca" || true
                mkdir -p "$BUILD_CTX_DIR/orca"
                cp -a "$ORCA_ASSET_PATH"/. "$BUILD_CTX_DIR/orca/"
            else
                print_warning "ORCA_ASSET_PATH '$ORCA_ASSET_PATH' not found; skipping"
            fi
        elif [ -n "$ORCA_ASSET_URL" ]; then
            print_info "Downloading Orca asset from $ORCA_ASSET_URL into temporary build context"
            mkdir -p "$BUILD_CTX_DIR/orca" && curl -fsSL "$ORCA_ASSET_URL" -o "$BUILD_CTX_DIR/orca/orca_asset" || print_warning "Download failed"
            # Extraction logic could be added here depending on asset type
        fi

        # If we prepared files into .tmp_build_context, make them available to docker-compose by copying into repo root under build_context/
        if [ -d "$BUILD_CTX_DIR" ]; then
            rm -rf ./build_context || true
            mv "$BUILD_CTX_DIR" ./build_context
            print_info "Prepared temporary build_context at ./build_context"
        fi

        # Build orcaslicer-binaries layer first if orca worker is enabled (optimized caching)
        # Note: All binary downloading and extraction is now consolidated in Dockerfile.multistage orcaslicer-binaries stage
        if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
            ORCA_VERSION="${ORCASLICER_VERSION:-2.3.1}"
            print_info "Building orcaslicer-binaries:${ORCA_VERSION} layer (optimized caching via Dockerfile.multistage)..."
            
            # Build binary layer with automatic download and extraction
            BUILD_ARGS="--build-arg ORCASLICER_VERSION=${ORCA_VERSION} --build-arg ALLOW_STUB=false"

            # Add GitHub token if available (to avoid rate limits)
            if [ -n "${GITHUB_TOKEN:-}" ]; then
                BUILD_ARGS="$BUILD_ARGS --build-arg GITHUB_TOKEN=${GITHUB_TOKEN}"
            fi

            # If caller supplied a prebuilt ORCA_ASSET_IMAGE and it exists locally, tag and skip building
            if [ -n "${ORCA_ASSET_IMAGE:-}" ]; then
                if docker image inspect "${ORCA_ASSET_IMAGE}" >/dev/null 2>&1; then
                    print_info "Found local ORCA_ASSET_IMAGE: ${ORCA_ASSET_IMAGE} - tagging for use and skipping build"
                    docker tag "${ORCA_ASSET_IMAGE}" "orcaslicer-binaries:${ORCA_VERSION}" || true
                    docker tag "${ORCA_ASSET_IMAGE}" "orcaslicer-binaries:latest" || true
                    # Mark to skip the build step
                    export _PF_SKIP_ORCA_BUILD=1
                else
                    print_info "ORCA_ASSET_IMAGE set to ${ORCA_ASSET_IMAGE} but image not found locally; will attempt build"
                fi
            fi
            
            # Ensure a root-level Dockerfile.multistage exists for build commands
            if [ ! -f "./Dockerfile.multistage" ]; then
                print_error "Dockerfile.multistage not found - required for OrcaSlicer builds"
                exit 1
            fi

            ORCA_BUILD_CMD=(docker build)
            if [ -n "${DOCKER_BUILD_PLATFORM:-}" ]; then
                ORCA_BUILD_CMD+=(--platform "${DOCKER_BUILD_PLATFORM}")
            fi
            ORCA_BUILD_CMD+=(-f Dockerfile.multistage --target orcaslicer-binaries -t "orcaslicer-binaries:${ORCA_VERSION}" -t "orcaslicer-binaries:latest" $BUILD_ARGS .)

            if [ "${_PF_SKIP_ORCA_BUILD:-0}" = "1" ]; then
                print_success "Skipping orcaslicer-binaries build (using prebuilt image)"
            else
                if "${ORCA_BUILD_CMD[@]}"; then
                    print_success "orcaslicer-binaries:${ORCA_VERSION} layer built successfully (cached for future builds)"
                else
                    print_error "Failed to build orcaslicer-binaries:${ORCA_VERSION} layer"
                    print_error "This layer contains the OrcaSlicer binary and will be cached for optimal build performance"
                    exit 1
                fi
            fi
        fi

        # Note: slicer-base stage is now part of Dockerfile.multistage (orcaslicer-worker target)
        # No separate build needed - docker compose build will handle it automatically
        
        # If we have a prebuilt orcaslicer-binaries image, create an override compose file
        # that uses additional_contexts to make Docker use the cached image instead of rebuilding
        ORCA_OVERRIDE_FILE=""
        cleanup_orca_override() {
            if [ -n "${ORCA_OVERRIDE_FILE:-}" ] && [ -f "${ORCA_OVERRIDE_FILE}" ]; then
                rm -f "${ORCA_OVERRIDE_FILE}"
            fi
        }
        # Build command - may include override file for orcaslicer binaries caching
        local build_compose_cmd=("${compose_cmd[@]}")
        if [ "${_PF_SKIP_ORCA_BUILD:-0}" = "1" ] && [ -n "${ORCA_VERSION:-}" ]; then
            ORCA_OVERRIDE_FILE="${SCRIPT_DIR}/.orca-binaries-override.yml"
            cat > "${ORCA_OVERRIDE_FILE}" << EOF
# Auto-generated: Use prebuilt orcaslicer-binaries image instead of building
services:
  orcaslicer-worker:
    build:
      additional_contexts:
        orcaslicer-binaries: docker-image://orcaslicer-binaries:${ORCA_VERSION}
EOF
            print_info "Using prebuilt orcaslicer-binaries:${ORCA_VERSION} (via additional_contexts override)"
            # Add override file to BUILD command only (not the main compose_cmd used for 'up')
            build_compose_cmd+=(-f "${ORCA_OVERRIDE_FILE}")
            # Ensure cleanup on exit
            trap cleanup_orca_override EXIT
        fi
        
        # Now build all services
        # Support passing --platform to docker compose build when requested
        if [ -n "${DOCKER_BUILD_PLATFORM:-}" ]; then
            print_info "Attempting docker compose build with platform ${DOCKER_BUILD_PLATFORM}"
            # Try using the --platform flag first (supported on modern compose). If it fails
            # (for example older compose binary that reports unknown flag), fall back to
            # setting DOCKER_DEFAULT_PLATFORM and retrying without the flag.
            if "${build_compose_cmd[@]}" build --progress=plain --build-arg BUILD_VERBOSITY="${BUILD_VERBOSITY}" --platform "${DOCKER_BUILD_PLATFORM}"; then
                print_success "Docker images built successfully"
            else
                print_warning "docker compose build --platform failed; retrying with DOCKER_DEFAULT_PLATFORM fallback"
                export DOCKER_DEFAULT_PLATFORM="${DOCKER_BUILD_PLATFORM}"
                if "${build_compose_cmd[@]}" build --progress=plain --build-arg BUILD_VERBOSITY="${BUILD_VERBOSITY}"; then
                    print_success "Docker images built successfully (using DOCKER_DEFAULT_PLATFORM=${DOCKER_BUILD_PLATFORM})"
                else
                    print_error "Failed to build Docker images (even with DOCKER_DEFAULT_PLATFORM)"
                    print_error "For detailed build logs, run: ./debug-docker-build.sh"
                    exit 1
                fi
            fi
        else
            if "${build_compose_cmd[@]}" build --progress=plain --build-arg BUILD_VERBOSITY="${BUILD_VERBOSITY}"; then
                print_success "Docker images built successfully"
            else
                print_error "Failed to build Docker images"
                print_error "For detailed build logs, run: ./debug-docker-build.sh"
                print_info "To force a clean rebuild, run: ./scripts/deploy-docker.sh --tear-down --non-interactive"
                exit 1
            fi
        fi
        
        # Clean up the temporary override file if it was created
        cleanup_orca_override
    fi
    
    print_info "Step 2/3: Starting containers..."
    print_info "Bringing up services with configuration from $ENV_FILE"

    # Activate profiles for enabled workers (compose v2 profiles)
    # Build complete compose command with profiles BEFORE the 'up' subcommand
    local final_compose_cmd=("${compose_cmd[@]}")
    
    if [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -gt 0 ]; then
        final_compose_cmd+=(--profile orca)
    fi

    # Bring up services
    if [ "$DRY_RUN" = "true" ]; then
        print_info "Dry-run mode: not starting containers."
        print_info "Would run: ${final_compose_cmd[*]} up -d"
    else
        # If microservices architecture, start the database first to speed up readiness
        if [ "$ARCHITECTURE" = "microservices" ]; then
            print_info "Bringing up database service first to speed readiness"
            # Attempt to start postgres/mysql/sqlserver only
            local seed_cmd=("")
            # Build a minimal compose command for core infra
            local infra_compose=(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE")
            if [ -f docker-compose.override.yml ]; then
                infra_compose+=( -f docker-compose.override.yml )
            fi

            # Decide which services to start
            local infra_services=(database)
            # Note: Using generic 'database' service name that supports multiple providers via environment variables

            # Run infra services up
            # Optionally include --remove-orphans
            local remove_orphans_flag=""
            if [ "${COMPOSE_REMOVE_ORPHANS}" = "true" ] || [ "${COMPOSE_REMOVE_ORPHANS}" = "1" ]; then
                remove_orphans_flag="--remove-orphans"
            fi

            # Preflight: if starting database with SQL Server provider, ensure host port is free to avoid Docker bind errors
            if echo " ${infra_services[*]} " | grep -q " database " && [ "${DB_PROVIDER:-postgres}" = "sqlserver" ]; then
                local sql_host_port=${SQLSERVER_PORT:-1433}
                if nc -z localhost "$sql_host_port" 2>/dev/null; then
                    print_warning "SQL Server host port $sql_host_port is already in use. Attempting to identify owner..."

                    # Try to find a container listening on that port
                    local owner_container
                    owner_container=$(docker ps --format '{{.Names}} {{.Ports}}' | grep ":${sql_host_port}->" | awk '{print $1}' | head -n1 || true)

                    if [ -n "$owner_container" ]; then
                        print_info "Port $sql_host_port appears bound by container: $owner_container"
                        if [ "$NON_INTERACTIVE" = "true" ]; then
                                    if [ "${COMPOSE_REMOVE_ORPHANS:-true}" = "true" ]; then
                                        print_info "Non-interactive: removing container $owner_container"
                                        docker rm -f "$owner_container" || true
                                        audit_log "remove" "preflight: removed owner container $owner_container binding port $sql_host_port"
                                    else
                                        print_error "Non-interactive and COMPOSE_REMOVE_ORPHANS=false: cannot auto-remove $owner_container. Exiting."
                                        exit 3
                                    fi
                                else
                            # Interactive prompt: ask to remove only that container
                            echo
                            print_info "Remove container $owner_container that is binding port $sql_host_port? (y/N)"
                            read -r resp || true
                            if [[ "$resp" =~ ^([yY][eE][sS]|[yY])$ ]]; then
                                docker rm -f "$owner_container" || true
                                audit_log "remove" "preflight: removed owner container $owner_container binding port $sql_host_port"
                                print_success "Removed $owner_container"
                            else
                                print_error "Please free port $sql_host_port or change SQLSERVER_PORT in your configuration. Aborting."
                                exit 3
                            fi
                        fi
                    else
                        print_error "No container owner found for port $sql_host_port; it may be a host process."
                        print_info "Diagnostic: sudo lsof -nP -iTCP:$sql_host_port -sTCP:LISTEN"
                        exit 3
                    fi
                fi
            fi

            if "${infra_compose[@]}" up -d ${remove_orphans_flag} "${infra_services[@]}"; then
                print_success "Database service started"
            else
                print_warning "Failed to start infra services - continuing to full bring-up"
            fi

            # Wait for DB health/readiness
            if ! wait_for_database; then
                print_error "Database failed to become healthy. Cannot proceed with deployment."
                return 1
            fi

            # Detect orphan containers left by compose and suggest removal if any exist
            local orphan_list
            orphan_list=$(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" ps --quiet --all 2>/dev/null | xargs -r docker inspect --format '{{.Name}} {{.State.Status}}' 2>/dev/null | grep -E "orphan|Exited|Created" || true)
            if [ -n "$orphan_list" ]; then
                print_warning "Found orphan or leftover containers that may interfere with startup:"
                echo "$orphan_list"
                print_info "Suggestion: run with --remove-orphans or manually remove the listed containers:"
                echo "  docker compose --env-file $ENV_FILE -f $COMPOSE_FILE up -d --remove-orphans"
            fi

            # Start API first, wait for it to become healthy, then start remaining services
            print_info "Starting API service first so it can initialize before frontend/workers"
            if "${final_compose_cmd[@]}" up -d api; then
                print_success "API container started (initial)"
            else
                print_warning "Failed to start API alone; will attempt full bring-up for all services"
            fi

            # Wait for API health endpoint before bringing up UI and workers
            if wait_for_api; then
                print_success "API reported Healthy via /health - starting remaining services"
            else
                print_warning "API did not become healthy within timeout. Proceeding to start remaining services anyway. Monitor API logs for issues."
            fi

            # Now start the remaining services (frontend, workers, etc.)
            if "${final_compose_cmd[@]}" up -d; then
                print_success "All containers started successfully"
                
                # For microservices architecture, nginx-proxy is part of the docker-compose stack
                # and runs on the docker network in bridge mode. Don't try to start a host-mode proxy.
                # For monolithic with host mode, we may need a separate host-mode nginx proxy.
                if [ "$ARCHITECTURE" = "microservices" ]; then
                    # For microservices, just verify the docker-compose nginx-proxy is working
                    if check_nginx_proxy; then
                        print_info "nginx proxy verification passed"
                    else
                        print_error "nginx proxy verification FAILED - aborting deployment"
                        exit 2
                    fi
                elif [ "${NETWORK_MODE:-bridge}" = "host" ]; then
                    # For monolithic with host mode, start a host-mode nginx proxy
                    start_host_mode_nginx_proxy || true
                    if check_nginx_proxy; then
                        print_info "nginx proxy verification passed"
                    else
                        print_error "nginx proxy verification FAILED - aborting deployment"
                        exit 2
                    fi
                fi
            else
                print_error "Failed to start containers"
                exit 1
            fi
        else
            if "${final_compose_cmd[@]}" up -d; then
                print_success "Containers started successfully"
            else
                print_error "Failed to start containers"
                exit 1
            fi
        fi
    fi

    # Scaling (only if counts >1). Use service names; if profiles not enabled skip scaling.
    if [ "$DRY_RUN" != "true" ] && [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -gt 1 ]; then
        print_info "Scaling OrcaSlicer workers to $ORCA_WORKER_COUNT replicas"
        "${final_compose_cmd[@]}" up -d --scale orcaslicer-worker="$ORCA_WORKER_COUNT"
    fi
    
    if [ "$DRY_RUN" = "true" ]; then
        print_info "Dry-run complete. No containers launched."
    else
        print_success "Step 3/3: Containers starting..."
        print_info "Waiting for all services to be healthy..."
        
        # Wait for containers to be healthy (with timeout)
        local max_wait=120  # 2 minutes total
        local wait_interval=5
        local elapsed=0
        local all_healthy=false
        
        while [ $elapsed -lt $max_wait ]; do
            # Check if all containers are healthy
        local unhealthy_count=$(dc ps --format json 2>/dev/null | grep -E '"Health":"(starting|unhealthy)"' | wc -l | tr -d ' ')
            
            if [ "$unhealthy_count" -eq 0 ]; then
                all_healthy=true
                print_success "All containers are healthy!"
                break
            fi
            
            # Show progress
            if [ $((elapsed % 15)) -eq 0 ]; then
                print_info "Still waiting for services to become healthy... ($elapsed seconds elapsed)"
                dc ps --format "table {{.Name}}\t{{.Status}}" 2>/dev/null | grep -E "starting|unhealthy" || true
            fi
            
            sleep $wait_interval
            elapsed=$((elapsed + wait_interval))
        done
        
        if [ "$all_healthy" = false ]; then
            print_warning "Some services may still be starting after ${max_wait}s. Checking detailed status..."
        fi
    fi
}


# Wait for database service to become healthy. Uses docker compose health status when available
wait_for_database() {
    # Only relevant for microservices where DB runs in compose
    if [ "$ARCHITECTURE" != "microservices" ]; then
        return 0
    fi

    print_info "Waiting for database service to be healthy (timeout: ${DB_WAIT_TIMEOUT:-300}s, increased for SQL Server)..."
    local timeout=${DB_WAIT_TIMEOUT:-300}
    local interval=3
    local elapsed=0

    # Use generic database service name (all providers use the same service)
    local db_service="database"

    while [ $elapsed -lt $timeout ]; do
        # Use docker compose ps JSON to look for Health or rely on container's port availability
        # Prefer checking container health status if available
        local health_state
        health_state=$(dc ps --format json 2>/dev/null | grep -o '"Name":"[^\"]*' | grep -o '[^\"]*$' | while read -r name; do
            # Match service by suffix
            if echo "$name" | grep -q "$db_service"; then
                docker inspect --format='{{json .State.Health.Status}}' "$name" 2>/dev/null || echo "unknown"
            fi
        done | head -n1 | tr -d '"') || true

        if [ "$health_state" = "healthy" ]; then
            print_success "Database ($db_service) reports healthy"
            return 0
        fi

        # As fallback, confirm the database is accepting connections
        if [ "${DB_PROVIDER:-postgres}" = "postgres" ]; then
            if postgres_readiness_check; then
                print_success "PostgreSQL is accepting connections"
                return 0
            fi
        elif [ "${DB_PROVIDER:-postgres}" = "sqlserver" ]; then
            if nc -z localhost ${SQLSERVER_PORT:-1433} 2>/dev/null; then
                print_success "SQL Server port ${SQLSERVER_PORT:-1433} reachable on localhost"
                return 0
            fi
        elif [ "${DB_PROVIDER:-postgres}" = "mysql" ]; then
            if nc -z localhost 3306 2>/dev/null; then
                print_success "MySQL port 3306 reachable on localhost"
                return 0
            fi
        fi

        if [ $((elapsed % 15)) -eq 0 ]; then
            print_info "Still waiting for DB to become available... ($elapsed/$timeout seconds)"
            dc ps --format "table {{.Name}}\t{{.Status}}" 2>/dev/null | grep -E "starting|unhealthy|health" || true
        fi

        sleep $interval
        elapsed=$((elapsed + interval))
    done

    # Timeout reached - provide detailed diagnostics
    print_error "🔴 DATABASE HEALTH CHECK FAILED"
    print_error "Database did not become healthy within ${timeout}s timeout."
    print_error ""
    print_error "📊 DIAGNOSTIC INFORMATION:"
    print_error ""
    
    # Show container status
    print_error "Container Status:"
    dc ps --format "table {{.Name}}\t{{.Status}}\t{{.Health}}" 2>/dev/null || true
    print_error ""
    
    # Show logs from database container
    print_error "Recent Database Logs (last 50 lines):"
    dc logs database --tail 50 2>/dev/null || true
    print_error ""
    
    # SQL Server specific diagnostics
    if [ "${DB_PROVIDER:-postgres}" = "sqlserver" ]; then
        print_error "🔍 SQL SERVER SPECIFIC CHECKS:"
        print_error "- SA password complexity: Ensure MSSQL_SA_PASSWORD meets requirements"
        print_error "  (minimum 8 chars, uppercase, lowercase, number, special char)"
        print_error "- Check if port 1433 is in use: sudo lsof -i :1433"
        print_error "- Verify SA_PASSWORD in .env is correct: grep MSSQL_SA_PASSWORD .env"
        print_error ""
        print_error "Try restarting with a new strong password:"
        print_error "  rm .env docker-compose.override.yml 2>/dev/null"
        print_error "  ./scripts/deploy-docker.sh  # Let script generate new password"
    fi
    
    # Generic diagnostics
    print_error "🔧 TROUBLESHOOTING STEPS:"
    print_error "1. Check available disk space: df -h"
    print_error "2. Verify Docker daemon is running: docker ps"
    print_error "3. Check for port conflicts:"
    print_error "   - PostgreSQL: sudo lsof -i :5432"
    print_error "   - SQL Server: sudo lsof -i :1433"
    print_error "   - MySQL: sudo lsof -i :3306"
    print_error "4. Check Docker logs for the database container:"
    print_error "   docker compose logs database"
    print_error "5. Increase timeout if on slow system:"
    print_error "   DB_WAIT_TIMEOUT=600 ./scripts/deploy-docker.sh"
    print_error "6. Clean up and retry:"
    print_error "   docker compose down -v"
    print_error "   ./scripts/deploy-docker.sh"
    print_error ""
    
    return 1
}


# List candidate orphan containers for the current compose project
list_orphan_containers() {
    # Return containers that are associated with the compose project label but not present in compose ps
    local project_label
    project_label=$(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" ps --format '{{.Name}}' 2>/dev/null | sed 's/\///' | awk -F'_' '{print $1}' | head -n1 || true)
    # Fallback label
    project_label=${project_label:-pfarm}

    # Compose-known names
    docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" ps --format '{{.Name}}' 2>/dev/null | sort > /tmp/compose_names.txt || true
    # All docker names with compose project label
    docker ps -a --filter "label=com.docker.compose.project=$project_label" --format '{{.Names}}' | sort > /tmp/docker_names.txt || true

    comm -23 /tmp/docker_names.txt /tmp/compose_names.txt || true
}

# Prompt to remove listed orphan containers (interactive)
prompt_remove_orphans() {
    local orphans
    orphans=$(list_orphan_containers)
    if [ -z "$orphans" ]; then
        return 0
    fi

    print_warning "Detected potential orphan containers:\n$orphans"
    if [ "$NON_INTERACTIVE" = "true" ]; then
        if [ "${COMPOSE_REMOVE_ORPHANS:-true}" = "true" ]; then
            print_info "Non-interactive and COMPOSE_REMOVE_ORPHANS=true: removing orphans"
            removed=""
            while IFS= read -r c; do
                docker rm -f "$c" || true
                removed="$removed $c"
            done <<< "$orphans"
            audit_log "remove" "non-interactive: removed orphan containers:$removed"
            return 0
        else
            print_info "Non-interactive and COMPOSE_REMOVE_ORPHANS=false: skipping orphan removal"
            return 0
        fi
    fi

    echo
    print_info "Would you like to remove these orphan containers?"
    select opt in "Remove all" "Show logs" "Skip"; do
        case $opt in
            "Remove all")
                removed=""
                while IFS= read -r c; do
                    docker rm -f "$c" || true
                    removed="$removed $c"
                done <<< "$orphans"
                audit_log "remove" "interactive: removed orphan containers:$removed"
                print_success "Removed orphan containers"
                break
                ;;
            "Show logs")
                while IFS= read -r c; do
                    print_info "Logs for $c:"; docker logs --tail 50 "$c" || true; echo; done <<< "$orphans"
                ;;
            "Skip")
                print_info "Skipping orphan removal"
                break
                ;;
        esac
    done
}

fetch_api_health_summary() {
    local base_url="$1"
    local status_var="$2"
    local desc_var="$3"
    local http_var="$4"
    local payload_var="$5"

    local health_url="${base_url%/}/health"
    local raw
    raw=$(curl -s --max-time 5 "$health_url" -w "\n%{http_code}" 2>/dev/null || true)

    if [ -z "$raw" ]; then
        printf -v "$status_var" ''
        printf -v "$desc_var" ''
        printf -v "$http_var" '000'
        printf -v "$payload_var" ''
        return 1
    fi

    local http_code
    http_code=$(printf '%s\n' "$raw" | tail -n1 | tr -d '\r')
    local body
    body=$(printf '%s\n' "$raw" | sed '$d')

    local status desc
    status=$(printf '%s' "$body" | grep -o '"status":"[^"]*"' | head -n1 | cut -d '"' -f4)
    desc=$(printf '%s' "$body" | grep -o '"description":"[^"]*"' | head -n1 | cut -d '"' -f4)

    printf -v "$status_var" '%s' "$status"
    printf -v "$desc_var" '%s' "$desc"
    printf -v "$http_var" '%s' "$http_code"
    printf -v "$payload_var" '%s' "$body"

    if [ -z "$body" ]; then
        return 1
    fi

    if [ -z "$status" ]; then
        return 2
    fi

    return 0
}

wait_for_api() {
    print_info "Waiting for API to become healthy (validated via /health)..."
    local timeout=${API_WAIT_TIMEOUT:-180}
    local interval=3
    local elapsed=0
    local api_base="http://localhost:${API_PORT:-5245}"
    local healthz_url="${api_base}/healthz"
    local last_detail_log=-999

    while [ $elapsed -lt $timeout ]; do
        # Check if API container is actually running
        local container_state=$(docker compose ps -q api 2>/dev/null | xargs -I {} docker inspect -f '{{.State.Running}}' {} 2>/dev/null || echo "false")
        if [ "$container_state" = "false" ]; then
            # Container is not running - check if it exited with an error
            local exit_code=$(docker compose ps -q api 2>/dev/null | xargs -I {} docker inspect -f '{{.State.ExitCode}}' {} 2>/dev/null || echo "0")
            if [ "$exit_code" != "0" ]; then
                print_error "API container exited with error code $exit_code (not running)"
                run_api_diagnostics "🩺 API Startup Diagnostics"
                print_info "Recent API container logs:"
                docker compose logs api --tail 100 2>/dev/null || true
                echo
                exit 2
            fi
        fi

        local health_status="" health_desc="" health_http="" health_payload=""
        if fetch_api_health_summary "$api_base" health_status health_desc health_http health_payload; then
            if [ "$health_status" = "Healthy" ]; then
                print_success "API /health reports Healthy (HTTP ${health_http:-200})"
                return 0
            fi

            if [ $((elapsed - last_detail_log)) -ge 15 ]; then
                print_warning "API responded but dependencies still initializing (status=${health_status:-unknown}, http=${health_http:-n/a})."
                [ -n "$health_desc" ] && print_info "Health description: $health_desc"
              
                if [ -n "$health_payload" ]; then
                    print_info "Latest /health payload (truncated):"
                    printf '%s\n' "$health_payload" | head -n 20
                fi
                last_detail_log=$elapsed
            fi
        else
            if curl -sf "$healthz_url" >/dev/null 2>&1; then
                if [ $((elapsed - last_detail_log)) -ge 15 ]; then
                    print_info "Quick /healthz responded but /health not ready yet. Waiting for database connectivity..."
                    last_detail_log=$elapsed
                fi
            fi
        fi

        if [ $((elapsed % 15)) -eq 0 ]; then
            print_info "Still waiting for API to be fully healthy... ($elapsed/$timeout seconds)"
            dc ps --format "table {{.Name}}\t{{.Status}}" 2>/dev/null | grep -E "api|frontend|orcaslicer" || true
        fi

        sleep $interval
        elapsed=$((elapsed + interval))
    done

    print_warning "Timeout waiting for API comprehensive health after ${timeout}s. Proceeding with deployment but UI may show errors until API stabilizes."
    local fail_on_timeout=${API_FAIL_ON_TIMEOUT:-true}

    if [ "$fail_on_timeout" = "true" ] || [ "$fail_on_timeout" = "1" ]; then
        run_api_diagnostics "🩺 API Startup Diagnostics"
        print_error "API did not reach Healthy status within ${timeout}s and API_FAIL_ON_TIMEOUT is enabled. Failing deployment."
        echo
        print_info "Useful diagnostic commands to investigate the API container:"
        echo "  docker compose --env-file $ENV_FILE ps"
        echo "  docker compose --env-file $ENV_FILE logs api --no-color --tail 200"
        echo "  docker compose --env-file $ENV_FILE logs api -f"
        echo "  docker compose --env-file $ENV_FILE exec api sh -c 'ls -la /app'  # inspect container filesystem"
        echo "  docker compose --env-file $ENV_FILE exec api sh -c 'cat /app/logs/*.log 2>/dev/null || true'"
        echo "  docker compose --env-file $ENV_FILE up -d --build api  # rebuild and restart API"
        echo
        exit 2
    fi

    run_api_diagnostics "🩺 API Startup Diagnostics"
    return 1
}

# Lightweight check to validate that the browser-facing origin (nginx-proxy)
# correctly forwards /api requests to the API service. This is useful to
# catch misconfigured nginx upstreams (e.g. stale 8080 references) early.
check_nginx_proxy() {
    # Only check for microservices deployments where nginx-proxy is expected
    if [ "$ARCHITECTURE" != "microservices" ]; then
        return 0
    fi

    local proxy_host="localhost"
    local proxy_port=${HTTP_PORT:-8080}
    local proxy_url="http://$proxy_host:$proxy_port/api/healthz"
    print_info "Verifying nginx proxy forwards /api to API: $proxy_url"

    # For microservices in bridge mode, verify the docker-compose nginx-proxy
    # (which is what we're using for microservices deployments)
    local retries=${PROXY_VERIFY_RETRIES:-6}
    local interval=${PROXY_VERIFY_INTERVAL:-5}
    local attempt=1

    while [ $attempt -le $retries ]; do
        if curl -sf --max-time 3 "$proxy_url" >/tmp/_proxy_check 2>/dev/null; then
            if grep -q '"status"' /tmp/_proxy_check || grep -q '^OK$' /tmp/_proxy_check; then
                print_success "✓ nginx proxy appears to forward /api to API (HTTP ${proxy_port})"
                rm -f /tmp/_proxy_check || true
                return 0
            else
                print_info "  Proxy returned non-JSON/OK response (attempt ${attempt}/${retries})"
                sed -n '1,40p' /tmp/_proxy_check || true
            fi
        else
            print_info "  No response from proxy (attempt ${attempt}/${retries})"
        fi
        attempt=$((attempt + 1))
        sleep $interval
    done

    print_warning "✗ nginx proxy check failed after ${retries} attempts. Check /deploy/nginx/nginx-microservices.conf and ensure upstream points to api:5245"
    print_info "Example checks:"
    print_info "  docker compose --env-file $ENV_FILE -f $COMPOSE_FILE ps"
    print_info "  docker compose --env-file $ENV_FILE -f $COMPOSE_FILE logs nginx-proxy --tail 50"
    print_info "  docker exec printfarmer-nginx-proxy nginx -T | sed -n '1,200p'"
    return 1
}

# Perform automatic initial admin setup
setup_initial_admin() {
    log_header "🔐 Setting Up Initial Admin User"

    if [ "$AUTO_ADMIN" != "true" ]; then
        log_info "Auto-admin setup disabled (use --auto-admin to enable)"
        return 0
    fi

    # Determine API URL (Docker-specific logic)
    local api_url
    if [ -n "${DOCKER_PORT:-}" ]; then
        api_url="http://localhost:${DOCKER_PORT}"
    else
        api_url="http://localhost:5245"
    fi

    # Wait for API to be ready (Docker-specific wait logic)
    log_info "Waiting for API to be ready at $api_url/api/setup/status..."
    local max_attempts=30
    local attempt=0
    while [ $attempt -lt $max_attempts ]; do
        if curl -s -f -m 2 "$api_url/api/setup/status" > /dev/null 2>&1; then
            log_success "API is ready"
            break
        fi
        attempt=$((attempt + 1))
        if [ $attempt -lt $max_attempts ]; then
            sleep 2
        fi
    done

    if [ $attempt -ge $max_attempts ]; then
        log_error "API did not become ready after $((max_attempts * 2)) seconds"
        return 1
    fi

    # Use provided credentials
    local admin_username="${AUTO_ADMIN_USERNAME:-admin}"
    local admin_password="${AUTO_ADMIN_PASSWORD:-}"
    local admin_email="${AUTO_ADMIN_EMAIL:-admin@printfarmer.local}"

    # Generate a strong password if not provided
    if [ -z "$admin_password" ]; then
        admin_password=$(openssl rand -base64 16 2>/dev/null | tr -d '=+/' | cut -c1-16 || echo "PrintFarmer2025!")
        log_info "Generated admin password: $admin_password"
    fi

    # Call the common function to create admin via API
    if create_initial_admin "$api_url" "$admin_username" "$admin_password" "$admin_email"; then
        if [ "${AUTO_ADMIN_PASSWORD:-}" = "" ]; then
            log_warn "Save the generated password: $admin_password"
        fi
        return 0
    else
        return 1
    fi
}

# Verify deployment
verify_deployment() {
    print_header "🔍 Verifying Deployment"

    if [ "$DRY_RUN" = "true" ]; then
        print_info "Dry-run mode: skipping live deployment verification."
        return 0
    fi
    
    local api_url="http://localhost:$HTTP_PORT"
    if [ "$ARCHITECTURE" = "microservices" ]; then
        local direct_api_url="http://localhost:$API_PORT"
    fi
    
    print_info "Checking container status..."
    dc ps
    echo
    
    print_info "Running comprehensive health checks..."
    local health_check_failed=false
    
    # Test basic health endpoint
    print_info "Testing basic health endpoint..."
    local basic_health=$(curl -s "$api_url/healthz" 2>/dev/null)
    if [ -n "$basic_health" ] && (echo "$basic_health" | grep -q '"status":"ok"' || echo "$basic_health" | grep -q '^OK$'); then
        print_success "✓ Basic health check: OK"
    else
        print_warning "✗ Basic health check: FAILED (endpoint not responding or unexpected response)"
        if [ -n "$basic_health" ]; then
            print_info "Expected: JSON with \"status\":\"ok\" or plain OK"
            print_info "Actual: $basic_health"
        fi
        health_check_failed=true
    fi
    
    # Test comprehensive health endpoint
    print_info "Testing comprehensive health endpoint..."
    local health_json=$(curl -s "$api_url/health" 2>/dev/null)
    
    if [ -n "$health_json" ]; then
        # Check if it's JSON or simple text response
        if echo "$health_json" | grep -q '^{'; then
            # JSON response
            local health_status=$(echo "$health_json" | grep -o '"status":"[^"]*"' | head -1 | cut -d '"' -f4)
            
            if [ "$health_status" = "Healthy" ]; then
                print_success "✓ Comprehensive health check: Healthy"
                
                # Parse and display key health metrics
                if command -v jq >/dev/null 2>&1; then
                    print_info "Health check details:"
                    echo "$health_json" | jq -r '
                        .results | to_entries[] | 
                        "  • \(.key): \(.value.description // .value.status // "OK")"
                    ' 2>/dev/null || true
                fi
            else
                print_warning "✗ Comprehensive health check: Status = ${health_status:-unknown}"
                print_info "Expected: JSON with \"status\":\"Healthy\" and detailed results"
                print_info "Actual response:"
                if command -v jq >/dev/null 2>&1; then
                    echo "$health_json" | jq '.' 2>/dev/null || echo "$health_json"
                else
                    echo "$health_json"
                fi
                health_check_failed=true
            fi
        elif echo "$health_json" | grep -q '^OK$'; then
            # Simple "OK" response
            print_success "✓ Comprehensive health check: OK"
        else
            print_warning "✗ Comprehensive health check: Unexpected response"
            print_info "Full health check result:"
            echo "$health_json"
            health_check_failed=true
        fi
    else
        print_warning "✗ Comprehensive health check: FAILED (no response)"
        
        # Retry once after brief delay
        print_info "Retrying after 5 seconds..."
        sleep 5
        health_json=$(curl -s "$api_url/health" 2>/dev/null)
        if [ -n "$health_json" ] && echo "$health_json" | grep -q '"status":"Healthy"'; then
            print_success "✓ Comprehensive health check: OK (after retry)"
        else
            print_warning "✗ Still failing - services may need more time to start"
            print_info "Tip: Run 'docker compose --env-file $ENV_FILE logs api' to see API logs"
            health_check_failed=true
        fi
    fi
    
    # Test API endpoints (catalog endpoint - reliable and doesn't require printers to exist)
    print_info "Testing API endpoints..."
    local endpoint_response=$(curl -s -w "\n%{http_code}" "$api_url/api/catalog/manufacturers" 2>&1)
    local endpoint_body=$(echo "$endpoint_response" | head -n -1)
    local endpoint_status=$(echo "$endpoint_response" | tail -n 1)
    
    if [ "$endpoint_status" = "200" ]; then
        # Count manufacturers to verify data is present
        local mfr_count=$(echo "$endpoint_body" | jq 'length' 2>/dev/null || echo "?")
        print_success "✓ API endpoints: OK (/api/catalog/manufacturers - $mfr_count manufacturers)"
    else
        print_warning "✗ API endpoints: Failed"
        print_info "  HTTP Status: $endpoint_status"
        if [ -n "$endpoint_body" ]; then
            # Show first line of error response
            local error_line=$(echo "$endpoint_body" | head -n 1)
            print_info "  Response: ${error_line:0:120}"
        fi
        health_check_failed=true
    fi
    
    # Test worker health if enabled
    if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
        print_info "Testing OrcaSlicer worker..."
        local orca_url="http://localhost:${ORCA_HOST_PORT:-8081}"
        if curl -sf "$orca_url/healthz" >/dev/null 2>&1; then
            print_success "✓ OrcaSlicer worker: Healthy"
        else
            print_warning "✗ OrcaSlicer worker: Not responding"
            health_check_failed=true
        fi
    fi
    
    # Browser-origin / Proxy health check: ensure the public-facing origin proxies /api to the API
    # Use SERVER_HOST if set, otherwise default to localhost
    local proxy_host=${SERVER_HOST:-localhost}
    local proxy_port=${HTTP_PORT:-8080}
    local proxy_url="http://$proxy_host:$proxy_port"

    print_info "Verifying browser-origin proxy: $proxy_url/api/setup/status"
    local proxy_retries=${API_HEALTH_RETRIES:-60}
    local proxy_interval=${API_HEALTH_INTERVAL:-5}
    local p_attempt=1
    local proxy_ok=false
    while [ $p_attempt -le $proxy_retries ]; do
        # Use short timeout to keep checks responsive
        # Capture HTTP status and Content-Type header for better diagnostics
        proxy_status=$(curl -s -o /tmp/_proxy_body -w "%{http_code}" -m 3 "$proxy_url/api/setup/status" 2>/dev/null || echo "000")
        proxy_ct=$(curl -sI -m 3 "$proxy_url/api/setup/status" 2>/dev/null | tr -d '\r' | awk -F": " '/Content-Type/{print $2; exit}') || proxy_ct=""

        if [ "$proxy_status" = "000" ]; then
            print_info "  No response from proxy (attempt ${p_attempt}/${proxy_retries})"
        else
            # Read a small snippet of the body for diagnostics
            proxy_snippet=$(head -c 1024 /tmp/_proxy_body | sed -n '1,20p' | sed -e 's/\x0/ /g') || proxy_snippet=""
            if echo "$proxy_snippet" | grep -q '^{'; then
                proxy_ok=true
                print_success "✓ Browser-origin proxy responding with JSON (HTTP ${proxy_status}, after ${p_attempt} attempt(s))"
                break
            else
                if echo "$proxy_snippet" | grep -qi '^<!doctype\|^<html'; then
                    print_info "  Proxy returned HTML (SPA) - proxy may be serving static site instead of routing /api (HTTP ${proxy_status}, Content-Type: ${proxy_ct})"
                else
                    print_info "  Proxy returned non-JSON response (HTTP ${proxy_status}, Content-Type: ${proxy_ct})"
                fi
                if [ -n "$proxy_snippet" ]; then
                    print_info "  Response snippet (first 1KB):"
                    echo "$proxy_snippet" | sed 's/^/    /'
                fi
            fi
        fi

        p_attempt=$((p_attempt + 1))
        sleep $proxy_interval
    done

    if [ "$proxy_ok" = false ]; then
        print_warning "✗ Browser-origin proxy did not return JSON after ${proxy_retries} attempts"
        print_info "Tip: Ensure the frontend/proxy is bound to host port $proxy_port and is routing /api to the API. Example host URL: http://$proxy_host:$proxy_port/api/setup/status"
        print_info "Common fixes:"
        print_info "  • If you're using microservices, ensure nginx-proxy is started and bound to the host port and that its config proxies /api -> api:5245."
        print_info "  • If nginx-proxy is not present, access the API directly on API port: http://<host>:$API_PORT/api/setup/status"
        print_info "  • If you see HTML, the request is hitting the frontend directly; either remap ports so nginx-proxy wins or configure frontend to not bind host ports in microservices mode."
        health_check_failed=true
    fi

    echo
    if [ "$health_check_failed" = true ]; then
        run_api_diagnostics "🩺 API Health Diagnostics"
        print_warning "⚠️  Some health checks failed. Services may still be initializing."
        print_info "Wait a few moments and check manually:"
        print_info "  • Health: curl http://localhost:$HTTP_PORT/health | jq"
        print_info "  • Logs:   docker compose --env-file $ENV_FILE logs -f"
        echo
        return 1
    else
        print_success "✅ All health checks passed!"
        echo
        return 0
    fi
}

# Display final information
display_final_info() {
    local verification_passed="${1:-true}"
    
    print_header "🎉 Deployment Complete"
    
    if [ "$DRY_RUN" = "true" ]; then
        print_success "Dry-run summary (no containers started)"
    else
        if [ "$verification_passed" = true ]; then
            print_success "✅ PrintFarmer is now running and healthy!"
        else
            print_warning "⚠️  PrintFarmer is deployed but some health checks failed"
            print_info "Services may still be initializing - check status below"
        fi
    fi
    echo
    
    # Determine the hostname/IP to show in URLs
    local SERVER_HOST="localhost"
    if [ "${DEPLOYING_TO_LINUX:-no}" = "yes" ] || [ "$OS" = "linux" ]; then
        # Try to get the primary IP address
        if command -v hostname >/dev/null 2>&1; then
            # Try hostname -I first (works on most Linux)
            local detected_ip=$(hostname -I 2>/dev/null | awk '{print $1}')
            if [ -z "$detected_ip" ]; then
                # Fallback: try ip route (works on most Linux)
                detected_ip=$(ip route get 1 2>/dev/null | awk '{print $7; exit}')
            fi
            if [ -z "$detected_ip" ]; then
                # Fallback: try hostname -i
                detected_ip=$(hostname -i 2>/dev/null | awk '{print $1}')
            fi
            if [ -n "$detected_ip" ] && [ "$detected_ip" != "127.0.0.1" ]; then
                SERVER_HOST="$detected_ip"
            else
                # Last resort: use hostname
                SERVER_HOST=$(hostname 2>/dev/null || echo "localhost")
            fi
        fi
    fi
    
    echo -e "${GREEN}Access URLs:${NC}"
    echo -e "${BLUE}  🌐 Web Interface: http://$SERVER_HOST:$HTTP_PORT${NC}"
    
    if [ "$ARCHITECTURE" = "microservices" ]; then
        echo -e "${BLUE}  🔧 Direct API: http://$SERVER_HOST:$API_PORT${NC}"
    fi
    
    echo -e "${BLUE}  ❤️  Health Check: http://$SERVER_HOST:$HTTP_PORT/healthz${NC}"
    
    # Show localhost alternative if we're showing an IP
    if [ "$SERVER_HOST" != "localhost" ]; then
        echo -e "${BLUE}  📍 Local access: http://localhost:$HTTP_PORT${NC}"
    fi
    echo
    
    echo -e "${GREEN}Management Commands:${NC}"
    echo -e "${BLUE}  • View status:    docker compose --env-file $ENV_FILE ps${NC}"
    if [ "$DRY_RUN" != "true" ]; then
        echo -e "${BLUE}  • View logs:      docker compose --env-file $ENV_FILE logs -f${NC}"
        echo -e "${BLUE}  • Stop services:  docker compose --env-file $ENV_FILE down${NC}"
        echo -e "${BLUE}  • Update/restart: docker compose --env-file $ENV_FILE up -d --build${NC}"
    else
        echo -e "${BLUE}  • (Dry-run) To deploy: docker compose --env-file $ENV_FILE up -d --build${NC}"
    fi
    echo

    if [ "$ENABLE_DISCOVERY" = "yes" ]; then
        echo -e "${GREEN}Network Discovery:${NC}"
        echo -e "${BLUE}  • Configured ranges: $NETWORK_RANGES${NC}"
        if [ "$OS" = "macos" ]; then
            print_warning "  Note: macOS Docker may have limited WiFi device access"
        fi
        echo
    fi

    echo -e "${GREEN}Distributed Slicing:${NC}"
    echo -e "${BLUE}  • Enabled: $ENABLE_DISTRIBUTED_SLICING${NC}"
    if [ "$ENABLE_DISTRIBUTED_SLICING" = "true" ]; then
        echo -e "${BLUE}  • Orca Workers: $ORCA_WORKER_COUNT (enabled: $ENABLE_ORCA_WORKER)${NC}"
    fi

    
    echo -e "${GREEN}Configuration Files:${NC}"
    echo -e "${BLUE}  • Environment: $ENV_FILE${NC}"
    echo -e "${BLUE}  • Compose: $COMPOSE_FILE${NC}"
    if [ -f "docker-compose.override.yml" ]; then
        echo -e "${BLUE}  • Override: docker-compose.override.yml${NC}"
    fi
    echo
    
    # Troubleshooting section
    if [ "$DRY_RUN" != "true" ]; then
        echo -e "${YELLOW}Troubleshooting:${NC}"
        echo -e "${BLUE}  • Check container status: docker compose --env-file $ENV_FILE ps${NC}"
        echo -e "${BLUE}  • View all logs: docker compose --env-file $ENV_FILE logs${NC}"
        echo -e "${BLUE}  • Check specific service: docker compose --env-file $ENV_FILE logs api${NC}"
        echo -e "${BLUE}  • Restart a service: docker compose --env-file $ENV_FILE restart api${NC}"
        
        # Show additional help if verification failed
        if [ "$verification_passed" = false ]; then
            echo
            echo -e "${YELLOW}⚠️  Health Check Failures - Common Solutions:${NC}"
            echo -e "${BLUE}  1. Check API container logs:${NC}"
            echo -e "     docker compose --env-file $ENV_FILE logs api | tail -50"
            echo -e "${BLUE}  2. Check if API crashed (exit code):${NC}"
            echo -e "     docker ps -a | grep api"
            echo -e "${BLUE}  3. Restart API container:${NC}"
            echo -e "     docker compose --env-file $ENV_FILE restart api"
            echo -e "${BLUE}  4. Rebuild and restart:${NC}"
            echo -e "     docker compose --env-file $ENV_FILE up -d --build api"
            echo -e "${BLUE}  5. Check health manually (wait 30s then):${NC}"
            echo -e "     curl http://localhost:$HTTP_PORT/health | jq"
        fi
        
        # Port 80 specific troubleshooting
        if [ "$HTTP_PORT" = "80" ]; then
            echo
            echo -e "${YELLOW}Port 80 Notes:${NC}"
            echo -e "${BLUE}  • Requires elevated privileges on Linux${NC}"
            echo -e "${BLUE}  • Check if port is bound: sudo netstat -tlnp | grep :80${NC}"
            echo -e "${BLUE}  • If connection refused, check firewall: sudo ufw status${NC}"
        fi
        
        # Remote access troubleshooting
        if [ "$SERVER_HOST" != "localhost" ]; then
            echo
            echo -e "${YELLOW}Remote Access Notes:${NC}"
            echo -e "${BLUE}  • Ensure firewall allows port $HTTP_PORT${NC}"
            if [ "$ARCHITECTURE" = "microservices" ]; then
                echo -e "${BLUE}  • Ensure firewall allows port $API_PORT${NC}"
            fi
            echo -e "${BLUE}  • Test from server: curl http://localhost:$HTTP_PORT/healthz${NC}"
            echo -e "${BLUE}  • Check Docker networks: docker network ls${NC}"
        fi
        echo
    fi
    
    print_info "For troubleshooting, see: DOCKER_DEPLOYMENT.md"
    print_info "For local development, see: LOCAL_DEVELOPMENT.md"
}

# Redeploy existing deployment with rebuild
redeploy_existing() {
    print_header "🔄 Redeploying PrintFarmer (Rebuild Mode)"
    
    # Check if previous config exists
    if [ ! -f "$CONFIG_FILE" ]; then
        print_error "No previous deployment configuration found!"
        print_info "File expected: $CONFIG_FILE"
        print_info "Please run a full deployment first: ./scripts/deploy-docker.sh"
        exit 1
    fi
    
    print_info "Loading previous deployment configuration..."
    # shellcheck disable=SC1090
    source "$CONFIG_FILE"
    
    print_success "Loaded configuration:"
    echo -e "  ${BLUE}Architecture:${NC} $ARCHITECTURE"
    echo -e "  ${BLUE}Database:${NC} $DB_PROVIDER"
    echo -e "  ${BLUE}Network Mode:${NC} $NETWORK_MODE"
    echo -e "  ${BLUE}Compose File:${NC} $COMPOSE_FILE"
    echo
    
    # Force rebuild flag
    REBUILD=true
    
    # Set env file path
    ENV_FILE=".env"
    
    print_info "Starting redeployment with rebuild..."
    
    # Validate configuration first
    validate_configuration
    
    # Generate fresh env files with same config
    generate_env_file
    generate_react_env_production
    
    # Use existing compose override if it exists
    if [ -f "docker-compose.override.yml" ]; then
        print_info "Using existing docker-compose.override.yml"
    fi
    
    # Deploy with rebuild
    deploy_containers
    
    print_success "✅ Redeployment complete!"
    print_info "All containers have been rebuilt and restarted with the same configuration."
    
    exit 0
}

# Main execution
main() {
    # Handle help mode first
    if [ "$SHOW_HELP" = "true" ]; then
        show_help
        # Function exits, so we never reach here
    fi
    
    # Handle redeploy mode
    if [ "$REDEPLOY" = "true" ]; then
        redeploy_existing
        # Function exits, so we never reach here
    fi
    
    # Handle tear-down mode
    if [ "$TEAR_DOWN" = "true" ]; then
        tear_down_deployment
        # Function exits, so we never reach here
    fi
    
    # Handle offline deployment modes (these modes exit after completion)
    if [ "$PREPARE_OFFLINE" = "true" ]; then
        if prepare_offline_deployment "$IMAGES_DIR"; then
            print_success "All offline materials prepared. You can now transfer the folder to your offline machine."
            exit 0
        else
            print_error "Failed to prepare offline deployment materials"
            exit 1
        fi
    fi
    
    if [ "$DEPLOY_OFFLINE" = "true" ]; then
        if deploy_offline_mode "$IMAGES_DIR"; then
            print_info "Continuing with interactive deployment configuration..."
            # Fall through to normal deployment flow below
        else
            print_error "Failed to load offline deployment materials"
            exit 1
        fi
    fi
    
    # Handle image management options (these exit early if used)
    if [ "$PULL_IMAGES" = "true" ]; then
        if pull_base_images; then
            if [ "$SAVE_IMAGES" = "true" ]; then
                save_images_to_tar "$IMAGES_DIR"
            fi
        fi
        exit 0
    fi
    
    if [ "$SAVE_IMAGES" = "true" ]; then
        if [ "$PULL_IMAGES" != "true" ]; then
            print_info "Saving already downloaded images..."
        fi
        save_images_to_tar "$IMAGES_DIR"
        exit 0
    fi
    
    if [ "$LOAD_IMAGES" = "true" ]; then
        if load_images_from_tar "$IMAGES_DIR"; then
            print_info "Proceeding with deployment..."
        else
            exit 1
        fi
    fi
    
    if [ "$CACHE_ORCASLICER" = "true" ]; then
        cache_orcaslicer "$IMAGES_DIR/orcaslicer"
        exit 0
    fi
    
    if [ "$LOAD_CACHED_ORCASLICER" = "true" ]; then
        auto_load_orcaslicer "$IMAGES_DIR/orcaslicer"
        exit 0
    fi
    
    print_header "🚀 PrintFarmer Docker Deployment Setup"
    
    print_info "This script will help you deploy PrintFarmer using Docker containers."
    print_info "You'll be prompted for configuration with sensible defaults provided."
    echo
    
    # Verify repository assets are available even when executed outside repo root
    if [ ! -f "$REPO_ROOT/global.json" ] || [ ! -d "$REPO_ROOT/scripts/docker" ]; then
        print_error "Required repository assets not found"
        print_info "Expected files: $REPO_ROOT/global.json and $REPO_ROOT/scripts/docker/"
        exit 1
    fi

    # Inform user when running from a directory other than the repository root
    if [ "$(pwd)" != "$REPO_ROOT" ]; then
        print_info "Detected repository root at $REPO_ROOT"
        print_info "Running from $(pwd); generated deployment files will be created here"
    fi
    
    # Load previous configuration if available (sets defaults for interactive mode)
    load_previous_config || true
    
    # Execute setup steps
    detect_environment
    choose_architecture
    configure_database
    configure_networking
    adjust_connection_strings_for_network_mode
    configure_external_storage
    configure_additional
    validate_configuration
    save_deployment_config
    generate_env_file
    generate_react_env_production
    
    # Generate deployment configuration using new compose generator
    # CLI flags take precedence over environment variables
    local include_monitoring="false"
    local include_telemetry="false" 
    local include_security="false"
    local include_registry="false"
    
    # Set from CLI flags or environment variables
    if [ "${CLI_INCLUDE_MONITORING:-false}" = "true" ] || [ "${INCLUDE_MONITORING:-false}" = "true" ]; then
        include_monitoring="true"
    fi
    if [ "${CLI_INCLUDE_TELEMETRY:-false}" = "true" ] || [ "${INCLUDE_TELEMETRY:-false}" = "true" ]; then
        include_telemetry="true"
    fi
    if [ "${CLI_INCLUDE_SECURITY:-false}" = "true" ] || [ "${INCLUDE_SECURITY:-false}" = "true" ]; then
        include_security="true"
    fi
    if [ "${CLI_INCLUDE_REGISTRY:-false}" = "true" ] || [ "${INCLUDE_REGISTRY:-false}" = "true" ]; then
        include_registry="true"
    fi
    
    # Determine output directory (CLI option or default to current directory)
    local output_dir="${CLI_OUTPUT_DIR:-$(pwd)}"

    if generate_deployment_config "$ARCHITECTURE" "$INCLUDE_MONITORING" "$INCLUDE_TELEMETRY" "$INCLUDE_SECURITY" "$INCLUDE_REGISTRY" "$INCLUDE_DISCOVERY" "$output_dir"; then
        print_success "Using new compose generator"
    else
        print_warning "Falling back to legacy compose generation"
        generate_compose_override
    fi

    # Optional prepull for Apple Silicon or slow networks: pull common base images
    prepull_images() {
        if [ "${PREPULL:-false}" != "true" ]; then
            return 0
        fi

        print_info "Pre-pulling common images to speed builds on Apple Silicon (amd64)"
        # Respect DOCKER_DEFAULT_PLATFORM if set; otherwise default to linux/amd64 for arm64 hosts
        local platform_arg=""
        if [ -n "${DOCKER_DEFAULT_PLATFORM:-}" ]; then
            platform_arg="--platform ${DOCKER_DEFAULT_PLATFORM}"
        else
            platform_arg="--platform linux/amd64"
        fi

        # Minimal set of images used in compose; expand as needed
        local images=("nginx:alpine" "postgres:15" "mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim" "node:18-alpine")
        for img in "${images[@]}"; do
            print_info "Pulling $img ($platform_arg)"
            if docker pull $platform_arg "$img"; then
                print_success "Pulled $img"
            else
                print_warning "Failed pulling $img; continuing"
            fi
        done
    }

    # Run prepull step if requested
    prepull_images
    
    # Auto-load cached images if available (after all configuration prompts)
    # This searches common locations automatically - no user intervention needed
    # Pass empty string to trigger auto-discovery in common paths
    print_info "Checking for cached Docker images..."
    auto_load_cached_images ""
    
    # Auto-load OrcaSlicer AppImage if available
    auto_load_orcaslicer ""
    
    deploy_containers
    
    # Run auto-admin setup if requested
    setup_initial_admin || true
    
    # Run verification and capture result
    local verification_passed=true
    verify_deployment || verification_passed=false
    
    display_final_info "$verification_passed"
    
    # Cleanup generated files unless requested to keep them
    cleanup_generated_files
    
    if [ "$verification_passed" = true ]; then
        print_success "Setup completed successfully! 🎉"
    else
        print_warning "Setup completed with warnings - please check health status above"
        print_info "Services may need a few more moments to fully initialize"
        exit 1
    fi
}

# Support a verify-only CLI mode so callers can run only the verification
# steps against an existing deployment (without generating files or starting
# containers). This parses a single long flag '--verify-deployment' and
# executes the `verify_deployment` function using the existing
# $CONFIG_FILE and/or .env if present.
VERIFY_DEPLOYMENT=false
_ARGS_KEEP=()

# Parse leftover CLI args to capture optional verify-only parameters
while [ $# -gt 0 ]; do
    case "$1" in
        -h|--help)
            SHOW_HELP=true
            shift
            ;;
        -n|--dry-run)
            DRY_RUN=true
            shift
            ;;
        -b|--batch|--non-interactive)
            NON_INTERACTIVE=true
            shift
            ;;
        --redeploy)
            REDEPLOY=true
            shift
            ;;
        --tear-down|--teardown|--clean)
            TEAR_DOWN=true
            shift
            ;;
        --build-verbosity)
            if [ -n "${2:-}" ]; then
                BUILD_VERBOSITY="$2"
                shift 2
            else
                echo "Missing value for --build-verbosity" >&2; exit 2
            fi
            ;;
        --build-verbosity=*)
            BUILD_VERBOSITY="${1#--build-verbosity=}"
            shift
            ;;
        --verbose-build)
            BUILD_VERBOSITY="detailed"
            shift
            ;;
        --architecture)
            if [ -n "${2:-}" ]; then
                CLI_ARCHITECTURE="$2"
                shift 2
            else
                echo "Missing value for --architecture" >&2; exit 2
            fi
            ;;
        --architecture=*)
            CLI_ARCHITECTURE="${1#--architecture=}"
            shift
            ;;
        --include-monitoring)
            CLI_INCLUDE_MONITORING=true
            shift
            ;;
        --include-telemetry)
            CLI_INCLUDE_TELEMETRY=true
            shift
            ;;
        --include-security)
            CLI_INCLUDE_SECURITY=true
            shift
            ;;
        --include-registry)
            CLI_INCLUDE_REGISTRY=true
            shift
            ;;
        --output-dir)
            if [ -n "${2:-}" ]; then
                CLI_OUTPUT_DIR="$2"
                shift 2
            else
                echo "Missing value for --output-dir" >&2; exit 2
            fi
            ;;
        --output-dir=*)
            CLI_OUTPUT_DIR="${1#--output-dir=}"
            shift
            ;;
        --verify-deployment)
            VERIFY_DEPLOYMENT=true
            shift
            ;;
        --env-file)
            if [ -n "${2:-}" ]; then
                ENV_FILE="$2"
                shift 2
            else
                echo "Missing value for --env-file" >&2; exit 2
            fi
            ;;
        --env-file=*)
            ENV_FILE="${1#--env-file=}"
            shift
            ;;
        --config-file)
            if [ -n "${2:-}" ]; then
                CONFIG_FILE="$2"
                shift 2
            else
                echo "Missing value for --config-file" >&2; exit 2
            fi
            ;;
        --prepull)
            PREPULL=true
            shift
            ;;
        --auto-admin-config)
            if [ -n "${2:-}" ]; then
                AUTO_ADMIN_CONFIG_FILE="$2"
                shift 2
            else
                echo "Missing value for --auto-admin-config" >&2; exit 2
            fi
            ;;
        --auto-admin-config=*)
            AUTO_ADMIN_CONFIG_FILE="${1#--auto-admin-config=}"
            shift
            ;;
        --auto-admin)
            AUTO_ADMIN=true
            shift
            ;;
        --auto-admin-username)
            if [ -n "${2:-}" ]; then
                AUTO_ADMIN_USERNAME="$2"
                shift 2
            else
                echo "Missing value for --auto-admin-username" >&2; exit 2
            fi
            ;;
        --auto-admin-username=*)
            AUTO_ADMIN_USERNAME="${1#--auto-admin-username=}"
            shift
            ;;
        --auto-admin-password)
            if [ -n "${2:-}" ]; then
                AUTO_ADMIN_PASSWORD="$2"
                shift 2
            else
                echo "Missing value for --auto-admin-password" >&2; exit 2
            fi
            ;;
        --auto-admin-password=*)
            AUTO_ADMIN_PASSWORD="${1#--auto-admin-password=}"
            shift
            ;;
        --auto-admin-email)
            if [ -n "${2:-}" ]; then
                AUTO_ADMIN_EMAIL="$2"
                shift 2
            else
                echo "Missing value for --auto-admin-email" >&2; exit 2
            fi
            ;;
        --auto-admin-email=*)
            AUTO_ADMIN_EMAIL="${1#--auto-admin-email=}"
            shift
            ;;
        --config-file=*)
            CONFIG_FILE="${1#--config-file=}"
            shift
            ;;
        --prepare-offline)
            PREPARE_OFFLINE=true
            shift
            ;;
        --deploy-offline)
            DEPLOY_OFFLINE=true
            shift
            ;;
        --pull-images)
            PULL_IMAGES=true
            shift
            ;;
        --save-images)
            SAVE_IMAGES=true
            shift
            ;;
        --load-images)
            LOAD_IMAGES=true
            shift
            ;;
        --cache-orcaslicer)
            CACHE_ORCASLICER=true
            shift
            ;;
        --load-cached-orcaslicer)
            LOAD_CACHED_ORCASLICER=true
            shift
            ;;
        --images-dir)
            if [ -n "${2:-}" ]; then
                IMAGES_DIR="$2"
                shift 2
            else
                echo "Missing value for --images-dir" >&2; exit 2
            fi
            ;;
        --images-dir=*)
            IMAGES_DIR="${1#--images-dir=}"
            shift
            ;;
        --env)
            if [ -n "${2:-}" ]; then
                export "${2}"
                shift 2
            else
                echo "Missing value for --env" >&2; exit 2
            fi
            ;;
        --env=*)
            export "${1#--env=}"
            shift
            ;;
        --)
            shift
            break
            ;;
        *)
            _ARGS_KEEP+=("$1")
            shift
            ;;
    esac
done

set -- "${_ARGS_KEEP[@]:-}"
postgres_readiness_check() {
    local pg_user="${POSTGRES_USER:-postgres}"
    local pg_db="${POSTGRES_DB:-printfarmer}"
    local pg_password="${POSTGRES_PASSWORD:-}"

    dc exec -T database sh -c "PGPASSWORD='${pg_password}' pg_isready -U '${pg_user}' -d '${pg_db}' -h localhost" >/dev/null 2>&1
}


# Auto-detect and load auto-admin config if not explicitly provided via --auto-admin-config
if [ -z "$AUTO_ADMIN_CONFIG_FILE" ]; then
    auto_detect_admin_config
fi

# Load auto-admin config file if found (can be overridden by command-line flags)
if [ -n "$AUTO_ADMIN_CONFIG_FILE" ] && [ -f "$AUTO_ADMIN_CONFIG_FILE" ]; then
    load_auto_admin_config
fi

# If verify-only requested, load existing config and environment and run verification
if [ "$VERIFY_DEPLOYMENT" = "true" ]; then
    # Ensure CONFIG_FILE points to repo-local config if not already set
    CONFIG_FILE="${CONFIG_FILE:-$REPO_ROOT/.deploy-config}"
    if [ -f "$CONFIG_FILE" ]; then
        # shellcheck disable=SC1090
        source "$CONFIG_FILE" || true
    fi

    # Prefer an explicit .env file if present
    if [ -f .env ]; then
        ENV_FILE=".env"
    else
        ENV_FILE="${ENV_FILE:-.env}"
    fi

    # Basic compose file defaults when not set by config
    COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.yml}"
    # Prefer host-network compose only when network mode is host and file exists
    if [ "${NETWORK_MODE:-bridge}" = "host" ] && [ -f docker-compose.host-network.yml ]; then
        COMPOSE_FILE="docker-compose.host-network.yml"
    # If architecture is microservices, prefer the microservices template when available
    elif [ "${ARCHITECTURE:-}" = "microservices" ] && [ -f docker-compose.microservices.yml ]; then
        COMPOSE_FILE="docker-compose.microservices.yml"
    fi

    print_header "🔍 Verify-only mode: running deployment verification"
    # Ensure dry-run is false so verify_deployment performs live checks
    DRY_RUN=false

    if verify_deployment; then
        print_success "Verify-only: deployment verification succeeded"
        exit 0
    else
        print_error "Verify-only: deployment verification failed"
        exit 2
    fi
fi

# Run main function only when script is executed directly (not when sourced)
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi
