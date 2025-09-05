#!/bin/bash

# PrintFarmer Docker Deployment Script
# Automated setup for Docker-based deployment with user-friendly prompts

set -euo pipefail

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
    
    echo -e "${YELLOW}$prompt${NC}"
    echo -e "${BLUE}Default: $default${NC}"
    read -r input
    
    if [ -z "$input" ]; then
        eval "$var_name=\"$default\""
    else
        eval "$var_name=\"$input\""
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
    
    echo -e "${YELLOW}$prompt [$default_text]${NC}"
    read -r input
    
    if [ -z "$input" ]; then
        input="$default"
    fi
    
    case "$input" in
        [Yy]|[Yy]es)
            eval "$var_name=\"yes\""
            ;;
        *)
            eval "$var_name=\"no\""
            ;;
    esac
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
            COMPOSE_FILE="docker-compose.yml"
            print_success "Selected: Microservices deployment"
            ;;
        *)
            print_error "Invalid choice. Please run the script again."
            exit 1
            ;;
    esac
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
    else
        ALLOW_LOCAL_NETWORK="false"
        NETWORK_RANGES=""
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
}

# Generate environment file
generate_env_file() {
    print_header "📝 Generating Configuration"
    
    print_info "Creating environment file: $ENV_FILE"
    
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
    
    case "$DB_PROVIDER" in
        sqlite)
            echo "ConnectionStrings__Default=$CONNECTION_STRING" >> "$ENV_FILE"
            ;;
        postgres)
            echo "ConnectionStrings__Postgres=$CONNECTION_STRING" >> "$ENV_FILE"
            ;;
        sqlserver)
            echo "ConnectionStrings__SqlServer=$CONNECTION_STRING" >> "$ENV_FILE"
            ;;
        mysql)
            echo "ConnectionStrings__MySql=$CONNECTION_STRING" >> "$ENV_FILE"
            ;;
    esac
    
    cat >> "$ENV_FILE" << EOF

# Network Configuration
ALLOW_LOCAL_NETWORK=$ALLOW_LOCAL_NETWORK
ALLOWED_NETWORK_RANGES=$NETWORK_RANGES

# Feature Flags  
ENABLE_SWAGGER=$ENABLE_SWAGGER
ENABLE_DETAILED_LOGGING=$ENABLE_DETAILED_LOGGING

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
    fi
}

# Build and deploy
deploy_containers() {
    print_header "🚀 Building and Deploying Containers"
    
    print_info "Building Docker images..."
    if docker compose --env-file "$ENV_FILE" build --no-cache; then
        print_success "Docker images built successfully"
    else
        print_error "Failed to build Docker images"
        exit 1
    fi
    
    print_info "Starting containers..."
    if docker compose --env-file "$ENV_FILE" up -d; then
        print_success "Containers started successfully"
    else
        print_error "Failed to start containers"
        exit 1
    fi
    
    print_info "Waiting for services to be ready..."
    sleep 15
}

# Verify deployment
verify_deployment() {
    print_header "🔍 Verifying Deployment"
    
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
    
    print_success "PrintFarmer is now running!"
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
    echo -e "${BLUE}  • View logs:      docker compose --env-file $ENV_FILE logs -f${NC}"
    echo -e "${BLUE}  • Stop services:  docker compose --env-file $ENV_FILE down${NC}"
    echo -e "${BLUE}  • Update/restart: docker compose --env-file $ENV_FILE up -d --build${NC}"
    echo
    
    if [ "$ENABLE_DISCOVERY" = "yes" ]; then
        echo -e "${GREEN}Network Discovery:${NC}"
        echo -e "${BLUE}  • Configured ranges: $NETWORK_RANGES${NC}"
        if [ "$OS" = "macos" ]; then
            print_warning "  Note: macOS Docker may have limited WiFi device access"
        fi
        echo
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
    generate_env_file
    generate_compose_override
    deploy_containers
    verify_deployment
    display_final_info
    
    print_success "Setup completed successfully! 🎉"
}

# Run main function
main "$@"
