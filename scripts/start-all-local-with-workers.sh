#!/usr/bin/env bash
# PrintFarmer - Complete Local Development with Distributed Slicer Workers
# This script starts ALL services including a separate OrcaSlicer worker container
#
# Usage: ./scripts/start-all-local-with-workers.sh [options]
#
# Options:
#   --foreground/-f     Run services in foreground (blocks until Ctrl+C)
#   --no-orca           Skip OrcaSlicer worker container
#   (PrusaSlicer worker support removed)
#   --no-tests          Skip running initial tests
#   --api-only          Rebuild and restart ONLY the API server (leaves everything else running)
#   --clean             Clean build artifacts and containers (preserves database data)
#   --fresh             Complete fresh start (removes containers AND data volumes)
#
# Note: --clean and --fresh are mutually exclusive. Use one or the other.
#
# Services started:
#   1. API Backend (ASP.NET Core) - localhost:5245
#   2. React Frontend (Vite) - localhost:3000
#   3. OrcaSlicer Worker Container - localhost:8081
#   5. (Deprecated) PrusaSlicer worker support has been removed
#
# Requirements:
#   - .NET SDK 9.0.302+
#   - Node.js 18+
#   - Docker (for worker containers)
#   - Docker images: printfarmer/orcaslicer-worker

set -euo pipefail

# Configuration
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SRC_DIR="$ROOT_DIR/src"
API_DIR="$SRC_DIR/api"
REACT_DIR="$SRC_DIR/Web/ReactApp"

API_URL=${API_URL:-http://localhost:5245}
REACT_URL=${REACT_URL:-http://localhost:3000}
ORCA_WORKER_URL=${ORCA_WORKER_URL:-http://localhost:8081}

# Central DB connection string for API and workers
DB_CONNECTION_STRING=${DB_CONNECTION_STRING:-"Host=localhost;Port=5432;Database=printfarmer;Username=postgres;Password=postgres"}
# NOTE: DB_CONNECTION_STRING is a shell variable for convenience. We'll export it as the
# canonical environment variable consumed by the application: ConnectionStrings__Default.
# Also set ConnectionStrings__DefaultConnection and keep provider-specific vars for
# backward compatibility.

# Logging and PID management
LOG_DIR=${LOG_DIR:-"$ROOT_DIR/logs"}
PID_DIR=${PID_DIR:-"$ROOT_DIR/.pids"}
API_LOG="$LOG_DIR/api.log"
REACT_LOG="$LOG_DIR/react.log"
ORCA_LOG="$LOG_DIR/orca-worker.log"
META_FILE="$PID_DIR/services-with-workers.meta"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Utility functions (must be defined before first use)
info() { echo -e "${BLUE}ℹ️  $*${NC}"; }
success() { echo -e "${GREEN}✅ $*${NC}"; }
warn() { echo -e "${YELLOW}⚠️  $*${NC}"; }
error() { echo -e "${RED}❌ $*${NC}"; exit 1; }

# Utility to free a TCP port if in use
check_port() {
  local port=$1
  if lsof -Pi :$port -sTCP:LISTEN -t >/dev/null 2>&1; then
    warn "Port $port is already in use. Attempting to free it..."
    lsof -ti:$port | xargs kill -9 2>/dev/null || true
    sleep 2
    if lsof -Pi :$port -sTCP:LISTEN -t >/dev/null 2>&1; then
      error "Could not free port $port. Please stop the conflicting process manually."
    fi
  fi
}

# Utility to ensure a command is available
require_cmd() {
  if ! command -v "$1" &> /dev/null; then
    error "Required command '$1' not found. Please install it first."
  fi
}

# Utility to clean up orphan/dangling Docker images
cleanup_orphan_images() {
  info "Cleaning up orphan Docker images..."
  if docker images -f "dangling=true" -q | grep -q .; then
    orphan_count=$(docker images -f "dangling=true" -q | wc -l | tr -d ' ')
    warn "Removing $orphan_count orphan/dangling Docker image(s)..."
    docker image prune -f >/dev/null 2>&1 || true
    success "Orphan Docker images cleaned"
  else
    info "No orphan Docker images found"
  fi
}

# Utility to remove PrintFarmer worker and base slicer images
cleanup_slicer_images() {
  info "Removing slicer worker and base images..."
  local images=(
    "printfarmer/orcaslicer-worker"
    "printfarmer/slicer-base"
    "slicer-base"
  )
  local removed=0
  for img in "${images[@]}"; do
    if docker image inspect "$img" >/dev/null 2>&1; then
      warn "Removing image: $img"
      docker rmi -f "$img" >/dev/null 2>&1 || true
      removed=$((removed + 1))
    fi
  done
  if [[ $removed -gt 0 ]]; then
    success "Removed $removed slicer image(s)"
  else
    info "No slicer images found to remove"
  fi
}

# --- PostgreSQL dev environment ---
if [[ -f "$ROOT_DIR/scripts/env.postgres-dev.sh" ]]; then
  source "$ROOT_DIR/scripts/env.postgres-dev.sh"
  info "Loaded PostgreSQL dev environment variables."
else
  warn "env.postgres-dev.sh not found. Using default environment."
fi


# Ensure PostgreSQL container is running before proceeding
POSTGRES_CONTAINER_NAME="printfarmer-postgres"
POSTGRES_IMAGE="postgres:15"

ensure_postgres_container() {
  info "Ensuring PostgreSQL container is running..."
  local cid
  POSTGRES_VOLUME="printfarmer-postgres-data"
  
  # Check if container exists and is running
  cid=$(docker ps -q --filter "name=^/${POSTGRES_CONTAINER_NAME}$")
  if [[ -n "$cid" ]]; then
    info "PostgreSQL container already running."
    return
  fi
  
  # Only remove volume and container if --fresh flag is set
  # (--clean preserves data volumes)
  if [[ $FRESH -eq 1 ]]; then
    # Remove existing container if it exists
    cid=$(docker ps -aq --filter "name=^/${POSTGRES_CONTAINER_NAME}$")
    if [[ -n "$cid" ]]; then
      warn "Removing container: $POSTGRES_CONTAINER_NAME (--fresh specified)"
      docker rm -f "$POSTGRES_CONTAINER_NAME" >/dev/null 2>&1 || true
    fi
    
    # Loop until the volume is deleted
    for i in {1..30}; do
      if docker volume ls -q | grep -q "^${POSTGRES_VOLUME}$"; then
        warn "Attempting to remove Postgres data volume: ${POSTGRES_VOLUME} (attempt $i)"
        docker volume rm "${POSTGRES_VOLUME}" >/dev/null 2>&1 || true
        sleep 1
      else
        success "Postgres data volume ${POSTGRES_VOLUME} deleted."
        break
      fi
      if [[ $i -eq 30 ]]; then
        error "Failed to delete Postgres data volume ${POSTGRES_VOLUME} after 30 seconds. Please remove it manually."
      fi
    done
  elif [[ $CLEAN -eq 1 ]]; then
    # For --clean, just remove the container but keep the volume
    cid=$(docker ps -aq --filter "name=^/${POSTGRES_CONTAINER_NAME}$")
    if [[ -n "$cid" ]]; then
      info "Removing PostgreSQL container for clean build (keeping data volume)..."
      docker rm -f "$POSTGRES_CONTAINER_NAME" >/dev/null 2>&1 || true
    fi
  else
    # Normal mode: just remove stopped container if exists, keep the volume
    cid=$(docker ps -aq --filter "name=^/${POSTGRES_CONTAINER_NAME}$")
    if [[ -n "$cid" ]]; then
      info "Removing stopped PostgreSQL container (keeping data volume)..."
      docker rm -f "$POSTGRES_CONTAINER_NAME" >/dev/null 2>&1 || true
    fi
  fi
  
  # Create new container
  info "Creating new PostgreSQL container..."
  POSTGRES_CONTAINER_ID=$(docker run -d --name "$POSTGRES_CONTAINER_NAME" -p 5432:5432 \
    -v ${POSTGRES_VOLUME}:/var/lib/postgresql/data \
    -e POSTGRES_DB=printfarmer \
    -e POSTGRES_USER=postgres \
    -e POSTGRES_PASSWORD=postgres \
    "$POSTGRES_IMAGE")
  
  if [[ -z "$POSTGRES_CONTAINER_ID" ]] || [[ ! "$POSTGRES_CONTAINER_ID" =~ ^[0-9a-f]{64}$ ]]; then
    error "Failed to create PostgreSQL container. Docker output: $POSTGRES_CONTAINER_ID"
  fi
  
  # Wait for readiness
  for i in {1..30}; do
    if docker exec "$POSTGRES_CONTAINER_NAME" pg_isready -U postgres >/dev/null 2>&1; then
      success "PostgreSQL container ready (ID: ${POSTGRES_CONTAINER_ID:0:12})"
      return
    fi
    if [[ $i -eq 30 ]]; then
      error "PostgreSQL container failed to become ready within 30 seconds"
    fi
    sleep 1
  done
}

# Fresh cleanup function - terminates all existing containers and processes (adapted for workers)
fresh_cleanup() {
  info "Starting fresh cleanup - terminating all existing containers and processes..."
  
  # Stop and remove PrintFarmer Docker containers by name pattern (running or stopped)
  local containers=(
    "printfarmer-orca-worker"
  )
  # Only remove stopped containers if --fresh is present
  if [[ ${FRESH:-0} -eq 1 ]]; then
    for cname in "${containers[@]}"; do
      if docker ps -a --filter "name=$cname" | grep -q .; then
        warn "Removing container: $cname (fresh start)"
        docker rm -f "$cname" >/dev/null 2>&1 || true
      fi
    done
  fi
  
  for container_name in "${containers[@]}"; do
    if docker ps -q --filter "name=$container_name" | grep -q .; then
      warn "Stopping container: $container_name"
      #docker stop "$container_name" >/dev/null 2>&1 || true
      docker rm -f "$container_name" >/dev/null 2>&1 || true
    fi
  done
  
  # Remove worker and base images to ensure fresh rebuild
  cleanup_slicer_images
  
  # Kill any processes on PrintFarmer ports
  local ports=(5000 5245 7281 3000)  # API, React ports
  for port in "${ports[@]}"; do
    if lsof -Pi :$port -sTCP:LISTEN -t >/dev/null 2>&1; then
      warn "Terminating processes on port $port"
      lsof -ti:$port | xargs kill -9 2>/dev/null || true
      sleep 1
    fi
  done
  
  # Remove any existing metadata file
  rm -f "$META_FILE" 2>/dev/null || true
  
  # Clean up database files and persistent resources
  info "Cleaning up database files and persistent resources..."
  
  # Clean up database files
  if [[ -f "$SRC_DIR/api/farm.db" ]]; then
    rm -f "$SRC_DIR/api/farm.db"
    warn "Removed main database file: farm.db"
  fi
  
  if [[ -f "$SRC_DIR/api/bin/Debug/net9.0/farm.db" ]]; then
    rm -f "$SRC_DIR/api/bin/Debug/net9.0/farm.db"
    warn "Removed build output database file"
  fi
  
  # Clean up test database files
  if [[ -d "$SRC_DIR/tests/_temp" ]]; then
    rm -rf "$SRC_DIR/tests/_temp"
    warn "Removed test temporary database files"
  fi
  
  # Clean up log files
  if [[ -d "$LOG_DIR" ]] && [[ "$(ls -A $LOG_DIR 2>/dev/null)" ]]; then
    rm -f "$LOG_DIR"/*.log 2>/dev/null || true
    warn "Cleared log files"
  fi
  
  # Clean up PID directory
  if [[ -d "$PID_DIR" ]] && [[ "$(ls -A $PID_DIR 2>/dev/null)" ]]; then
    rm -f "$PID_DIR"/* 2>/dev/null || true
    warn "Cleared PID files"
  fi
  
  # Clean up Vite cache
  if [[ -d "$REACT_DIR/node_modules/.vite" ]]; then
    rm -rf "$REACT_DIR/node_modules/.vite"
    warn "Cleared Vite cache"
  fi
  
  # Clean up orphan/dangling Docker images
  cleanup_orphan_images
  
  success "Fresh cleanup completed - ready for clean startup"
}
# Cleanup function for graceful shutdown
cleanup() {
  info "Shutting down services..."
  
  if [[ -f "$META_FILE" ]]; then
    source "$META_FILE" 2>/dev/null || true
    
    # Stop .NET services
    if [[ -n "${API_PID:-}" ]] && kill -0 "$API_PID" 2>/dev/null; then
      kill "$API_PID" 2>/dev/null || true
      warn "Stopped API server (PID: $API_PID)"
    fi
    
    if [[ -n "${REACT_PID:-}" ]] && kill -0 "$REACT_PID" 2>/dev/null; then
      kill "$REACT_PID" 2>/dev/null || true
      warn "Stopped React dev server (PID: $REACT_PID)"
    fi
    
    # Stop Docker containers
  for container_var in "ORCA_CONTAINER_ID"; do
      container_id="${!container_var:-}"
      if [[ -n "$container_id" ]] && docker ps -q --filter "id=$container_id" | grep -q .; then
        docker stop "$container_id" >/dev/null 2>&1 || true
        docker rm "$container_id" >/dev/null 2>&1 || true
        warn "Stopped container: ${container_id:0:12}"
      fi
    done
  fi
  
  # Clean up files
  rm -f "$META_FILE" 2>/dev/null || true
  
  success "All services stopped"
}

# Set up signal handlers
trap cleanup EXIT INT TERM

# Check prerequisites
info "Checking prerequisites..."
require_cmd dotnet
require_cmd npm
require_cmd node

# Parse command line options
FOREGROUND=0
NO_ORCA=0
NO_TESTS=0
CLEAN=0
FRESH=0
API_ONLY=0
BUILD_ORCA=0

while [[ $# -gt 0 ]]; do
  case $1 in
    --foreground|-f)
      FOREGROUND=1
      shift
      ;;
    --no-orca)
      NO_ORCA=1
      shift
      ;;
    --no-tests)
      NO_TESTS=1
      shift
      ;;
    --api-only)
      API_ONLY=1
      shift
      ;;
    --clean)
      if [[ $FRESH -eq 1 ]]; then
        error "--clean and --fresh are mutually exclusive. Use one or the other."
      fi
        CLEAN=1
        # Force worker image rebuild after cleaning
  BUILD_ORCA=1
        shift
        ;;

    --fresh)
      if [[ $CLEAN -eq 1 ]]; then
        error "--clean and --fresh are mutually exclusive. Use one or the other."
      fi
      FRESH=1
      shift
      ;;
    *)
      echo "Unknown option: $1"
  echo "Usage: $0 [--foreground] [--no-orca] [--no-tests] [--api-only] [--clean] [--fresh]"
      echo ""
      echo "Options:"
      echo "  --api-only  Rebuild and restart ONLY the API server (leaves everything else running)"
      echo "  --clean     Clean build artifacts and containers (keeps data volumes)"
      echo "  --fresh     Fresh start - removes everything including data volumes"
      echo "              (--clean and --fresh are mutually exclusive)"
      exit 1
      ;;
  esac
done

# Early exit for API-only mode: stop existing API, ensure Docker and Postgres, rebuild and start API only
if [[ $API_ONLY -eq 1 ]]; then
  info "API-only mode: rebuilding and restarting only the API server"
  
  # Stop any existing API process on the port
  PORT=${API_URL##*:}
  info "Stopping any process on API port $PORT..."
  if lsof -Pi :$PORT -sTCP:LISTEN -t >/dev/null 2>&1; then
    warn "Terminating existing API process on port $PORT"
    lsof -ti:$PORT | xargs kill -9 2>/dev/null || true
    sleep 2
  fi

  # Ensure Docker daemon is running (needed for PostgreSQL)
  require_cmd docker
  if ! docker info > /dev/null 2>&1; then
    warn "Docker not responsive; attempting to start..."
    if [[ "$OSTYPE" == darwin* ]]; then
      open --background -a Docker
    else
      sudo systemctl restart docker
    fi
    # Wait for Docker
    for i in {1..30}; do
      if docker info > /dev/null 2>&1; then
        success "Docker is now responsive"
        break
      fi
      sleep 2
    done
    if ! docker info > /dev/null 2>&1; then
      error "Docker could not be started"
    fi
  fi

  # Ensure Postgres container is running (for API database)
  ensure_postgres_container

  # Build and start API only
  info "Building API server..."
  cd "$SRC_DIR"
  dotnet build api/Farm.Web.Api.csproj -c Debug
  
  # Create log directory if needed
  mkdir -p "$LOG_DIR"
  
  # Start API server
  info "Starting API server at $API_URL..."
  export ASPNETCORE_ENVIRONMENT=Development
  export DEPLOYMENT_MODE=monolithic
  export ASPNETCORE_URLS="$API_URL"
  export ConnectionStrings__DefaultConnection="$DB_CONNECTION_STRING"
  export ConnectionStrings__Default="$DB_CONNECTION_STRING"
  # keep provider-specific var if env.postgres-dev.sh set it (helps older scripts)
  if [[ -n "${ConnectionStrings__Postgres:-}" ]]; then
    export ConnectionStrings__Postgres="$ConnectionStrings__Postgres"
  else
    export ConnectionStrings__Postgres="$DB_CONNECTION_STRING"
  fi
  
  dotnet run --project api/Farm.Web.Api.csproj > "$API_LOG" 2>&1 &
  API_PID=$!
  
  # Wait for API to be ready
  info "Waiting for API server to be ready..."
  for i in {1..60}; do
    if curl -s "$API_URL/healthz" > /dev/null 2>&1; then
      success "API server ready at $API_URL (PID: $API_PID)"
      break
    fi
    if [[ $i -eq 60 ]]; then
      error "API server failed to start within 60 seconds. Check logs: $API_LOG"
    fi
    sleep 1
  done
  
  # Display summary
  echo
  success "🚀 API server restarted successfully!"
  echo
  echo "📊 Service Info:"
  echo "  • API Backend:   $API_URL"
  echo "  • Process ID:    $API_PID"
  echo "  • Log File:      $API_LOG"
  echo
  echo "🔍 Health Checks:"
  echo "  • Basic Health:  $API_URL/healthz"
  echo "  • Detailed:      $API_URL/health"
  echo
  echo "To stop the API server:"
  echo "  kill $API_PID"
  echo
  
  # Exit early - don't continue with the rest of the script
  exit 0
fi

# Require Docker for full mode below
require_cmd docker

# --- Docker health check and restart logic ---
if command -v docker &> /dev/null; then
  info "Checking Docker daemon status..."
  docker_restarted=0
  if ! docker info > /dev/null 2>&1; then
    warn "Docker is installed but not responding. Attempting to restart Docker..."
    if [[ "$OSTYPE" == "darwin"* ]]; then
      open --background -a Docker
      info "Waiting for Docker Desktop to start..."
      for i in {1..30}; do
        if docker info > /dev/null 2>&1; then
          success "Docker is now responsive."
          docker_restarted=1
          break
        fi
        sleep 2
      done
    else
      sudo systemctl restart docker
      info "Waiting for Docker daemon to restart..."
      for i in {1..30}; do
        if docker info > /dev/null 2>&1; then
          success "Docker is now responsive."
          docker_restarted=1
          break
        fi
        sleep 2
      done
    fi
    if ! docker info > /dev/null 2>&1; then
      error "Docker could not be started. Please start Docker manually."
    fi
  else
    success "Docker is running."
  fi
  # Always check and start Postgres container after Docker is responsive
  POSTGRES_CONTAINER_NAME="printfarmer-postgres"
  POSTGRES_CID=$(docker ps -a -q --filter "name=^/${POSTGRES_CONTAINER_NAME}$")
  POSTGRES_RUNNING=$(docker ps -q --filter "name=^/${POSTGRES_CONTAINER_NAME}$")
  if [[ -n "$POSTGRES_CID" ]] && [[ -z "$POSTGRES_RUNNING" ]]; then
    info "Starting existing stopped/exited PostgreSQL container after Docker restart..."
    docker start "$POSTGRES_CONTAINER_NAME" >/dev/null
    success "PostgreSQL container started (ID: ${POSTGRES_CID:0:12})"
  fi

  # Remove stopped containers for PrintFarmer images if they exist but are not running
  for cname in printfarmer-orca-worker; do
    if docker ps -a --filter "name=$cname" --format '{{.Status}}' | grep -v Up | grep -q .; then
      warn "Removing stopped container: $cname"
      docker rm -f "$cname" >/dev/null 2>&1 || true
    fi
  done
fi

# Verify .NET version
if ! dotnet --version | grep -q "^9\.0\."; then
  error ".NET SDK 9.0+ required. Current version: $(dotnet --version)"
fi

# Verify Node.js version
node_version=$(node --version | sed 's/v//')
if ! printf '%s\n18.0.0\n' "$node_version" | sort -V | head -1 | grep -q "^18"; then
  error "Node.js 18+ required. Current version: $node_version"
fi

success "Prerequisites check passed"


# --- Ensure containers/images are stopped/removed BEFORE any build if --clean or --fresh ---
if [[ $FRESH -eq 1 ]]; then
  fresh_cleanup
elif [[ $CLEAN -eq 1 ]]; then
  info "Cleaning up containers and images before build (--clean specified)"
  # Stop and remove PrintFarmer Docker containers by name pattern (running or stopped)
  containers=(
    "printfarmer-orca-worker"
    "printfarmer-postgres"
  )
  for cname in "${containers[@]}"; do
    if docker ps -a --filter "name=$cname" | grep -q .; then
      warn "Removing container: $cname (--clean)"
      docker rm -f "$cname" >/dev/null 2>&1 || true
    fi
  done
  
  # Remove worker and base images to ensure clean rebuild
  cleanup_slicer_images
  
  # Clean up orphan/dangling Docker images
  cleanup_orphan_images
fi

## Ensure Postgres container is started only once, after all other containers are built
# ...existing code...

# Check for Docker images
if [[ $NO_ORCA -eq 0 ]] && ( [[ $BUILD_ORCA -eq 1 ]] || ! docker image inspect printfarmer/orcaslicer-worker >/dev/null 2>&1 ); then
  warn "OrcaSlicer worker image not found. Building it with optimized binary caching..."
  cd "$ROOT_DIR"
  
  # Build slicer-base first
  SLICER_CMD=(docker build)
  if [ -n "${DOCKER_BUILD_PLATFORM:-}" ]; then
    SLICER_CMD+=(--platform "${DOCKER_BUILD_PLATFORM}")
  fi
  SLICER_CMD+=(-f Dockerfile.slicer-base -t printfarmer/slicer-base .)
  "${SLICER_CMD[@]}"
  docker tag printfarmer/slicer-base:latest slicer-base:latest
  # Tag alternate name used by worker Dockerfiles if present
  docker tag printfarmer/slicer-base:latest printfarmer-slicer-base:latest || true
  
  # Build optimized binary layer first (cached for future builds)
  ORCA_VERSION="${ORCASLICER_VERSION:-2.3.1}"
  info "Building orcaslicer-binaries:${ORCA_VERSION} (cached binary layer)..."
  ORCA_BIN_CMD=(docker build)
  if [ -n "${DOCKER_BUILD_PLATFORM:-}" ]; then
    ORCA_BIN_CMD+=(--platform "${DOCKER_BUILD_PLATFORM}")
  fi
  # Generate Dockerfile.orcaslicer-binaries for local builds if generator exists
  if [ -x "$ROOT_DIR/scripts/docker/dockerfile-generator.sh" ]; then
    info "Generating Dockerfile.orcaslicer-binaries for local build"
    (cd "$ROOT_DIR" && ./scripts/docker/dockerfile-generator.sh --generate-config --enable-orca-worker yes --out ./Dockerfile.orcaslicer-binaries) || info "Generator failed; falling back to canonical"
    _PF_CREATED_ROOT_ORCA_DOCKERFILE=1
  fi
  ORCA_DOCKERFILE=${ORCA_DOCKERFILE:-"./scripts/docker/dockerfiles/Dockerfile.orcaslicer-binaries"}
  if [ -f "$ROOT_DIR/Dockerfile.orcaslicer-binaries" ]; then
    ORCA_DOCKERFILE="$ROOT_DIR/Dockerfile.orcaslicer-binaries"
  fi
  ORCA_BIN_CMD+=(-f "$ORCA_DOCKERFILE" \
    -t "orcaslicer-binaries:${ORCA_VERSION}" \
    -t "orcaslicer-binaries:latest" \
    --build-arg ORCASLICER_VERSION="${ORCA_VERSION}" \
    --build-arg ALLOW_STUB=false \
    .)
  "${ORCA_BIN_CMD[@]}"
  
  # Build worker using cached binaries (fast)
  info "Building orcaslicer-worker using cached binaries..."
  ORCA_WORKER_CMD=(docker build)
  if [ -n "${DOCKER_BUILD_PLATFORM:-}" ]; then
    ORCA_WORKER_CMD+=(--platform "${DOCKER_BUILD_PLATFORM}")
  fi
  ORCA_WORKER_CMD+=(-f Dockerfile.orcaslicer \
    -t printfarmer/orcaslicer-worker \
    --build-arg ORCASLICER_VERSION="${ORCA_VERSION}" \
    .)
  "${ORCA_WORKER_CMD[@]}"
fi


# Create directories
mkdir -p "$LOG_DIR" "$PID_DIR"

# Clean build artifacts if requested
if [[ $CLEAN -eq 1 ]]; then
  info "Cleaning build artifacts..."
  cd "$SRC_DIR"
  find . -name "bin" -o -name "obj" | xargs rm -rf 2>/dev/null || true
  if [[ -d "$REACT_DIR/dist" ]]; then
    rm -rf "$REACT_DIR/dist"
  fi
  if [[ -d "$REACT_DIR/node_modules/.vite" ]]; then
    rm -rf "$REACT_DIR/node_modules/.vite"
  fi
  success "Build artifacts cleaned"
fi


# Check and free ports
info "Checking ports..."
check_port ${API_URL##*:}
check_port ${REACT_URL##*:}
if [[ $NO_ORCA -eq 0 ]]; then
  check_port ${ORCA_WORKER_URL##*:}
fi

# Bootstrap dependencies if needed
info "Bootstrapping dependencies..."
cd "$SRC_DIR"

if [[ ! -f "$API_DIR/bin/Debug/net9.0/Farm.Web.Api.dll" ]] || [[ $CLEAN -eq 1 ]]; then
  info "Restoring .NET dependencies..."
  dotnet restore ./farm-web.sln
  info "Building .NET solution..."
  dotnet build ./farm-web.sln -c Debug
fi

cd "$REACT_DIR"
if [[ ! -d "node_modules" ]] || [[ $CLEAN -eq 1 ]]; then
  info "Installing React dependencies..."
  npm install --legacy-peer-deps
fi

# Ensure EF Core migrations are applied after cleaning and before starting API server


# Create/update React .env for development with distributed workers

info "Setting up React environment variables..."
cat > "$REACT_DIR/.env" <<EOF
# Auto-generated by start-all-local-with-workers.sh - DO NOT EDIT MANUALLY
# This file is created automatically for distributed worker development

# SignalR URLs for development - connect directly to API server
VITE_SIGNALR_PRINTERS_URL=$API_URL/hubs/printers
VITE_SIGNALR_HARVEST_URL=$API_URL/hubs/harvest

# API Base URL (optional, defaults to relative URLs which work via proxy)
VITE_API_BASE_URL=$API_URL

# Generated on: $(date)
EOF

cd "$SRC_DIR"

success "Dependencies ready"


# Check Docker before starting worker containers
if [[ $API_ONLY -eq 0 ]]; then
  require_cmd docker
  if command -v docker &> /dev/null; then
    info "Checking Docker daemon status before starting worker containers..."
  if ! docker info > /dev/null 2>&1; then
    warn "Docker is installed but not responding. Attempting to restart Docker..."
    if [[ "$OSTYPE" == "darwin"* ]]; then
      open --background -a Docker
      info "Waiting for Docker Desktop to start..."
      for i in {1..30}; do
        if docker info > /dev/null 2>&1; then
          success "Docker is now responsive."
          break
        fi
        sleep 2
      done
    else
      sudo systemctl restart docker
      info "Waiting for Docker daemon to restart..."
      for i in {1..30}; do
        if docker info > /dev/null 2>&1; then
          success "Docker is now responsive."
          break
        fi
        sleep 2
      done
    fi
    if ! docker info > /dev/null 2>&1; then
      error "Docker could not be started. Please start Docker manually."
    fi
  else
    success "Docker is running."
  fi
fi

# Start OrcaSlicer worker
if [[ $NO_ORCA -eq 0 ]]; then
  ORCA_CONTAINER_NAME="printfarmer-orca-worker"
  info "Ensuring OrcaSlicer worker container is running..."
  if cid=$(docker ps -q --filter "name=^/${ORCA_CONTAINER_NAME}$"); then
    if [[ -n "$cid" ]]; then
      success "OrcaSlicer worker container already running (ID: ${cid:0:12})"
      ORCA_CONTAINER_ID="$cid"
    else
      # If container exists but is stopped, start it
      cid=$(docker ps -a -q --filter "name=^/${ORCA_CONTAINER_NAME}$")
      if [[ -n "$cid" ]]; then
        info "Starting existing stopped OrcaSlicer worker container..."
        docker start "$ORCA_CONTAINER_NAME" >/dev/null
        ORCA_CONTAINER_ID="$cid"
      else
        # Create new container
        info "Creating new OrcaSlicer worker container..."
        ORCA_CONTAINER_ID=$(docker run -d --name "$ORCA_CONTAINER_NAME" \
          -p ${ORCA_WORKER_URL##*:}:8080 \
          -e Worker__StorageEndpoint="http://host.docker.internal:5245" \
          -e Worker__WorkingDirectory="/app/temp" \
          -e ASPNETCORE_URLS="http://+:8080" \
          printfarmer/orcaslicer-worker)
        if [[ -z "$ORCA_CONTAINER_ID" ]]; then
          error "Failed to start OrcaSlicer worker container"
        fi
      fi
    fi
  else
    # Create new container
    info "Creating new OrcaSlicer worker container..."
    ORCA_CONTAINER_ID=$(docker run -d --name "$ORCA_CONTAINER_NAME" \
      -p ${ORCA_WORKER_URL##*:}:8080 \
      -e Worker__StorageEndpoint="http://host.docker.internal:5245" \
      -e Worker__WorkingDirectory="/app/temp" \
      -e ASPNETCORE_URLS="http://+:8080" \
      printfarmer/orcaslicer-worker)
    if [[ -z "$ORCA_CONTAINER_ID" ]]; then
      error "Failed to start OrcaSlicer worker container"
    fi
  fi
  success "OrcaSlicer worker container ready (ID: ${ORCA_CONTAINER_ID:0:12})"
else
  ORCA_CONTAINER_ID=""
  info "Skipping OrcaSlicer worker (--no-orca specified)"
fi


fi  # End of API_ONLY check for Docker/worker startup

# Set empty container IDs if API_ONLY mode
if [[ $API_ONLY -eq 1 ]]; then
  ORCA_CONTAINER_ID=""
  info "API-only mode: skipping worker containers"
fi

# Final check: Ensure Postgres is running before starting API/React
ensure_postgres_container

# Environment setup for API with distributed slicing enabled
export ASPNETCORE_ENVIRONMENT=Development
export DEPLOYMENT_MODE=monolithic
export ASPNETCORE_URLS="$API_URL"
export ALLOWED_ORIGINS="$REACT_URL"
export ConnectionStrings__DefaultConnection="$DB_CONNECTION_STRING"
# Backwards-compatible alias: some environments expect ConnectionStrings:Default
# while others (historical) use ConnectionStrings:DefaultConnection. Export
# both to avoid provider/connection-string mismatches (observed as Npgsql key
# parsing errors when Postgres provider was selected but SQLite-style "Data
# Source=..." connection string was used).
export ConnectionStrings__Default="$DB_CONNECTION_STRING"
export ENABLE_DISTRIBUTED_SLICING=true

# Start API server
info "Starting API server with distributed slicing enabled..."
cd "$SRC_DIR"
if [[ $FOREGROUND -eq 1 ]]; then
  # In foreground mode, we'll start in background initially for testing, then switch to foreground
  dotnet run --project api/Farm.Web.Api.csproj > "$API_LOG" 2>&1 &
  API_PID=$!
else
  dotnet run --project api/Farm.Web.Api.csproj > "$API_LOG" 2>&1 &
  API_PID=$!
fi

# Start React dev server (unless --api-only)
if [[ $API_ONLY -eq 0 ]]; then
  info "Starting React dev server..."
  cd "$REACT_DIR"
  if [[ $FOREGROUND -eq 1 ]]; then
    npm run dev > "$REACT_LOG" 2>&1 &
    REACT_PID=$!
  else
    npm run dev > "$REACT_LOG" 2>&1 &
    REACT_PID=$!
  fi
else
  REACT_PID=""
  info "API-only mode: skipping React dev server"
fi

# Save service metadata
cat > "$META_FILE" << EOF
API_PID=$API_PID
REACT_PID=$REACT_PID
ORCA_CONTAINER_ID=$ORCA_CONTAINER_ID
API_URL=$API_URL
REACT_URL=$REACT_URL
ORCA_WORKER_URL=$ORCA_WORKER_URL
STARTED_AT=$(date)
EOF

success "Services starting..."

# Wait for services to be ready
info "Waiting for services to be ready..."

# Wait for API
for i in {1..60}; do
  if curl -s "$API_URL/healthz" > /dev/null 2>&1; then
    success "API server ready at $API_URL"
    break
  fi
  if [[ $i -eq 60 ]]; then
    error "API server failed to start within 60 seconds. Check logs: $API_LOG"
  fi
  sleep 1
done

# Wait for React dev server (unless --api-only)
if [[ $API_ONLY -eq 0 ]]; then
  for i in {1..60}; do
    if curl -s "$REACT_URL" > /dev/null 2>&1; then
      success "React dev server ready at $REACT_URL"
      break
    fi
    if [[ $i -eq 60 ]]; then
      error "React dev server failed to start within 60 seconds. Check logs: $REACT_LOG"
    fi
    sleep 1
  done
fi

# Wait for worker containers (unless --api-only)
if [[ $API_ONLY -eq 0 && $NO_ORCA -eq 0 ]]; then
  for i in {1..60}; do
    if curl -s "$ORCA_WORKER_URL/healthz" > /dev/null 2>&1; then
      success "OrcaSlicer worker ready at $ORCA_WORKER_URL"
      break
    fi
    if [[ $i -eq 60 ]]; then
      warn "OrcaSlicer worker failed to start within 60 seconds. Check container: docker logs $ORCA_CONTAINER_ID"
    fi
    sleep 1
  done
fi


# Run initial tests unless disabled
if [[ $NO_TESTS -eq 0 ]]; then
  info "Running initial health checks..."

  # Test API endpoints (comprehensive health)
  health_json=$(curl -s "$API_URL/health")
  health_status=$(echo "$health_json" | grep -o '"status":"[^"]*"' | head -1 | cut -d '"' -f4)
  if [[ "$health_status" == "Healthy" ]]; then
    success "API health check passed (comprehensive)"
  else
    warn "API health check failed (comprehensive): status=$health_status"
    echo "Full health check result (pretty-printed):"
    if command -v jq >/dev/null 2>&1; then
      echo "$health_json" | jq
    else
      echo "$health_json"
    fi

    # Provide additional diagnostics to surface root causes quickly
    echo
    warn "Dumping recent API logs to help diagnose the health failure (tail 200 lines):"
    if [[ -f "$API_LOG" ]]; then
      tail -n 200 "$API_LOG" || true
    else
      echo "(API log file not found: $API_LOG)"
    fi

    # If the worker containers exist, include their recent logs as well
    if [[ -n "${ORCA_CONTAINER_ID:-}" ]]; then
      warn "Recent Orca worker logs (docker logs --tail 200):"
      docker logs --tail 200 "$ORCA_CONTAINER_ID" || true
    fi
    exit 1
  fi
  
  # Test React
  if curl -s "$REACT_URL" | grep -q -i "printfarmer\|vite"; then
    success "React dev server serving content"
  else
    warn "React dev server not serving expected content"
  fi
  
  # Test worker health endpoints
  if [[ $NO_ORCA -eq 0 ]]; then
    if curl -s "$ORCA_WORKER_URL/healthz" | grep -q '"status":"ok"'; then
      success "OrcaSlicer worker health check passed"
    else
      warn "OrcaSlicer worker health check failed"
    fi
  fi
  
else
  info "Skipping initial tests (--no-tests specified)"
fi

# Display summary
echo
success "🚀 All services are ready for FULL DISTRIBUTED SLICING!"
echo
echo "📊 Service URLs:"
echo "  • API Backend:      $API_URL"
echo "  • React Frontend:   $REACT_URL"
if [[ -n "${DB_PROVIDER:-}" ]]; then
  echo "  • Data Backend:      $DB_PROVIDER"
else
  echo "  • Data Backend:      (not set)"
fi
if [[ $NO_ORCA -eq 0 ]]; then
echo "  • OrcaSlicer Worker: $ORCA_WORKER_URL"
fi
echo
echo "🔍 Health Checks:"
echo "  • API Health:       $API_URL/healthz"
echo "  • API Detailed:     $API_URL/health"
if [[ $NO_ORCA -eq 0 ]]; then
echo "  • Orca Worker:      $ORCA_WORKER_URL/healthz"
fi
echo
echo "📝 Log Files:"
echo "  • API Logs:         $API_LOG"
echo "  • React Logs:       $REACT_LOG"
if [[ $NO_ORCA -eq 0 ]]; then
echo "  • Orca Worker:      docker logs $ORCA_CONTAINER_ID"
fi
echo
echo "🛠️  Development URLs:"
echo "  • Main Application: $REACT_URL"
echo "  • API Documentation: $API_URL/swagger (if enabled)"
echo
echo "⚙️  Distributed Slicing:"
echo "  • Status: ENABLED"
echo "  • Queue Stats: Available via API endpoints"
echo "  • Real-time Updates: Via SignalR at $API_URL/hubs/printers"
echo

if [[ $FOREGROUND -eq 1 ]]; then
  info "Running in foreground mode. Press Ctrl+C to stop all services."
  echo
  
  # Switch to foreground mode
  wait $API_PID $REACT_PID
else
  info "Running in background mode."
  echo "To stop all services, run:"
  echo "  kill $API_PID $REACT_PID"
  if [[ $NO_ORCA -eq 0 ]]; then
    echo "  docker stop $ORCA_CONTAINER_ID"
  fi
  echo
  echo "Process IDs and container IDs saved to: $META_FILE"
  
  # Don't exit immediately in background mode, let the services run
  # Remove the cleanup trap since we want services to continue
  trap - EXIT
fi