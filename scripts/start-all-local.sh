#!/usr/bin/env bash
# PrintFarmer - Complete Local Development Startup Script
# This script starts all services required for end-to-end local testing without Docker containers
#
# Usage: ./scripts/start-all-local.sh [options]
#
# Options:
#   --foreground/-f    Run services in foreground (blocks until Ctrl+C)
#   --with-redis       Start Redis container for distributed slicing (optional)
#   --no-tests         Skip running initial tests
#   --clean            Clean build artifacts before starting
#   --fresh            Terminate all existing containers/apps and clean up database files before starting fresh
#
# Services started:
#   1. API Backend (ASP.NET Core) - localhost:5245
#   2. React Frontend (Vite) - localhost:3000
#   3. Redis (optional, via Docker) - localhost:6379 (for distributed slicing only)
#
# Requirements:
#   - .NET SDK 9.0.302+
#   - Node.js 18+
#   - Docker (only if --with-redis flag used)

set -euo pipefail

# Configuration
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SRC_DIR="$ROOT_DIR/src"
API_DIR="$SRC_DIR/api"
REACT_DIR="$SRC_DIR/Web/ReactApp"

API_URL=${API_URL:-http://localhost:5245}
REACT_URL=${REACT_URL:-http://localhost:3000}
REDIS_URL=${REDIS_URL:-localhost:6379}

# Logging and PID management
LOG_DIR=${LOG_DIR:-"$ROOT_DIR/logs"}
PID_DIR=${PID_DIR:-"$ROOT_DIR/.pids"}
API_LOG="$LOG_DIR/api.log"
REACT_LOG="$LOG_DIR/react.log"
REDIS_LOG="$LOG_DIR/redis.log"
META_FILE="$PID_DIR/services.meta"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Parse command line options
FOREGROUND=0
WITH_REDIS=0
NO_TESTS=0
CLEAN=0
FRESH=0

while [[ $# -gt 0 ]]; do
  case $1 in
    --foreground|-f)
      FOREGROUND=1
      shift
      ;;
    --with-redis)
      WITH_REDIS=1
      shift
      ;;
    --no-tests)
      NO_TESTS=1
      shift
      ;;
    --clean)
      CLEAN=1
      shift
      ;;
    --fresh)
      FRESH=1
      shift
      ;;
    *)
      echo "Unknown option: $1"
      exit 1
      ;;
  esac
done

# Utility functions
info() { echo -e "${BLUE}ℹ️  $*${NC}"; }
success() { echo -e "${GREEN}✅ $*${NC}"; }
warn() { echo -e "${YELLOW}⚠️  $*${NC}"; }
error() { echo -e "${RED}❌ $*${NC}"; exit 1; }

require_cmd() {
  if ! command -v "$1" &> /dev/null; then
    error "Required command '$1' not found. Please install it first."
  fi
}

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

# Fresh cleanup function - terminates all existing containers and processes
fresh_cleanup() {
  info "Starting fresh cleanup - terminating all existing containers and processes..."
  
  # Stop and remove Redis container if it exists
  if docker ps -q --filter "name=printfarmer-redis-distributed" | grep -q .; then
    warn "Stopping Redis container: printfarmer-redis-distributed"
    docker stop "printfarmer-redis-distributed" >/dev/null 2>&1 || true
    docker rm "printfarmer-redis-distributed" >/dev/null 2>&1 || true
  fi
  
  # Kill any processes on PrintFarmer ports
  local ports=(5000 5245 7281 3000 6379)  # API, React, Redis ports
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
  
  success "Fresh cleanup completed - ready for clean startup"
}

# Cleanup function for graceful shutdown
cleanup() {
  info "Shutting down services..."
  
  if [[ -f "$META_FILE" ]]; then
    source "$META_FILE" 2>/dev/null || true
    
    # Stop services
    if [[ -n "${API_PID:-}" ]] && kill -0 "$API_PID" 2>/dev/null; then
      kill "$API_PID" 2>/dev/null || true
      warn "Stopped API server (PID: $API_PID)"
    fi
    
    if [[ -n "${REACT_PID:-}" ]] && kill -0 "$REACT_PID" 2>/dev/null; then
      kill "$REACT_PID" 2>/dev/null || true
      warn "Stopped React dev server (PID: $REACT_PID)"
    fi
    
    if [[ -n "${REDIS_CONTAINER_ID:-}" ]] && docker ps -q --filter "id=$REDIS_CONTAINER_ID" | grep -q .; then
      docker stop "$REDIS_CONTAINER_ID" >/dev/null 2>&1 || true
      docker rm "$REDIS_CONTAINER_ID" >/dev/null 2>&1 || true
      warn "Stopped Redis container"
    fi
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
if [[ $WITH_REDIS -eq 1 ]]; then
  require_cmd docker
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

# Fresh cleanup - terminate all existing containers and processes
if [[ $FRESH -eq 1 ]]; then
  fresh_cleanup
fi

# Check and free ports
info "Checking ports..."
check_port ${API_URL##*:}
check_port ${REACT_URL##*:}
if [[ $WITH_REDIS -eq 1 ]]; then
  check_port ${REDIS_URL##*:}
fi

# Bootstrap dependencies if needed
info "Bootstrapping dependencies..."
cd "$SRC_DIR"

if [[ ! -f "$API_DIR/bin/Debug/net9.0/Farm.Web.Api.dll" ]] || [[ $CLEAN -eq 1 ]]; then
  info "Restoring and building .NET solution..."
  dotnet restore ./farm-web.sln
  dotnet build ./farm-web.sln -c Debug
fi

cd "$REACT_DIR"
if [[ ! -d "node_modules" ]] || [[ $CLEAN -eq 1 ]]; then
  info "Installing React dependencies..."
  npm install
fi
cd "$SRC_DIR"

success "Dependencies ready"

# Start Redis if requested
if [[ $WITH_REDIS -eq 1 ]]; then
  info "Starting Redis container..."
  REDIS_CONTAINER_ID=$(docker run -d --name printfarmer-redis-local -p 6379:6379 redis:7-alpine redis-server --appendonly yes)
  if [[ -z "$REDIS_CONTAINER_ID" ]]; then
    error "Failed to start Redis container"
  fi
  
  # Wait for Redis to be ready
  for i in {1..30}; do
    if docker exec "$REDIS_CONTAINER_ID" redis-cli ping >/dev/null 2>&1; then
      success "Redis container started (ID: ${REDIS_CONTAINER_ID:0:12})"
      break
    fi
    if [[ $i -eq 30 ]]; then
      error "Redis container failed to start within 30 seconds"
    fi
    sleep 1
  done
else
  REDIS_CONTAINER_ID=""
  info "Skipping Redis (distributed slicing will use local embedded worker only)"
fi

# Environment setup
export ASPNETCORE_ENVIRONMENT=Development
export DEPLOYMENT_MODE=monolithic
export ASPNETCORE_URLS="$API_URL"
export ALLOWED_ORIGINS="$REACT_URL"

if [[ $WITH_REDIS -eq 1 ]]; then
  export ConnectionStrings__Redis="$REDIS_URL"
fi

# Start API server
info "Starting API server..."
cd "$SRC_DIR"
if [[ $FOREGROUND -eq 1 ]]; then
  # In foreground mode, we'll start in background initially for testing, then switch to foreground
  dotnet run --project api/Farm.Web.Api.csproj > "$API_LOG" 2>&1 &
  API_PID=$!
else
  dotnet run --project api/Farm.Web.Api.csproj > "$API_LOG" 2>&1 &
  API_PID=$!
fi

# Start React dev server
info "Starting React dev server..."
cd "$REACT_DIR"
if [[ $FOREGROUND -eq 1 ]]; then
  npm run dev > "$REACT_LOG" 2>&1 &
  REACT_PID=$!
else
  npm run dev > "$REACT_LOG" 2>&1 &
  REACT_PID=$!
fi

# Save service metadata
cat > "$META_FILE" << EOF
API_PID=$API_PID
REACT_PID=$REACT_PID
REDIS_CONTAINER_ID=$REDIS_CONTAINER_ID
API_URL=$API_URL
REACT_URL=$REACT_URL
REDIS_URL=$REDIS_URL
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

# Wait for React dev server
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

# Test Redis connection if enabled
if [[ $WITH_REDIS -eq 1 ]]; then
  if docker exec "$REDIS_CONTAINER_ID" redis-cli ping >/dev/null 2>&1; then
    success "Redis ready at $REDIS_URL"
  else
    warn "Redis container running but not responding to ping"
  fi
fi

# Run initial tests unless disabled
if [[ $NO_TESTS -eq 0 ]]; then
  info "Running initial health checks..."
  
  # Test API endpoints
  if curl -s "$API_URL/healthz" | grep -q '"status":"ok"'; then
    success "API health check passed"
  else
    warn "API health check failed"
  fi
  
  if curl -s "$API_URL/api/printers" | grep -q '\[\]'; then
    success "API printers endpoint working"
  else
    warn "API printers endpoint returned unexpected response"
  fi
  
  # Test React
  if curl -s "$REACT_URL" | grep -q -i "printfarmer\|vite"; then
    success "React dev server serving content"
  else
    warn "React dev server not serving expected content"
  fi
else
  info "Skipping initial tests (--no-tests specified)"
fi

# Display summary
echo
success "🚀 All services are ready!"
echo
echo "📊 Service URLs:"
echo "  • API Backend:     $API_URL"
echo "  • React Frontend:  $REACT_URL"
if [[ $WITH_REDIS -eq 1 ]]; then
echo "  • Redis:           $REDIS_URL"
fi
echo
echo "🔍 Health Checks:"
echo "  • API Health:      $API_URL/healthz"
echo "  • API Detailed:    $API_URL/health"
echo "  • API Endpoints:   $API_URL/api/printers"
echo
echo "📝 Log Files:"
echo "  • API Logs:        $API_LOG"
echo "  • React Logs:      $REACT_LOG"
if [[ $WITH_REDIS -eq 1 ]]; then
echo "  • Redis Logs:      docker logs $REDIS_CONTAINER_ID"
fi
echo
echo "🛠️  Development URLs:"
echo "  • Main Application: $REACT_URL"
echo "  • API Documentation: $API_URL/swagger (if enabled)"
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
  if [[ $WITH_REDIS -eq 1 ]]; then
    echo "  docker stop $REDIS_CONTAINER_ID"
  fi
  echo
  echo "Or simply run this script again with Ctrl+C to use the cleanup handler."
  echo
  echo "To monitor logs:"
  echo "  tail -f $API_LOG"
  echo "  tail -f $REACT_LOG"
  echo
  echo "Process IDs saved to: $META_FILE"
  
  # Don't exit immediately in background mode, let the services run
  # Remove the cleanup trap since we want services to continue
  trap - EXIT
fi