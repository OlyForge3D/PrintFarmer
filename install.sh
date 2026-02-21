#!/usr/bin/env bash
# PrintFarmer Quick Installer
# Generates a docker-compose.yml and .env for deployment using pre-built images from GHCR.
# End users do NOT need to clone the repo — this script is self-contained.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/jpapiez/PrintFarmer/main/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/jpapiez/PrintFarmer/main/install.sh | bash -s -- --non-interactive
#   curl -fsSL https://raw.githubusercontent.com/jpapiez/PrintFarmer/main/install.sh | bash -s -- --version v1.0.0
#
# Options:
#   --non-interactive     Skip prompts, use defaults
#   --version TAG         Image tag to use (default: latest)
#   --port PORT           HTTP port (default: 8080)
#   --dir DIR             Installation directory (default: ./printfarmer)
#   --with-spoolman URL   Connect to a Spoolman instance (e.g., http://10.0.0.50:7912)
#   --dry-run             Generate files without starting containers
#   --help                Show this help

set -euo pipefail

# ============================================================================
# Configuration defaults
# ============================================================================
REGISTRY_HOST="ghcr.io/jpapiez"
IMAGE_TAG="latest"
HTTP_PORT="8080"
INSTALL_DIR="./printfarmer"
NON_INTERACTIVE=false
SPOOLMAN_URL=""
DRY_RUN=false
SHOW_HELP=false

# ============================================================================
# Colors & helpers
# ============================================================================
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; BOLD='\033[1m'; NC='\033[0m'
info()    { echo -e "${BLUE}[INFO]${NC} $*"; }
success() { echo -e "${GREEN}[OK]${NC} $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC} $*"; }
error()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }
fatal()   { error "$@"; exit 1; }

# ============================================================================
# Parse arguments
# ============================================================================
while [[ $# -gt 0 ]]; do
    case "$1" in
        --non-interactive) NON_INTERACTIVE=true; shift ;;
        --version)         IMAGE_TAG="${2:?--version requires a tag}"; shift 2 ;;
        --version=*)       IMAGE_TAG="${1#*=}"; shift ;;
        --port)            HTTP_PORT="${2:?--port requires a number}"; shift 2 ;;
        --port=*)          HTTP_PORT="${1#*=}"; shift ;;
        --dir)             INSTALL_DIR="${2:?--dir requires a path}"; shift 2 ;;
        --dir=*)           INSTALL_DIR="${1#*=}"; shift ;;
        --with-spoolman)   SPOOLMAN_URL="${2:?--with-spoolman requires a URL}"; shift 2 ;;
        --with-spoolman=*) SPOOLMAN_URL="${1#*=}"; shift ;;
        --dry-run)         DRY_RUN=true; shift ;;
        --help|-h)         SHOW_HELP=true; shift ;;
        *) warn "Unknown option: $1"; shift ;;
    esac
done

if [[ "$SHOW_HELP" == "true" ]]; then
    sed -n '2,/^$/{ s/^# //; s/^#//; p; }' "$0" 2>/dev/null || true
    exit 0
fi

# ============================================================================
# Banner
# ============================================================================
echo ""
echo -e "${BOLD}  ╔═══════════════════════════════════════╗${NC}"
echo -e "${BOLD}  ║       ${GREEN}PrintFarmer Installer${NC}${BOLD}           ║${NC}"
echo -e "${BOLD}  ║   3D Printer Management Dashboard     ║${NC}"
echo -e "${BOLD}  ╚═══════════════════════════════════════╝${NC}"
echo ""

# ============================================================================
# Prerequisites check
# ============================================================================
info "Checking prerequisites..."

check_command() {
    if ! command -v "$1" &>/dev/null; then
        fatal "'$1' is required but not installed. See https://docs.docker.com/get-docker/"
    fi
}
check_command docker
check_command curl

# Verify Docker is running
if ! docker info &>/dev/null; then
    fatal "Docker daemon is not running. Start Docker and try again."
fi

# Verify docker compose is available (v2 plugin or standalone)
if docker compose version &>/dev/null; then
    COMPOSE_CMD="docker compose"
elif command -v docker-compose &>/dev/null; then
    COMPOSE_CMD="docker-compose"
else
    fatal "'docker compose' (v2) is required. Update Docker or install the compose plugin."
fi

success "Docker $(docker --version | grep -oP '\d+\.\d+\.\d+') + $($COMPOSE_CMD version --short 2>/dev/null || $COMPOSE_CMD version | grep -oP '\d+\.\d+\.\d+')"

# ============================================================================
# Interactive prompts (skip with --non-interactive)
# ============================================================================
ask() {
    local prompt="$1" default="$2" var_name="$3"
    if [[ "$NON_INTERACTIVE" == "true" ]]; then
        eval "$var_name=\"$default\""
        return
    fi
    local current_val="${!var_name}"
    read -rp "$(echo -e "${BLUE}?${NC} ${prompt} [${current_val:-$default}]: ")" input
    eval "$var_name=\"${input:-${current_val:-$default}}\""
}

if [[ "$NON_INTERACTIVE" != "true" ]]; then
    echo -e "${BOLD}Configuration${NC}"
    echo ""
fi

ask "Installation directory"      "$INSTALL_DIR"  INSTALL_DIR
ask "HTTP port"                    "$HTTP_PORT"    HTTP_PORT
ask "Image version tag"            "$IMAGE_TAG"    IMAGE_TAG

if [[ "$NON_INTERACTIVE" != "true" && -z "$SPOOLMAN_URL" ]]; then
    ask "Spoolman URL (or blank to skip)" "" SPOOLMAN_URL
fi

echo ""

# ============================================================================
# Generate secrets
# ============================================================================
generate_secret() {
    local length="${1:-48}"
    # Avoid multi-stage pipes to prevent SIGPIPE under set -eo pipefail.
    local raw
    raw="$(openssl rand -base64 256 2>/dev/null || dd if=/dev/urandom bs=256 count=1 2>/dev/null | base64)"
    raw="${raw//[\/+=[:space:]]/}"  # Strip non-alphanumeric chars using parameter expansion
    printf '%s' "${raw:0:$length}"
}

DB_PASSWORD="$(generate_secret 32)"
JWT_KEY="$(generate_secret 64)"

# ============================================================================
# Create installation directory
# ============================================================================
INSTALL_DIR="$(realpath -m "$INSTALL_DIR")"
info "Installing to $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"

# ============================================================================
# Generate .env file
# ============================================================================
info "Generating .env..."
cat > "$INSTALL_DIR/.env" <<ENVEOF
# PrintFarmer Configuration — generated $(date -Iseconds)
# Image settings
REGISTRY_HOST=${REGISTRY_HOST}
IMAGE_TAG=${IMAGE_TAG}

# Ports
HTTP_PORT=${HTTP_PORT}

# Database (PostgreSQL)
POSTGRES_DB=printfarmer
POSTGRES_USER=printfarmer
POSTGRES_PASSWORD=${DB_PASSWORD}

# API
DB_PROVIDER=Postgres
ConnectionStrings__Default=Host=database;Port=5432;Database=printfarmer;Username=printfarmer;Password=${DB_PASSWORD}
Jwt__Key=${JWT_KEY}
Jwt__Issuer=PrintFarmer
Jwt__Audience=PrintFarmer
ASPNETCORE_ENVIRONMENT=Production

# Security
DEVMODE_BYPASS_AUTH=false
ALLOW_LOCAL_NETWORK=true
ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8,172.16.0.0/12

# Network discovery
PFARM__NetworkDiscovery__EnableDiscovery=true

# Spoolman integration (leave blank to disable)
PFARM__Spoolman__BaseUrl=${SPOOLMAN_URL}
ENVEOF

success ".env created"

# ============================================================================
# Generate nginx config
# ============================================================================
info "Generating nginx config..."
mkdir -p "$INSTALL_DIR/nginx"

cat > "$INSTALL_DIR/nginx/nginx-proxy.conf" <<'NGINXEOF'
user nginx;
worker_processes auto;
pid /var/cache/nginx/nginx.pid;
error_log /var/log/nginx/error.log warn;

events { worker_connections 1024; }

http {
    include       /etc/nginx/mime.types;
    default_type  application/octet-stream;
    sendfile on;
    keepalive_timeout 65;
    client_max_body_size 500M;

    gzip on;
    gzip_vary on;
    gzip_min_length 1024;
    gzip_types text/plain text/css text/xml text/javascript application/javascript application/json;

    resolver 127.0.0.11:53 valid=10s;
    resolver_timeout 5s;

    upstream api_backend  { server api:5245; }
    upstream frontend_backend { server frontend:80; }

    server {
        listen 80;
        server_name _;

        # Health
        location = /healthz { proxy_pass http://api_backend/healthz; proxy_http_version 1.1; proxy_set_header Host $host; }
        location = /health  { proxy_pass http://api_backend/health;  proxy_http_version 1.1; proxy_set_header Host $host; }

        # API (long timeout for gcode dispatch)
        location ~ ^/api/job-queue/[^/]+/dispatch$ {
            proxy_pass http://api_backend;
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection 'upgrade';
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_read_timeout 3600s;
            proxy_send_timeout 3600s;
            proxy_buffering off;
        }

        location /api/ {
            proxy_pass http://api_backend;
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection 'upgrade';
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_read_timeout 300s;
            proxy_send_timeout 300s;
            proxy_buffering off;
        }

        # SignalR WebSockets
        location /hubs/ {
            proxy_pass http://api_backend;
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection "upgrade";
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_read_timeout 3600s;
            proxy_send_timeout 3600s;
            proxy_buffering off;
        }

        # Frontend (SPA)
        location / {
            proxy_pass http://frontend_backend;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
        }

        # Security headers
        add_header X-Frame-Options "SAMEORIGIN" always;
        add_header X-Content-Type-Options "nosniff" always;
        add_header X-XSS-Protection "1; mode=block" always;
        add_header Referrer-Policy "strict-origin-when-cross-origin" always;
    }
}
NGINXEOF

success "nginx config created"

# ============================================================================
# Generate docker-compose.yml
# ============================================================================
info "Generating docker-compose.yml..."

cat > "$INSTALL_DIR/docker-compose.yml" <<COMPOSEEOF
# PrintFarmer Docker Compose — generated $(date -Iseconds)
# Images: ${REGISTRY_HOST}/*:${IMAGE_TAG}
# Docs: https://github.com/jpapiez/PrintFarmer

name: printfarmer

services:
  # PostgreSQL database
  database:
    image: postgres:16-alpine
    container_name: printfarmer-database
    restart: unless-stopped
    environment:
      POSTGRES_DB: \${POSTGRES_DB:-printfarmer}
      POSTGRES_USER: \${POSTGRES_USER:-printfarmer}
      POSTGRES_PASSWORD: \${POSTGRES_PASSWORD}
    volumes:
      - printfarmer-database:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U \${POSTGRES_USER:-printfarmer} -d \${POSTGRES_DB:-printfarmer}"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 15s
    networks:
      - printfarmer-network

  # PrintFarmer API
  api:
    image: \${REGISTRY_HOST}/printfarmer-api:\${IMAGE_TAG}
    container_name: printfarmer-api
    restart: unless-stopped
    depends_on:
      database:
        condition: service_healthy
    environment:
      - ASPNETCORE_ENVIRONMENT=\${ASPNETCORE_ENVIRONMENT:-Production}
      - ASPNETCORE_URLS=http://+:5245
      - DB_PROVIDER=\${DB_PROVIDER:-Postgres}
      - ConnectionStrings__Default=\${ConnectionStrings__Default}
      - CORS__AllowedOrigins=http://localhost:3000,http://localhost:\${HTTP_PORT:-8080}
      - DEPLOYMENT_MODE=microservices
      - ALLOW_LOCAL_NETWORK=\${ALLOW_LOCAL_NETWORK:-true}
      - ALLOWED_NETWORK_RANGES=\${ALLOWED_NETWORK_RANGES:-192.168.0.0/16,10.0.0.0/8}
      - Jwt__Key=\${Jwt__Key}
      - Jwt__Issuer=\${Jwt__Issuer:-PrintFarmer}
      - Jwt__Audience=\${Jwt__Audience:-PrintFarmer}
      - Security__DevModeBypassAuth=\${DEVMODE_BYPASS_AUTH:-false}
      - GCODE_STORAGE_PATH=/app/gcode
      - MODEL_UPLOAD_PATH=/app/models
      - DATAPROTECTION_KEYS_PATH=/app/data-protection-keys
      - PFARM__Spoolman__BaseUrl=\${PFARM__Spoolman__BaseUrl:-}
      - PFARM__NetworkDiscovery__EnableDiscovery=\${PFARM__NetworkDiscovery__EnableDiscovery:-true}
      - PFARM__NetworkDiscovery__DiscoverySubnets=\${PFARM__NetworkDiscovery__DiscoverySubnets:-}
      - Logging__LogLevel__Default=Information
      - Logging__LogLevel__Microsoft.AspNetCore=Warning
    volumes:
      - printfarmer-app-data:/data
      - printfarmer-model-storage:/app/models
      - printfarmer-gcode-storage:/app/gcode
      - printfarmer-slicer-profiles:/app/profiles
      - printfarmer-dataprotection-keys:/app/data-protection-keys
    healthcheck:
      test: ["CMD", "sh", "-c", "curl -sf http://localhost:5245/healthz || exit 1"]
      interval: 30s
      timeout: 15s
      retries: 5
      start_period: 120s
    networks:
      - printfarmer-network

  # React Frontend (Nginx)
  frontend:
    image: \${REGISTRY_HOST}/printfarmer-frontend:\${IMAGE_TAG}
    container_name: printfarmer-frontend
    restart: unless-stopped
    depends_on:
      api:
        condition: service_healthy
    environment:
      - NGINX_PROXY_API=http://api:5245
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:80/health"]
      interval: 30s
      timeout: 10s
      retries: 3
    networks:
      - printfarmer-network

  # Nginx reverse proxy — single entry point
  nginx-proxy:
    image: nginx:alpine
    container_name: printfarmer-nginx-proxy
    restart: unless-stopped
    ports:
      - "\${HTTP_PORT:-8080}:80"
    volumes:
      - ./nginx/nginx-proxy.conf:/etc/nginx/nginx.conf:ro
    depends_on:
      - frontend
      - api
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:80/healthz"]
      interval: 30s
      timeout: 10s
      retries: 3
    networks:
      - printfarmer-network

volumes:
  printfarmer-database:
  printfarmer-app-data:
  printfarmer-model-storage:
  printfarmer-gcode-storage:
  printfarmer-slicer-profiles:
  printfarmer-dataprotection-keys:

networks:
  printfarmer-network:
    name: printfarmer-network
    driver: bridge
COMPOSEEOF

success "docker-compose.yml created"

# ============================================================================
# Summary
# ============================================================================
echo ""
echo -e "${BOLD}  ╔═══════════════════════════════════════╗${NC}"
echo -e "${BOLD}  ║           ${GREEN}Installation Summary${NC}${BOLD}        ║${NC}"
echo -e "${BOLD}  ╚═══════════════════════════════════════╝${NC}"
echo ""
echo -e "  ${BOLD}Directory:${NC}     $INSTALL_DIR"
echo -e "  ${BOLD}URL:${NC}           http://localhost:${HTTP_PORT}"
echo -e "  ${BOLD}Image tag:${NC}     ${IMAGE_TAG}"
echo -e "  ${BOLD}Spoolman:${NC}      $( [[ -n "$SPOOLMAN_URL" ]] && echo "$SPOOLMAN_URL" || echo "not configured" )"
echo ""
echo -e "  Generated files:"
echo -e "    $INSTALL_DIR/docker-compose.yml"
echo -e "    $INSTALL_DIR/.env"
echo -e "    $INSTALL_DIR/nginx/nginx-proxy.conf"
echo ""

# ============================================================================
# Launch (unless --dry-run)
# ============================================================================
if [[ "$DRY_RUN" == "true" ]]; then
    info "Dry run — skipping container startup."
    echo ""
    echo -e "  To start PrintFarmer:"
    echo -e "    ${BOLD}cd $INSTALL_DIR && $COMPOSE_CMD up -d${NC}"
    echo ""
    exit 0
fi

START=true
if [[ "$NON_INTERACTIVE" != "true" ]]; then
    read -rp "$(echo -e "${BLUE}?${NC} Start PrintFarmer now? [Y/n]: ")" yn
    case "${yn,,}" in
        n|no) START=false ;;
    esac
fi

if [[ "$START" == "true" ]]; then
    info "Pulling images (this may take a few minutes on first run)..."
    cd "$INSTALL_DIR"
    $COMPOSE_CMD pull

    info "Starting PrintFarmer..."
    $COMPOSE_CMD up -d

    echo ""
    success "PrintFarmer is starting!"
    echo ""
    echo -e "  Open ${BOLD}http://localhost:${HTTP_PORT}${NC} in your browser."
    echo -e "  On first launch you'll be guided through admin account setup."
    echo ""
    echo -e "  ${BOLD}Useful commands:${NC}"
    echo -e "    $COMPOSE_CMD logs -f          # Watch logs"
    echo -e "    $COMPOSE_CMD ps               # Check status"
    echo -e "    $COMPOSE_CMD down             # Stop"
    echo -e "    $COMPOSE_CMD pull && $COMPOSE_CMD up -d  # Update"
    echo ""
else
    echo -e "  To start later:"
    echo -e "    ${BOLD}cd $INSTALL_DIR && $COMPOSE_CMD up -d${NC}"
    echo ""
fi
