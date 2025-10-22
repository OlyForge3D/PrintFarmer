#!/bin/bash

# PrintFarmer Docker Deployment Script
# Automated setup for Docker-based deployment with user-friendly prompts

set -euo pipefail

# Default flags
DRY_RUN=false
NON_INTERACTIVE=false
TEAR_DOWN=false
SHOW_HELP=false
# Compose up option to pass --remove-orphans (default true)
COMPOSE_REMOVE_ORPHANS=${COMPOSE_REMOVE_ORPHANS:-true}

# Parse simple flags early (only --dry-run / -n for now)
for arg in "$@"; do
    case "$arg" in
        --dry-run|-n)
            DRY_RUN=true
            ;;
        --non-interactive|--batch|-b)
            NON_INTERACTIVE=true
            ;;
        --remove-orphans)
            COMPOSE_REMOVE_ORPHANS=true
            ;;
        --no-remove-orphans)
            COMPOSE_REMOVE_ORPHANS=false
            ;;
        --tear-down|--teardown|--clean)
            TEAR_DOWN=true
            ;;
        --help|-h)
            SHOW_HELP=true
            ;;
    esac
done

# Allow env override for automated pipelines
if [ "${NON_INTERACTIVE:-}" = "1" ]; then
    NON_INTERACTIVE=true
fi

# Global guard: disable all slicer-related automatic builds when requested
if [ "${DISABLE_SLICER_BUILDS:-}" = "true" ] || [ "${DISABLE_SLICER_BUILDS:-}" = "1" ]; then
    print_warning "DISABLE_SLICER_BUILDS is set; automatic Orca/Prusa worker builds will be disabled."
    # Ensure variables exist so downstream logic respects the disable
    ENABLE_ORCA_WORKER=no
    ENABLE_PRUSA_WORKER=no
    ORCA_WORKER_COUNT=0
    PRUSA_WORKER_COUNT=0
fi

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Print colored output
print_info() { echo -e "${BLUE}ℹ️  $1${NC}"; }
print_success() { echo -e "${GREEN}✅ $1${NC}"; }
print_warning() { echo -e "${YELLOW}⚠️  $1${NC}"; }
print_error() { echo -e "${RED}❌ $1${NC}"; }

# Print section headers
print_header() {
    echo
    echo -e "${BLUE}================================================${NC}"
    echo -e "${BLUE}$1${NC}"
    echo -e "${BLUE}================================================${NC}"
    echo
}

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
        echo -e "${YELLOW}$prompt [$default_text]${NC}"
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

# Tear down existing deployment
tear_down_deployment() {
    print_header "🧹 Tearing Down PrintFarmer Deployment"
    
    print_warning "This will:"
    echo "  1. Stop and remove ALL Docker containers"
    echo "  2. Remove ALL Docker volumes (⚠️  ALL DATA WILL BE DELETED!)"
    echo "  3. Remove all PrintFarmer Docker images"
    echo "  4. Prune orphaned and dangling images"
    echo "  5. Clean up generated configuration files"
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
    print_info "Attempting to stop compose stacks (microservices / host-network / default)..."
    # Prefer microservices compose if present
    if [ -f docker-compose.microservices.yml ]; then
        print_info "Running: docker compose --env-file .env.microservices -f docker-compose.microservices.yml down --volumes --rmi all"
        docker compose --env-file .env.microservices -f docker-compose.microservices.yml down --volumes --rmi all || true
    fi

    # If host-network compose exists, tear it down explicitly
    if [ -f docker-compose.host-network.yml ]; then
        print_info "Running: docker compose -f docker-compose.host-network.yml down --volumes --rmi all"
        docker compose -f docker-compose.host-network.yml down --volumes --rmi all || true
    fi

    # Try the default compose file as well
    if [ -f docker-compose.yml ]; then
        print_info "Running: docker compose -f docker-compose.yml down --volumes --rmi all"
        docker compose -f docker-compose.yml down --volumes --rmi all || true
    fi

    # 1. Stop all remaining running containers (fallback)
    print_info "Step 1/7: Stopping any remaining running Docker containers..."
    if [ -n "$(docker ps -q)" ]; then
        docker stop $(docker ps -aq) 2>/dev/null || true
        print_success "Stopped running containers (attempted)"
    else
        print_info "No running containers found"
    fi

    # 2. Remove all remaining containers (force remove any leftovers)
    print_info "Step 2/7: Removing any remaining Docker containers..."
    local all_containers
    all_containers=$(docker ps -aq)
    if [ -n "$all_containers" ]; then
        # Try normal remove first, then force-remove to handle odd states
        docker rm $all_containers 2>/dev/null || true
        # Re-query and force remove stubborn containers
        remaining=$(docker ps -aq)
        if [ -n "$remaining" ]; then
            print_warning "Some containers remain after normal removal. Attempting force remove..."
            docker rm -f $remaining 2>/dev/null || true
        fi

        # Final check
        if [ -z "$(docker ps -aq)" ]; then
            print_success "Containers removed"
        else
            print_warning "Some containers could not be removed. Run 'docker ps -a' to inspect and remove manually."
        fi
    else
        print_info "No containers to remove"
    fi

    # Additionally, ensure any supported database containers are removed explicitly
    # This helps on systems where compose project names or previous runs left DB containers behind
    print_info "Ensuring supported database containers are removed (postgres/sqlserver/mysql)"
    for dbc in postgres sqlserver mysql; do
        # Look for container names that start with pfarm- or contain the service name
        containers=$(docker ps -a --format '{{.Names}}' | grep -E "(^|/)pfarm-${dbc}|${dbc}" || true)
        if [ -n "$containers" ]; then
            print_warning "Found database containers to remove for $dbc: $containers"
            docker rm -f $containers 2>/dev/null || true
            print_success "Removed $dbc containers: $containers"
        fi
    done
    
    # 3. Remove all volumes
    print_info "Step 3/7: Removing all Docker volumes..."
    if docker volume ls -q | grep -q .; then
        docker volume rm $(docker volume ls -q) 2>/dev/null || true
        print_success "Volumes removed"
    else
        print_info "No volumes to remove"
    fi
    
    # 4. Remove PrintFarmer images
    print_info "Step 4/7: Removing PrintFarmer Docker images..."
    if docker images --format "{{.Repository}}" | grep -q "printfarmer"; then
        docker images --format "{{.Repository}}:{{.Tag}}" | grep "printfarmer" | xargs -r docker rmi -f 2>/dev/null || true
        print_success "PrintFarmer images removed"
    else
        print_info "No PrintFarmer images to remove"
    fi
    
    # 5. Prune unused networks
    print_info "Step 5/7: Cleaning up Docker networks..."
    docker network prune -f > /dev/null 2>&1 || true
    print_success "Networks cleaned"
    
    # 6. Prune orphaned/dangling images
    print_info "Step 6/7: Pruning orphaned and dangling images..."
    docker image prune -f > /dev/null 2>&1 || true
    print_success "Orphaned images pruned"
    
    # 7. Remove generated files
    print_info "Step 7/7: Removing generated configuration files..."
    local files_removed=0
    
    if [ -f docker-compose.host-network.yml ]; then
        rm -f docker-compose.host-network.yml
        echo "  • Removed docker-compose.host-network.yml"
        ((files_removed++))
    fi
    
    if [ -f docker-compose.override.yml ]; then
        rm -f docker-compose.override.yml
        echo "  • Removed docker-compose.override.yml"
        ((files_removed++))
    fi
    
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
    --tear-down             Tear down existing deployment (stops containers, removes
        --teardown          volumes, cleans up). Useful for starting fresh.
        --clean             Same as --tear-down

EXAMPLES:
    # Interactive deployment (recommended for first-time setup)
    ./scripts/deploy-docker.sh

    # Tear down existing deployment and clean up
    ./scripts/deploy-docker.sh --tear-down

    # Validate configuration without deploying
    ./scripts/deploy-docker.sh --dry-run

    # Non-interactive deployment (for automation/CI)
    ./scripts/deploy-docker.sh --non-interactive

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

For more information, see:
    - DOCKER_DEPLOYMENT.md
    - LOCAL_DEVELOPMENT.md
    - README.md

EOF
    exit 0
}

# Configuration file location
CONFIG_FILE=".deploy-config"

# Load previous configuration if it exists
load_previous_config() {
    if [ -f "$CONFIG_FILE" ]; then
        print_info "Found previous deployment configuration"
        
        # Source the config file to load variables
        # shellcheck disable=SC1090
        source "$CONFIG_FILE"
        
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
INCLUDE_POSTGRES=${INCLUDE_POSTGRES:-no}
INCLUDE_SQLSERVER=${INCLUDE_SQLSERVER:-no}
INCLUDE_MYSQL=${INCLUDE_MYSQL:-no}
CONNECTION_STRING=$(printf '%q' "$CONNECTION_STRING")

# PostgreSQL Configuration
POSTGRES_DB=${POSTGRES_DB:-printfarmer}
POSTGRES_USER=${POSTGRES_USER:-postgres}
POSTGRES_PASSWORD=${POSTGRES_PASSWORD:-}

# SQL Server Configuration
SQLSERVER_DB=${SQLSERVER_DB:-printfarmer}
SQLSERVER_PASSWORD=${SQLSERVER_PASSWORD:-}
SQLSERVER_PORT=${SQLSERVER_PORT:-1433}
SQLSERVER_EDITION=${SQLSERVER_EDITION:-Developer}

# MySQL Configuration
MYSQL_DB=${MYSQL_DB:-printfarmer}
MYSQL_USER=${MYSQL_USER:-root}
MYSQL_PASSWORD=${MYSQL_PASSWORD:-}

# Network Configuration
ENABLE_DISCOVERY=$ENABLE_DISCOVERY
ALLOW_LOCAL_NETWORK=$ALLOW_LOCAL_NETWORK
NETWORK_RANGES=$(printf '%q' "$NETWORK_RANGES")
NETWORK_MODE=${NETWORK_MODE:-bridge}
HTTP_PORT=$HTTP_PORT

# Application Settings - Pre-populate Setup Wizard  
PFARM__NetworkDiscovery__EnableDiscovery=${ENABLE_DISCOVERY}
PFARM__NetworkDiscovery__DiscoverySubnets=$(printf '%q' "$NETWORK_RANGES")
EOF

    if [ "$ARCHITECTURE" = "microservices" ]; then
        echo "API_PORT=$API_PORT" >> "$CONFIG_FILE"
    fi

    cat >> "$CONFIG_FILE" << EOF

# Application Settings
ENVIRONMENT=$ENVIRONMENT
ENABLE_SWAGGER=$ENABLE_SWAGGER
ENABLE_DETAILED_LOGGING=$ENABLE_DETAILED_LOGGING

# Distributed Slicing
ENABLE_DISTRIBUTED_SLICING=$ENABLE_DISTRIBUTED_SLICING
ENABLE_ORCA_WORKER=${ENABLE_ORCA_WORKER:-no}
ORCA_WORKER_COUNT=${ORCA_WORKER_COUNT:-0}
ORCA_HOST_PORT=${ORCA_HOST_PORT:-8081}
ORCASLICER_VERSION=${ORCASLICER_VERSION:-2.3.1}
ENABLE_PRUSA_WORKER=${ENABLE_PRUSA_WORKER:-no}
PRUSA_WORKER_COUNT=${PRUSA_WORKER_COUNT:-0}
PRUSA_HOST_PORT=${PRUSA_HOST_PORT:-8082}
PRUSASLICER_VERSION=${PRUSASLICER_VERSION:-2.9.3}
EOF

    if [ "$ARCHITECTURE" = "microservices" ] && [ "${OVERRIDE_WORKER_ENDPOINTS:-no}" = "yes" ]; then
        cat >> "$CONFIG_FILE" << EOF

# Worker Endpoints (Advanced)
OVERRIDE_WORKER_ENDPOINTS=yes
EOF
        [ "${ENABLE_ORCA_WORKER}" = "yes" ] && echo "ORCA_WORKER_ENDPOINT=${ORCA_WORKER_ENDPOINT}" >> "$CONFIG_FILE"
        [ "${ENABLE_PRUSA_WORKER}" = "yes" ] && echo "PRUSA_WORKER_ENDPOINT=${PRUSA_WORKER_ENDPOINT}" >> "$CONFIG_FILE"
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

    if [ "$ARCHITECTURE" = "microservices" ] && [ "${REDIS_PERSIST:-no}" = "yes" ]; then
        echo "REDIS_PERSIST=yes" >> "$CONFIG_FILE"
    fi

    cat >> "$CONFIG_FILE" << EOF

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
    print_header "🏗️  Deployment Architecture"
    
    echo -e "${BLUE}PrintFarmer supports two deployment architectures:${NC}"
    echo
    echo -e "${GREEN}1. Monolithic (Recommended)${NC}"
    echo "   • Single container with API + Web frontend"
    echo "   • Simpler configuration and networking"
    echo "   • Good for most deployments"
    echo "   • Uses SQLite database by default"
    echo
    echo -e "${GREEN}2. Microservices (Advanced)${NC}"
    echo "   • Separate containers for API, Web, Database, Redis"
    echo "   • Enhanced networking capabilities"
    echo "   • Better for large-scale deployments"
    echo "   • Supports PostgreSQL, SQL Server, MySQL"
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
            ENV_FILE=".env.monolithic"
            COMPOSE_FILE="docker-compose.yml"
            print_success "Selected: Monolithic deployment"
            
            # Check .NET SDK for monolithic (optional but recommended for local builds)
            check_dotnet_sdk
            ;;
        2|microservices|micro)
            ARCHITECTURE="microservices"
            ENV_FILE=".env.microservices"
            COMPOSE_FILE="docker-compose.microservices.yml"
            print_success "Selected: Microservices deployment (using docker-compose.microservices.yml)"
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
    for var in ORCA_WORKER_COUNT PRUSA_WORKER_COUNT; do
        val=${!var:-0}
        if ! is_positive_int "$val"; then
            print_warning "Invalid value '$val' for $var. Resetting to 1."
            eval "$var=1"
        fi
    done

    # If distributed slicing disabled, zero out counts
    if [ "${ENABLE_DISTRIBUTED_SLICING:-false}" != "true" ]; then
        ORCA_WORKER_COUNT=0
        PRUSA_WORKER_COUNT=0
    fi

    # Monolithic constraints: host networking -> only one instance per worker due to fixed ports 8081/8082
    if [ "$ARCHITECTURE" = "monolithic" ]; then
        if [ "$ORCA_WORKER_COUNT" -gt 1 ]; then
            print_warning "Monolithic mode: Cannot scale OrcaSlicer workers (host networking / fixed port 8081). For scaling, use microservices. Forcing count=1."
            ORCA_WORKER_COUNT=1
        fi
        if [ "$PRUSA_WORKER_COUNT" -gt 1 ]; then
            print_warning "Monolithic mode: Cannot scale PrusaSlicer workers (host networking / fixed port 8082). Forcing count=1."
            PRUSA_WORKER_COUNT=1
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
    PRUSA_HOST_PORT=${PRUSA_HOST_PORT:-8082}
    if [ "$ARCHITECTURE" = "monolithic" ]; then
        # Only warn; cannot remap easily due to fixed host network & static internal ports
        if [ "$ENABLE_ORCA_WORKER" = "yes" ] && port_in_use "$ORCA_HOST_PORT"; then
            print_warning "Monolithic: Orca worker port $ORCA_HOST_PORT in use; startup may fail."
        fi
        if [ "$ENABLE_PRUSA_WORKER" = "yes" ] && port_in_use "$PRUSA_HOST_PORT"; then
            print_warning "Monolithic: Prusa worker port $PRUSA_HOST_PORT in use; startup may fail."
        fi
    else
        # Allow remap for microservices (we will rely on variable interpolation in compose file)
        if [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -gt 0 ] && port_in_use "$ORCA_HOST_PORT"; then
            suggest_port_replacement ORCA_HOST_PORT "$ORCA_HOST_PORT" "Orca worker"
        fi
        if [ "$ENABLE_PRUSA_WORKER" = "yes" ] && [ "$PRUSA_WORKER_COUNT" -gt 0 ] && port_in_use "$PRUSA_HOST_PORT"; then
            suggest_port_replacement PRUSA_HOST_PORT "$PRUSA_HOST_PORT" "Prusa worker"
        fi
    fi

    # Logical consistency: worker enabled but count 0 -> adjust to 1
    if [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -eq 0 ]; then
        print_warning "ENABLE_ORCA_WORKER=yes but ORCA_WORKER_COUNT=0. Setting count=1."
        ORCA_WORKER_COUNT=1
    fi
    if [ "$ENABLE_PRUSA_WORKER" = "yes" ] && [ "$PRUSA_WORKER_COUNT" -eq 0 ]; then
        print_warning "ENABLE_PRUSA_WORKER=yes but PRUSA_WORKER_COUNT=0. Setting count=1."
        PRUSA_WORKER_COUNT=1
    fi

    # If distributed slicing disabled but workers were enabled by mistake
    if [ "$ENABLE_DISTRIBUTED_SLICING" != "true" ] && { [ "$ENABLE_ORCA_WORKER" = "yes" ] || [ "$ENABLE_PRUSA_WORKER" = "yes" ]; }; then
        print_warning "Workers enabled but distributed slicing disabled. Forcing workers off."
        ENABLE_ORCA_WORKER=no
        ENABLE_PRUSA_WORKER=no
        ORCA_WORKER_COUNT=0
        PRUSA_WORKER_COUNT=0
    fi

    print_success "Validation complete."
}

# Configure database settings
configure_database() {
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
                        print_warning "Unknown database type, using SQLite as fallback"
                        DB_PROVIDER="sqlite"
                        CONNECTION_STRING="Data Source=/data/farm.db"
                        ;;
                esac
                ;;
            *)
                print_warning "Unknown choice, using SQLite as fallback"
                DB_PROVIDER="sqlite"
                CONNECTION_STRING="Data Source=/data/farm.db"
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
    print_header "🌐 Network Configuration"
    
    # Determine network mode first (affects discovery configuration)
    echo -e "${BLUE}Network Mode for API Container:${NC}"
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
                    print_info "API will bind to port ${API_PORT:-5245} on the host"
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
                print_info "API will bind to port ${API_PORT:-5245} on the host"
                ;;
            *)
                NETWORK_MODE="bridge"
                print_info "Using bridge mode (cross-platform compatible)"
                ;;
        esac
    fi
    
    echo
    
    # Configure discovery based on network mode
    if [ "$NETWORK_MODE" = "host" ]; then
        # Host mode: Auto-enable discovery with sensible defaults
        print_success "Network discovery automatically enabled with host networking"
        ENABLE_DISCOVERY="yes"
        ALLOW_LOCAL_NETWORK="true"
        
        echo -e "${BLUE}Configure IP address ranges to scan for printers:${NC}"
        echo "Common ranges:"
        echo "  • 192.168.0.0/16 (Most home networks: 192.168.x.x)"
        echo "  • 10.0.0.0/8 (Corporate networks: 10.x.x.x)"
        echo "  • 172.16.0.0/12 (Docker networks: 172.16.x.x-172.31.x.x)"
        echo
        
        prompt_with_default "Network ranges to scan (comma-separated):" "192.168.0.0/16,10.0.0.0/8" "NETWORK_RANGES"
    else
        # Bridge mode: Ask about discovery
        echo -e "${BLUE}Network discovery allows PrintFarmer to find 3D printers on your network.${NC}"
        echo
        
        if [ "$OS" = "macos" ]; then
            print_warning "macOS Docker has limited WiFi access. Network discovery may not work for WiFi-connected printers."
            print_info "Consider using local development instead of Docker on macOS."
            echo
        fi
        
        prompt_yes_no "Enable network discovery?" "yes" "ENABLE_DISCOVERY"
        
        if [ "$ENABLE_DISCOVERY" = "yes" ]; then
            echo
            echo -e "${BLUE}Configure IP address ranges to scan for printers:${NC}"
            echo "Common ranges:"
            echo "  • 192.168.0.0/16 (Most home networks: 192.168.x.x)"
            echo "  • 10.0.0.0/8 (Corporate networks: 10.x.x.x)"
            echo "  • 172.16.0.0/12 (Docker networks: 172.16.x.x-172.31.x.x)"
            echo
            
            prompt_with_default "Network ranges to scan (comma-separated):" "192.168.0.0/16,10.0.0.0/8" "NETWORK_RANGES"
            ALLOW_LOCAL_NETWORK="true"
        else
            ALLOW_LOCAL_NETWORK="false"
            NETWORK_RANGES=""
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
                CONNECTION_STRING="Host=localhost;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"
                print_success "PostgreSQL connection string updated for host networking"
                ;;
            sqlserver)
                # SQL Server: Change from "Server=sqlserver" to "Server=localhost,PORT"
                CONNECTION_STRING="Server=localhost,${SQLSERVER_PORT:-1433};Database=$SQLSERVER_DB;User Id=sa;Password=$SQLSERVER_PASSWORD;TrustServerCertificate=True;"
                print_success "SQL Server connection string updated for host networking (port ${SQLSERVER_PORT:-1433})"
                ;;
            mysql)
                # MySQL: Change from "Server=mysql" to "Server=localhost"
                CONNECTION_STRING="Server=localhost;Database=$MYSQL_DB;User=$MYSQL_USER;Password=$MYSQL_PASSWORD;"
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
    listen 80;
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
NGINXEOF
    
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
configure_additional() {
    print_header "⚙️  Additional Configuration"
    
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
    
    if [ "$ARCHITECTURE" = "microservices" ]; then
        echo
        echo -e "${BLUE}Redis is used for real-time SignalR communication between containers.${NC}"
        prompt_yes_no "Use persistent Redis storage?" "no" "REDIS_PERSIST"
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
        echo -e "${BLUE}Configure slicer workers. You can enable OrcaSlicer and/or PrusaSlicer workers and specify replica counts.${NC}"
    # Default to 'no' to avoid accidental enabling when slicer work is paused
    prompt_yes_no "Enable OrcaSlicer worker(s)?" "no" "ENABLE_ORCA_WORKER"
        if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
            prompt_with_default "OrcaSlicer version to deploy:" "${ORCASLICER_VERSION:-2.3.1}" "ORCASLICER_VERSION"
            prompt_with_default "Number of OrcaSlicer worker replicas:" "1" "ORCA_WORKER_COUNT"
        else
            ORCA_WORKER_COUNT=0
        fi

        prompt_yes_no "Enable PrusaSlicer worker(s)?" "no" "ENABLE_PRUSA_WORKER"
        if [ "$ENABLE_PRUSA_WORKER" = "yes" ]; then
            prompt_with_default "PrusaSlicer version to deploy:" "${PRUSASLICER_VERSION:-2.9.3}" "PRUSASLICER_VERSION"
            prompt_with_default "Number of PrusaSlicer worker replicas:" "1" "PRUSA_WORKER_COUNT"
        else
            PRUSA_WORKER_COUNT=0
        fi

        # Allow endpoint override (advanced) only if microservices; monolithic uses host networking and localhost
        if [ "$ARCHITECTURE" = "microservices" ]; then
            prompt_yes_no "Override default worker service endpoints?" "no" "OVERRIDE_WORKER_ENDPOINTS"
            if [ "$OVERRIDE_WORKER_ENDPOINTS" = "yes" ]; then
                if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
                    prompt_with_default "OrcaSlicer worker endpoint (API reachable URL):" "http://orcaslicer-worker:8080" "ORCA_WORKER_ENDPOINT"
                fi
                if [ "$ENABLE_PRUSA_WORKER" = "yes" ]; then
                    prompt_with_default "PrusaSlicer worker endpoint (API reachable URL):" "http://prusaslicer-worker:8080" "PRUSA_WORKER_ENDPOINT"
                fi
            fi
        fi
    else
        ENABLE_ORCA_WORKER=no
        ENABLE_PRUSA_WORKER=no
        ORCA_WORKER_COUNT=0
        PRUSA_WORKER_COUNT=0
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

# Generate environment file
generate_env_file() {
    print_header "📝 Generating Configuration"
    
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
    
    # Always expose a unified default connection string key consumed by Program.cs
    echo "ConnectionStrings__Default=$CONNECTION_STRING" >> "$ENV_FILE"
    
    cat >> "$ENV_FILE" << EOF

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
PRUSA_WORKER_COUNT=$PRUSA_WORKER_COUNT
ENABLE_ORCA_WORKER=$ENABLE_ORCA_WORKER
ENABLE_PRUSA_WORKER=$ENABLE_PRUSA_WORKER
ORCA_HOST_PORT=$ORCA_HOST_PORT
PRUSA_HOST_PORT=$PRUSA_HOST_PORT

# Slicer Versions
ORCASLICER_VERSION=${ORCASLICER_VERSION:-2.3.1}
PRUSASLICER_VERSION=${PRUSASLICER_VERSION:-2.9.3}

# Spoolman
SPOOLMAN_ENABLED=$ENABLE_SPOOLMAN
SPOOLMAN_BASE_URL=$SPOOLMAN_BASE_URL
SPOOLMAN_PORT=$SPOOLMAN_PORT

# Port Configuration
HTTP_PORT=$HTTP_PORT
EOF
    
    if [ "$ARCHITECTURE" = "microservices" ]; then
        cat >> "$ENV_FILE" << EOF
API_PORT=$API_PORT

# Redis Configuration
REDIS_CONNECTION=redis:6379
EOF
        
        if [ "${REDIS_PERSIST:-no}" = "yes" ]; then
            echo "REDIS_PERSISTENCE=yes" >> "$ENV_FILE"
        fi
    fi
    
    if [ "${INCLUDE_POSTGRES:-no}" = "yes" ]; then
        cat >> "$ENV_FILE" << EOF

# PostgreSQL Configuration
POSTGRES_DB=${POSTGRES_DB:-printfarmer}
POSTGRES_USER=${POSTGRES_USER:-postgres}
POSTGRES_PASSWORD=${POSTGRES_PASSWORD:-$DB_PASSWORD}
EOF
    fi
    
    if [ "${INCLUDE_SQLSERVER:-no}" = "yes" ]; then
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
    
    if [ "${INCLUDE_MYSQL:-no}" = "yes" ]; then
        cat >> "$ENV_FILE" << EOF

# MySQL Configuration
MYSQL_DB=${MYSQL_DB:-printfarmer}
MYSQL_USER=${MYSQL_USER:-root}
MYSQL_ROOT_PASSWORD=${MYSQL_PASSWORD:-$DB_PASSWORD}
MYSQL_DATABASE=${MYSQL_DB:-printfarmer}
EOF
    fi
    
    print_success "Environment file created: $ENV_FILE"
}

# Generate React .env.production file for Docker builds
generate_react_env_production() {
    local react_dir="src/Web/ReactApp"
    
    if [ ! -d "$react_dir" ]; then
        print_warning "React app directory not found, skipping React environment setup"
        return 0
    fi
    
    print_info "Creating React production environment file"
    
    cat > "$react_dir/.env.production" << 'EOF'
# React Production Build Configuration
# Auto-generated by deploy-docker.sh
# These relative URLs work through the Nginx proxy in Docker deployment

# API base URL - relative path routes through Nginx
VITE_API_BASE_URL=/api

# SignalR hub URL - relative path routes through Nginx
VITE_SIGNALR_PRINTERS_URL=/hubs/printers
EOF
    
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
    image: postgres:15-alpine
    environment:
      - POSTGRES_DB=\${POSTGRES_DB}
      - POSTGRES_USER=\${POSTGRES_USER}
      - POSTGRES_PASSWORD=\${POSTGRES_PASSWORD}
    ports:
      - "5432:5432"
    volumes:
      - postgres_data:/var/lib/postgresql/data
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
  # Redis for job queuing and worker coordination  
  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    networks:
      - printfarmer-network
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 5s
      retries: 5
    volumes:
      - redis_data:/data
    command: redis-server --appendonly yes --maxmemory 256mb --maxmemory-policy allkeys-lru

MAINEOF

        # Add the appropriate database service based on DB_PROVIDER
        case "${DB_PROVIDER:-postgres}" in
            postgres)
                cat >> docker-compose.host-network.yml << 'DBEOF'
  # PostgreSQL Database
  database:
    image: postgres:15-alpine
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
      redis:
        condition: service_healthy
    restart: on-failure:5
    environment:
      - ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-Production}
      - ASPNETCORE_URLS=http://0.0.0.0:${API_PORT:-5245}
      - API_URL=http://localhost:${API_PORT:-5245}
      - DB_PROVIDER=${DB_PROVIDER:-Postgres}
      - ConnectionStrings__Default=${ConnectionStrings__Default}
      - ConnectionStrings__Redis=localhost:6379
      - CORS__AllowedOrigins=${CORS__AllowedOrigins:-http://localhost:3000,http://localhost:8080}
      - DOCKER_HOST_NETWORK=true
      - NETWORK_MODE=host
      - ALLOW_LOCAL_NETWORK=${ALLOW_LOCAL_NETWORK:-true}
      - ALLOWED_NETWORK_RANGES=${ALLOWED_NETWORK_RANGES:-192.168.0.0/16,10.0.0.0/8}
      - DEPLOYMENT_MODE=microservices
      - Logging__LogLevel__Default=Information
      - Logging__LogLevel__Microsoft.AspNetCore=Warning
      - SlicerOrchestrator__EnableDistributedSlicing=${ENABLE_DISTRIBUTED_SLICING:-true}
      - SlicerOrchestrator__Workers__OrcaSlicer=${ORCA_WORKER_ENDPOINT:-http://localhost:8081}
      - SlicerOrchestrator__Workers__PrusaSlicer=${PRUSA_WORKER_ENDPOINT:-http://localhost:8082}
      - PFARM__Spoolman__BaseUrl=${PFARM__Spoolman__BaseUrl:-}
      - PFARM__NetworkDiscovery__EnableDiscovery=${PFARM__NetworkDiscovery__EnableDiscovery:-true}
      - PFARM__NetworkDiscovery__DiscoverySubnets=${PFARM__NetworkDiscovery__DiscoverySubnets:-}
    volumes:
      - app_data:/data
      - model_uploads:/app/uploads
      - gcode_storage:/app/gcode
      - slicer_profiles:/app/profiles
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
      dockerfile: Dockerfile.orcaslicer
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
      - ConnectionStrings__Redis=redis:6379
      - Worker__StorageEndpoint=http://localhost:${API_PORT:-5245}
      - Worker__WorkingDirectory=/app/temp
      - Worker__OrcaSlicerPath=/usr/local/bin/orcaslicer
      - Worker__WorkerId=orcaslicer-worker-1
      - Worker__QueueName=orcaslicer-jobs
      - Logging__LogLevel__Default=Information
    volumes:
      - orcaslicer_temp:/app/temp
      - gcode_storage:/app/gcode
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 90s

  # PrusaSlicer Worker - Distributed slicing microservice
  prusaslicer-worker:
    build:
      context: .
      dockerfile: Dockerfile.prusaslicer
    profiles:
      - prusa
    image: printfarmer-prusaslicer-worker
    restart: unless-stopped
    ports:
      - "8082:8080"
    networks:
      - printfarmer-network
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:8080
      - ConnectionStrings__Redis=redis:6379
      - Worker__StorageEndpoint=http://localhost:${API_PORT:-5245}
      - Worker__WorkingDirectory=/app/temp
      - Worker__PrusaSlicerPath=/usr/local/bin/prusa-slicer
      - Worker__WorkerId=prusaslicer-worker-1
      - Worker__QueueName=prusaslicer-jobs
      - Logging__LogLevel__Default=Information
      - Logging__LogLevel__Farm.PrusaSlicer.Worker=Debug
    volumes:
      - prusaslicer_temp:/app/temp
      - gcode_storage:/app/gcode
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 120s

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

networks:
  printfarmer-network:
    driver: bridge

volumes:
  redis_data:
  postgres_data:
  sqlserver_data:
  mysql_data:
  app_data:
  model_uploads:
  gcode_storage:
  slicer_profiles:
  orcaslicer_temp:
  prusaslicer_temp:
RESTEOF
        
        print_success "Host network compose file created: docker-compose.host-network.yml"
        print_warning "API will bind directly to host port ${API_PORT:-5245}"
        print_warning "Database and Redis accessible on localhost (host networking)"
        print_info "Workers and frontend use bridge network, API uses host network"
        print_info "This file is standalone - do NOT combine with docker-compose.microservices.yml"
    fi
}

# Build and deploy
deploy_containers() {
    print_header "🚀 Building and Deploying Containers"
    
    print_info "Step 1/3: Building Docker images..."
    print_info "This may take several minutes on first run..."
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
        # PRUSA_PRESEED_PATH/URL/IMAGE similar for Prusa
        ORCA_ASSET_IMAGE=${ORCA_ASSET_IMAGE:-}
        ORCA_ASSET_PATH=${ORCA_ASSET_PATH:-}
        ORCA_ASSET_URL=${ORCA_ASSET_URL:-}
        PRUSA_PRESEED_PATH=${PRUSA_PRESEED_PATH:-}
        PRUSA_PRESEED_URL=${PRUSA_PRESEED_URL:-}

        # Prepare a temporary build_context folder that will be used by docker compose build
        BUILD_CTX_DIR="./.tmp_build_context"
        mkdir -p "$BUILD_CTX_DIR"

        if [ -n "$ORCA_ASSET_IMAGE" ]; then
            print_info "Using Orca assets image: $ORCA_ASSET_IMAGE"
            docker pull "$ORCA_ASSET_IMAGE" || print_warning "Failed to pull $ORCA_ASSET_IMAGE; continuing and hoping it's local"
            # Tag locally so Dockerfile can refer to orcaslicer-assets:ci
            docker tag "$ORCA_ASSET_IMAGE" orcaslicer-assets:ci || true
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

        # Copy prusa preseed artifact into build context if provided
        if [ -n "$PRUSA_PRESEED_PATH" ] && [ -d "$PRUSA_PRESEED_PATH" ]; then
            print_info "Copying Prusa preseed from $PRUSA_PRESEED_PATH into temporary build context"
            rm -rf "$BUILD_CTX_DIR/prusa" || true
            mkdir -p "$BUILD_CTX_DIR/prusa"
            cp -a "$PRUSA_PRESEED_PATH"/. "$BUILD_CTX_DIR/prusa/"
        elif [ -n "$PRUSA_PRESEED_URL" ]; then
            print_info "Downloading Prusa preseed from $PRUSA_PRESEED_URL into temporary build context"
            mkdir -p "$BUILD_CTX_DIR/prusa" && curl -fsSL "$PRUSA_PRESEED_URL" -o "$BUILD_CTX_DIR/prusa/prusa_artifact" || print_warning "Download failed"
        fi

        # If we prepared files into .tmp_build_context, make them available to docker-compose by copying into repo root under build_context/
        if [ -d "$BUILD_CTX_DIR" ]; then
            rm -rf ./build_context || true
            mv "$BUILD_CTX_DIR" ./build_context
            print_info "Prepared temporary build_context at ./build_context"
        fi

        # Build orcaslicer-assets:local first if orca worker is enabled (required dependency)
        if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
            # Check if we have a real AppImage (non-empty, reasonable size > 1MB)
            if [ ! -f "./orcaslicer.AppImage" ] || [ ! -s "./orcaslicer.AppImage" ] || [ "$(stat -f%z "./orcaslicer.AppImage" 2>/dev/null || stat -c%s "./orcaslicer.AppImage" 2>/dev/null || echo 0)" -lt 1000000 ]; then
                print_info "No valid orcaslicer.AppImage found, downloading latest release..."
                
                # Fetch latest release info from GitHub API
                ORCA_API_URL="https://api.github.com/repos/SoftFever/OrcaSlicer/releases/latest"
                ORCA_RELEASE_JSON=$(curl -s "$ORCA_API_URL")
                
                # Extract Linux AppImage URL (pattern: OrcaSlicer_Linux_*.AppImage)
                ORCA_DOWNLOAD_URL=$(echo "$ORCA_RELEASE_JSON" | grep -o '"browser_download_url": "[^"]*' | grep -o 'https://[^"]*' | grep 'Linux.*\.AppImage$' | head -1)
                
                if [ -z "$ORCA_DOWNLOAD_URL" ]; then
                    print_error "Failed to find OrcaSlicer AppImage download URL"
                    print_error "Please manually download from: https://github.com/SoftFever/OrcaSlicer/releases"
                    print_error "Place the AppImage in the repository root as 'orcaslicer.AppImage'"
                    exit 1
                fi
                
                print_info "Downloading OrcaSlicer from: $ORCA_DOWNLOAD_URL"
                if curl -L -o orcaslicer.AppImage "$ORCA_DOWNLOAD_URL"; then
                    chmod +x orcaslicer.AppImage
                    print_success "Downloaded OrcaSlicer AppImage ($(du -h orcaslicer.AppImage | cut -f1))"
                else
                    print_error "Failed to download OrcaSlicer AppImage"
                    exit 1
                fi
            else
                print_info "Found existing orcaslicer.AppImage ($(du -h orcaslicer.AppImage | cut -f1))"
            fi
            
            print_info "Building orcaslicer-assets:local image..."
            if docker build -f Dockerfile.orca-assets -t orcaslicer-assets:local .; then
                print_success "orcaslicer-assets:local image built successfully"
            else
                print_error "Failed to build orcaslicer-assets:local image"
                exit 1
            fi
        fi

        # Build slicer-base first if workers are enabled (required dependency)
        if [ "$ENABLE_ORCA_WORKER" = "yes" ] || [ "$ENABLE_PRUSA_WORKER" = "yes" ]; then
            print_info "Building printfarmer-slicer-base image (required for worker containers)..."
            if docker build -f Dockerfile.slicer-base -t printfarmer-slicer-base:latest .; then
                print_success "printfarmer-slicer-base image built successfully"
            else
                print_error "Failed to build printfarmer-slicer-base image"
                exit 1
            fi
        fi
        
        # Now build all services
        if "${compose_cmd[@]}" build --no-cache; then
            print_success "Docker images built successfully"
        else
            print_error "Failed to build Docker images"
            exit 1
        fi
    fi
    
    print_info "Step 2/3: Starting containers..."
    print_info "Bringing up services with configuration from $ENV_FILE"

    # Activate profiles for enabled workers (compose v2 profiles)
    # Build complete compose command with profiles BEFORE the 'up' subcommand
    local final_compose_cmd=("${compose_cmd[@]}")
    
    if [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -gt 0 ]; then
        final_compose_cmd+=(--profile orca)
    fi
    if [ "$ENABLE_PRUSA_WORKER" = "yes" ] && [ "$PRUSA_WORKER_COUNT" -gt 0 ]; then
        final_compose_cmd+=(--profile prusa)
    fi

    # Bring up services
    if [ "$DRY_RUN" = "true" ]; then
        print_info "Dry-run mode: not starting containers."
        print_info "Would run: ${final_compose_cmd[*]} up -d"
    else
        # If microservices architecture, start DB and Redis first to speed up readiness
        if [ "$ARCHITECTURE" = "microservices" ]; then
            print_info "Bringing up database and redis services first to speed readiness"
            # Attempt to start postgres/mysql/sqlserver and redis only
            local seed_cmd=("")
            # Build a minimal compose command for core infra
            local infra_compose=(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE")
            if [ -f docker-compose.override.yml ]; then
                infra_compose+=( -f docker-compose.override.yml )
            fi

            # Decide which services to start
            local infra_services=(redis)
            case "${DB_PROVIDER:-postgres}" in
                postgres) infra_services+=(postgres) ;;
                sqlserver) infra_services+=(sqlserver) ;;
                mysql) infra_services+=(mysql) ;;
            esac

            # Run infra services up
            # Optionally include --remove-orphans
            local remove_orphans_flag=""
            if [ "${COMPOSE_REMOVE_ORPHANS}" = "true" ] || [ "${COMPOSE_REMOVE_ORPHANS}" = "1" ]; then
                remove_orphans_flag="--remove-orphans"
            fi

            # Preflight: if starting sqlserver, ensure host port is free to avoid Docker bind errors
            if echo " ${infra_services[*]} " | grep -q " sqlserver "; then
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
                print_success "Core infra (DB, Redis) started"
            else
                print_warning "Failed to start infra services - continuing to full bring-up"
            fi

            # Wait for DB health/readiness
            wait_for_database || true

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
                print_success "API is healthy - proceeding to start remaining services"
            else
                print_warning "API did not become healthy within timeout. Proceeding to start remaining services anyway. Monitor API logs for issues."
            fi

            # Now start the remaining services (frontend, workers, etc.)
            if "${final_compose_cmd[@]}" up -d; then
                print_success "All containers started successfully"
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
    if [ "$DRY_RUN" != "true" ] && [ "$ENABLE_PRUSA_WORKER" = "yes" ] && [ "$PRUSA_WORKER_COUNT" -gt 1 ]; then
        print_info "Scaling PrusaSlicer workers to $PRUSA_WORKER_COUNT replicas"
        "${final_compose_cmd[@]}" up -d --scale prusaslicer-worker="$PRUSA_WORKER_COUNT"
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
            local unhealthy_count=$(docker compose --env-file "$ENV_FILE" ps --format json 2>/dev/null | grep -E '"Health":"(starting|unhealthy)"' | wc -l | tr -d ' ')
            
            if [ "$unhealthy_count" -eq 0 ]; then
                all_healthy=true
                print_success "All containers are healthy!"
                break
            fi
            
            # Show progress
            if [ $((elapsed % 15)) -eq 0 ]; then
                print_info "Still waiting for services to become healthy... ($elapsed seconds elapsed)"
                docker compose --env-file "$ENV_FILE" ps --format "table {{.Name}}\t{{.Status}}" 2>/dev/null | grep -E "starting|unhealthy" || true
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

    print_info "Waiting for database service to be healthy (timeout configurable via DB_WAIT_TIMEOUT env)..."
    local timeout=${DB_WAIT_TIMEOUT:-120}
    local interval=3
    local elapsed=0

    # Determine DB service name from DB_PROVIDER
    local db_service="postgres"
    case "${DB_PROVIDER:-postgres}" in
        postgres) db_service="postgres" ;;
        sqlserver) db_service="sqlserver" ;;
        mysql) db_service="mysql" ;;
        *) db_service="postgres" ;;
    esac

    while [ $elapsed -lt $timeout ]; do
        # Use docker compose ps JSON to look for Health or rely on container's port availability
        # Prefer checking container health status if available
        local health_state
        health_state=$(docker compose --env-file "$ENV_FILE" ps --format json 2>/dev/null | grep -o '"Name":"[^"]*' | grep -o '[^\"]*$' | while read -r name; do
            # Match service by suffix
            if echo "$name" | grep -q "$db_service"; then
                docker inspect --format='{{json .State.Health.Status}}' "$name" 2>/dev/null || echo "unknown"
            fi
        done | head -n1 | tr -d '"') || true

        if [ "$health_state" = "healthy" ]; then
            print_success "Database ($db_service) reports healthy"
            return 0
        fi

        # As fallback, attempt a simple TCP connect to common DB ports
        if [ "${DB_PROVIDER:-postgres}" = "postgres" ]; then
            # Port 5432 inside compose network exposed to host as 5432 by override; try localhost:5432
            if nc -z localhost 5432 2>/dev/null; then
                print_success "Database port 5432 reachable on localhost"
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
            docker compose --env-file "$ENV_FILE" ps --format "table {{.Name}}\t{{.Status}}" 2>/dev/null | grep -E "starting|unhealthy|health" || true
        fi

        sleep $interval
        elapsed=$((elapsed + interval))
    done

    print_warning "Timeout waiting for database to be healthy after ${timeout}s. Proceeding, but API may log connection errors until DB is ready."
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
            echo "$orphans" | xargs -r docker rm -f || true
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
                echo "$orphans" | xargs -r docker rm -f || true
                print_success "Removed orphan containers"
                break
                ;;
            "Show logs")
                echo "$orphans" | while read -r c; do
                    print_info "Logs for $c:"; docker logs --tail 50 "$c" || true; echo; done
                ;;
            "Skip")
                print_info "Skipping orphan removal"
                break
                ;;
        esac
    done
}


wait_for_api() {
    print_info "Waiting for API to become healthy (timeout configurable via API_WAIT_TIMEOUT)..."
    local timeout=${API_WAIT_TIMEOUT:-120}
    local interval=3
    local elapsed=0
    local api_url="http://localhost:${API_PORT:-5245}/healthz"

    while [ $elapsed -lt $timeout ]; do
        if curl -sf "$api_url" >/dev/null 2>&1; then
            print_success "API responded to health check: $api_url"
            return 0
        fi

        if [ $((elapsed % 15)) -eq 0 ]; then
            print_info "Still waiting for API to be healthy... ($elapsed/$timeout seconds)"
            docker compose --env-file "$ENV_FILE" ps --format "table {{.Name}}\t{{.Status}}" 2>/dev/null | grep -E "api|frontend|orcaslicer|prusaslicer" || true
        fi

        sleep $interval
        elapsed=$((elapsed + interval))
    done

    print_warning "Timeout waiting for API health after ${timeout}s. Proceeding with deployment but UI may show errors until API is ready."
    # If configured, fail the deployment on API health timeout
    # API_FAIL_ON_TIMEOUT: if "true" (default), exit with non-zero to stop deployment
    local fail_on_timeout=${API_FAIL_ON_TIMEOUT:-true}

    if [ "$fail_on_timeout" = "true" ] || [ "$fail_on_timeout" = "1" ]; then
        print_error "API did not become healthy within ${timeout}s and API_FAIL_ON_TIMEOUT is enabled. Failing deployment."
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

    return 1
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
    docker compose --env-file "$ENV_FILE" ps
    echo
    
    print_info "Running comprehensive health checks..."
    local health_check_failed=false
    
    # Test basic health endpoint
    print_info "Testing basic health endpoint..."
    local basic_health=$(curl -s "$api_url/healthz" 2>/dev/null)
    if [ -n "$basic_health" ] && echo "$basic_health" | grep -q '"status":"ok"'; then
        print_success "✓ Basic health check: OK"
    else
        print_warning "✗ Basic health check: FAILED (endpoint not responding or unexpected response)"
        if [ -n "$basic_health" ]; then
            print_info "Response received: $basic_health"
        fi
        health_check_failed=true
    fi
    
    # Test comprehensive health endpoint
    print_info "Testing comprehensive health endpoint..."
    local health_json=$(curl -s "$api_url/health" 2>/dev/null)
    
    if [ -n "$health_json" ]; then
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
            print_info "Full health check result:"
            if command -v jq >/dev/null 2>&1; then
                echo "$health_json" | jq '.' 2>/dev/null || echo "$health_json"
            else
                echo "$health_json"
            fi
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
    
    # Test API endpoints
    print_info "Testing API endpoints..."
    if curl -sf "$api_url/api/printers" >/dev/null 2>&1; then
        print_success "✓ API endpoints: OK (/api/printers responding)"
    else
        print_warning "✗ API endpoints: Not ready yet"
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
    
    if [ "$ENABLE_PRUSA_WORKER" = "yes" ]; then
        print_info "Testing PrusaSlicer worker..."
        local prusa_url="http://localhost:${PRUSA_HOST_PORT:-8082}"
        if curl -sf "$prusa_url/healthz" >/dev/null 2>&1; then
            print_success "✓ PrusaSlicer worker: Healthy"
        else
            print_warning "✗ PrusaSlicer worker: Not responding"
            health_check_failed=true
        fi
    fi
    
    echo
    if [ "$health_check_failed" = true ]; then
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
        echo -e "${BLUE}  • Prusa Workers: $PRUSA_WORKER_COUNT (enabled: $ENABLE_PRUSA_WORKER)${NC}"
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

# Main execution
main() {
    # Handle help mode first
    if [ "$SHOW_HELP" = "true" ]; then
        show_help
        # Function exits, so we never reach here
    fi
    
    # Handle tear-down mode
    if [ "$TEAR_DOWN" = "true" ]; then
        tear_down_deployment
        # Function exits, so we never reach here
    fi
    
    print_header "🚀 PrintFarmer Docker Deployment Setup"
    
    print_info "This script will help you deploy PrintFarmer using Docker containers."
    print_info "You'll be prompted for configuration with sensible defaults provided."
    echo
    
    # Check if we're in the right directory
    if [ ! -f "docker-compose.yml" ] || [ ! -f "global.json" ]; then
        print_error "Please run this script from the PrintFarmer root directory"
        print_info "Expected files: docker-compose.yml, global.json"
        exit 1
    fi
    
    # Load previous configuration if available (sets defaults for interactive mode)
    load_previous_config || true
    
    # Execute setup steps
    detect_environment
    choose_architecture
    configure_database
    configure_networking
    adjust_connection_strings_for_network_mode
    configure_additional
    validate_configuration
    save_deployment_config
    generate_env_file
    generate_react_env_production
    generate_compose_override
    generate_host_network_override
    deploy_containers
    
    # Run verification and capture result
    local verification_passed=true
    verify_deployment || verification_passed=false
    
    display_final_info "$verification_passed"
    
    if [ "$verification_passed" = true ]; then
        print_success "Setup completed successfully! 🎉"
    else
        print_warning "Setup completed with warnings - please check health status above"
        print_info "Services may need a few more moments to fully initialize"
        exit 1
    fi
}

# Run main function
main "$@"
