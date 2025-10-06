#!/bin/bash

# PrintFarmer Docker Deployment Script
# Automated setup for Docker-based deployment with user-friendly prompts

set -euo pipefail

# Default flags
DRY_RUN=false
NON_INTERACTIVE=false

# Parse simple flags early (only --dry-run / -n for now)
for arg in "$@"; do
    case "$arg" in
        --dry-run|-n)
            DRY_RUN=true
            ;;
        --non-interactive|--batch|-b)
            NON_INTERACTIVE=true
            ;;
    esac
done

# Allow env override for automated pipelines
if [ "${NON_INTERACTIVE:-}" = "1" ]; then
    NON_INTERACTIVE=true
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
    
    if [ "$NON_INTERACTIVE" = "true" ]; then
        # If variable already exported, respect it; else use default
        if [ -n "${!var_name:-}" ]; then
            return 0
        fi
        eval "$var_name=\"$default\""
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
    
    local default_text="y/N"
    if [ "$default" = "y" ] || [ "$default" = "yes" ]; then
        default_text="Y/n"
    fi
    
    if [ "$NON_INTERACTIVE" = "true" ]; then
        # If pre-set variable, respect truthy/falsey values
        local current=${!var_name:-}
        if [ -n "$current" ]; then
            case "$current" in
                [Yy]|[Yy]es|true|1) eval "$var_name=\"yes\"" ;;
                *) eval "$var_name=\"no\"" ;;
            esac
        else
            # fallback to default
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
    
    prompt_with_default "Choose architecture [1=Monolithic, 2=Microservices]:" "1" "ARCH_CHOICE"
    
    case "$ARCH_CHOICE" in
        1|monolithic|mono)
            ARCHITECTURE="monolithic"
            ENV_FILE=".env.monolithic"
            COMPOSE_FILE="docker-compose.yml"
            print_success "Selected: Monolithic deployment"
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
        
        prompt_with_default "Database provider [sqlite/postgres/sqlserver/mysql]:" "sqlite" "DB_PROVIDER"
        
        case "$DB_PROVIDER" in
            sqlite)
                CONNECTION_STRING="Data Source=/data/farm.db"
                print_success "Using SQLite - Data will persist in Docker volume"
                ;;
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
                print_warning "Unknown database provider, using SQLite as fallback"
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
        
        prompt_with_default "Database provider [postgres/sqlserver/mysql/external]:" "postgres" "DB_PROVIDER"
        
        case "$DB_PROVIDER" in
            postgres)
                prompt_with_default "PostgreSQL password:" "postgres" "DB_PASSWORD"
                CONNECTION_STRING="Host=postgres;Database=printfarmer;Username=postgres;Password=$DB_PASSWORD"
                INCLUDE_POSTGRES="yes"
                ;;
            sqlserver)
                prompt_with_default "SQL Server SA password:" "YourStrong!Password123" "DB_PASSWORD"
                CONNECTION_STRING="Server=sqlserver;Database=printfarmer;User Id=sa;Password=$DB_PASSWORD;TrustServerCertificate=True;"
                INCLUDE_SQLSERVER="yes"
                ;;
            mysql)
                prompt_with_default "MySQL root password:" "example" "DB_PASSWORD"
                CONNECTION_STRING="Server=mysql;Database=printfarmer;User=root;Password=$DB_PASSWORD;"
                INCLUDE_MYSQL="yes"
                ;;
            external)
                prompt_with_default "External database provider [postgres/sqlserver/mysql]:" "postgres" "EXT_DB_TYPE"
                prompt_with_default "Connection string:" "Host=your-host;Database=printfarmer;Username=user;Password=password" "CONNECTION_STRING"
                DB_PROVIDER="$EXT_DB_TYPE"
                ;;
            *)
                print_warning "Unknown database provider, using PostgreSQL as fallback"
                DB_PROVIDER="postgres"
                DB_PASSWORD="postgres"
                CONNECTION_STRING="Host=postgres;Database=printfarmer;Username=postgres;Password=$DB_PASSWORD"
                INCLUDE_POSTGRES="yes"
                ;;
        esac
    fi
}

# Configure networking
configure_networking() {
    print_header "🌐 Network Configuration"
    
    echo -e "${BLUE}Network discovery allows PrintFarmer to automatically find 3D printers on your network.${NC}"
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
        
        # Host network mode configuration (Linux only)
        echo
        echo -e "${BLUE}Network Mode for API Container:${NC}"
        echo "  ${BLUE}1.${NC} Bridge (default) - Works on all platforms, limited broadcast/multicast"
        echo "  ${BLUE}2.${NC} Host (advanced) - Direct host network access, full discovery support"
        echo
        
        if [ "$OS" != "linux" ]; then
            print_warning "Host network mode only works on Linux. Forcing bridge mode."
            NETWORK_MODE="bridge"
        else
            echo -e "${YELLOW}For optimal network discovery (broadcast/multicast), choose host mode.${NC}"
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
    else
        ALLOW_LOCAL_NETWORK="false"
        NETWORK_RANGES=""
        NETWORK_MODE="bridge"
    fi
    
    echo
    echo -e "${BLUE}Configure external access:${NC}"
    prompt_with_default "HTTP port for web access:" "8080" "HTTP_PORT"
    
    if [ "$ARCHITECTURE" = "microservices" ]; then
        prompt_with_default "API port (for direct API access):" "5245" "API_PORT"
    fi
}

# Configure additional settings
configure_additional() {
    print_header "⚙️  Additional Configuration"
    
    prompt_with_default "Environment [Development/Production]:" "Production" "ENVIRONMENT"
    
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
        prompt_yes_no "Enable OrcaSlicer worker(s)?" "yes" "ENABLE_ORCA_WORKER"
        if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
            prompt_with_default "Number of OrcaSlicer worker replicas:" "1" "ORCA_WORKER_COUNT"
        else
            ORCA_WORKER_COUNT=0
        fi

        prompt_yes_no "Enable PrusaSlicer worker(s)?" "no" "ENABLE_PRUSA_WORKER"
        if [ "$ENABLE_PRUSA_WORKER" = "yes" ]; then
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
POSTGRES_DB=printfarmer
POSTGRES_USER=postgres  
POSTGRES_PASSWORD=$DB_PASSWORD
EOF
    fi
    
    if [ "${INCLUDE_SQLSERVER:-no}" = "yes" ]; then
        cat >> "$ENV_FILE" << EOF

# SQL Server Configuration
MSSQL_SA_PASSWORD=$DB_PASSWORD
ACCEPT_EULA=Y
EOF
    fi
    
    if [ "${INCLUDE_MYSQL:-no}" = "yes" ]; then
        cat >> "$ENV_FILE" << EOF

# MySQL Configuration
MYSQL_ROOT_PASSWORD=$DB_PASSWORD
MYSQL_DATABASE=printfarmer
EOF
    fi
    
    print_success "Environment file created: $ENV_FILE"
}

# Generate docker-compose override if needed
generate_compose_override() {
    if [ "$ARCHITECTURE" = "microservices" ] && { [ "${INCLUDE_POSTGRES:-no}" = "yes" ] || [ "${INCLUDE_SQLSERVER:-no}" = "yes" ] || [ "${INCLUDE_MYSQL:-no}" = "yes" ]; }; then
        print_info "Creating docker-compose override for database services"
        
        cat > docker-compose.override.yml << EOF
# Auto-generated database services
version: '3.8'

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
    ports:
      - "1433:1433"
    volumes:
      - sqlserver_data:/var/opt/mssql
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P \${MSSQL_SA_PASSWORD} -Q 'SELECT 1'"]
      interval: 30s
      timeout: 10s
      retries: 5
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
        print_info "Creating host network override for API container"
        
        cat > docker-compose.host-network.yml << EOF
# Auto-generated host network configuration for optimal discovery
# Generated by deploy-docker.sh on $(date)
version: '3.8'

services:
  api:
    # Use host networking for full network access
    network_mode: "host"
    # Remove port mapping (conflicts with host mode)
    ports: []
    # Remove networks (not compatible with host mode)
    networks: []
    environment:
      # Bind to specific port on host
      - ASPNETCORE_URLS=http://0.0.0.0:${API_PORT:-5245}
      # When in host mode, connect to Redis on localhost
      - ConnectionStrings__Redis=localhost:6379
      # Mark that we're using host networking
      - DOCKER_HOST_NETWORK=true
      # Pass through other required variables
      - NETWORK_MODE=host
EOF
        
        print_success "Host network override created: docker-compose.host-network.yml"
        print_warning "API will bind directly to host port ${API_PORT:-5245}"
        print_warning "Make sure this port is not in use by another service"
    fi
}

# Build and deploy
deploy_containers() {
    print_header "🚀 Building and Deploying Containers"
    
    print_info "Step 1/3: Building Docker images..."
    print_info "This may take several minutes on first run..."
    # Always include selected compose file
    local compose_cmd=(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE")

    if [ -f docker-compose.override.yml ]; then
        compose_cmd+=( -f docker-compose.override.yml )
    fi
    
    if [ -f docker-compose.host-network.yml ]; then
        compose_cmd+=( -f docker-compose.host-network.yml )
        print_info "Using host network override for API container"
    fi

    if [ "$DRY_RUN" = "true" ]; then
        print_info "Dry-run mode: skipping image build. (Would run: docker compose build)"
    elif "${compose_cmd[@]}" build --no-cache; then
        print_success "Docker images built successfully"
    else
        print_error "Failed to build Docker images"
        exit 1
    fi
    
    print_info "Step 2/3: Starting containers..."
    print_info "Bringing up services with configuration from $ENV_FILE"

    # Activate profiles for enabled workers (compose v2 profiles) in monolithic architecture
    local profiles_to_enable=()
    if [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -gt 0 ]; then
        profiles_to_enable+=(--profile orca)
    fi
    if [ "$ENABLE_PRUSA_WORKER" = "yes" ] && [ "$PRUSA_WORKER_COUNT" -gt 0 ]; then
        profiles_to_enable+=(--profile prusa)
    fi

    # Bring up base services first
    if [ "$DRY_RUN" = "true" ]; then
        print_info "Dry-run mode: not starting containers. (Would run: docker compose up -d ${profiles_to_enable[*]})"
    elif "${compose_cmd[@]}" up -d "${profiles_to_enable[@]}"; then
        print_success "Containers started successfully"
    else
        print_error "Failed to start containers"
        exit 1
    fi

    # Scaling (only if counts >1). Use service names; if profiles not enabled skip scaling.
    if [ "$DRY_RUN" != "true" ] && [ "$ENABLE_ORCA_WORKER" = "yes" ] && [ "$ORCA_WORKER_COUNT" -gt 1 ]; then
        print_info "Scaling OrcaSlicer workers to $ORCA_WORKER_COUNT replicas"
        "${compose_cmd[@]}" up -d --scale orcaslicer-worker="$ORCA_WORKER_COUNT"
    fi
    if [ "$DRY_RUN" != "true" ] && [ "$ENABLE_PRUSA_WORKER" = "yes" ] && [ "$PRUSA_WORKER_COUNT" -gt 1 ]; then
        print_info "Scaling PrusaSlicer workers to $PRUSA_WORKER_COUNT replicas"
        "${compose_cmd[@]}" up -d --scale prusaslicer-worker="$PRUSA_WORKER_COUNT"
    fi
    
    if [ "$DRY_RUN" = "true" ]; then
        print_info "Dry-run complete. No containers launched."
    else
        print_success "Step 3/3: Containers are starting..."
        print_info "Waiting for services to be ready..."
        sleep 15
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
    docker compose --env-file "$ENV_FILE" ps
    echo
    
    print_info "Testing health endpoints..."
    
    # Test basic health
    if curl -sf "$api_url/healthz" >/dev/null; then
        print_success "Basic health check: OK"
    else
        print_warning "Basic health check: FAILED"
        print_info "This might be normal if the service is still starting up"
    fi
    
    # Test comprehensive health
    if curl -sf "$api_url/health" >/dev/null; then
        print_success "Comprehensive health check: OK" 
    else
        print_warning "Comprehensive health check: FAILED"
    fi
    
    # Test API endpoints
    if curl -sf "$api_url/api/printers" >/dev/null; then
        print_success "API endpoints: OK"
    else
        print_warning "API endpoints: Not ready yet"
    fi
    
    echo
    print_success "Deployment verification completed!"
}

# Display final information
display_final_info() {
    print_header "🎉 Deployment Complete"
    
    if [ "$DRY_RUN" = "true" ]; then
        print_success "Dry-run summary (no containers started)"
    else
        print_success "PrintFarmer is now running!"
    fi
    echo
    echo -e "${GREEN}Access URLs:${NC}"
    echo -e "${BLUE}  🌐 Web Interface: http://localhost:$HTTP_PORT${NC}"
    
    if [ "$ARCHITECTURE" = "microservices" ]; then
        echo -e "${BLUE}  🔧 Direct API: http://localhost:$API_PORT${NC}"
    fi
    
    echo -e "${BLUE}  ❤️  Health Check: http://localhost:$HTTP_PORT/healthz${NC}"
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
    
    print_info "For troubleshooting, see: DOCKER_DEPLOYMENT.md"
    print_info "For local development, see: LOCAL_DEVELOPMENT.md"
}

# Main execution
main() {
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
    
    # Execute setup steps
    detect_environment
    choose_architecture
    configure_database
    configure_networking
    configure_additional
    validate_configuration
    generate_env_file
    generate_compose_override
    generate_host_network_override
    deploy_containers
    verify_deployment
    display_final_info
    
    print_success "Setup completed successfully! 🎉"
}

# Run main function
main "$@"
