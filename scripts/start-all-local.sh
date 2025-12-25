#!/usr/bin/env bash
# PrintFarmer - Complete Local Development Startup Script
# This script starts all services required for end-to-end local testing without Docker containers
#
# Usage: ./scripts/start-all-local.sh [options]
#
# Options:
#   --foreground/-f    Run services in foreground (blocks until Ctrl+C)
#   --config FILE      Load config from FILE (supports AUTO_ADMIN settings)
#   --no-tests         Skip running initial tests
#   --clean            Clean build artifacts before starting
#   --fresh            Terminate existing containers/apps and clean up database files before starting fresh
#   --tear-down        Stop all running services and exit (no startup)
#
# Services started:
#   1. API Backend (ASP.NET Core) - localhost:5245
#   2. Printer Discovery Service (ASP.NET Core) - localhost:5246
#   3. React Frontend (Vite) - localhost:3000
#
# Config File Example (~/.start-local.conf):
#   AUTO_ADMIN=true
#   AUTO_ADMIN_USERNAME=admin
#   AUTO_ADMIN_PASSWORD=MySecurePassword123!
#   AUTO_ADMIN_EMAIL=admin@printfarmer.local
#
# Requirements:
#   - .NET SDK 9.0.302+
#   - Node.js >=20.19

set -euo pipefail

# Load common utilities
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
if [[ ! -f "$SCRIPT_DIR/common-utils.sh" ]]; then
  echo "❌ Error: common-utils.sh not found in $SCRIPT_DIR"
  exit 1
fi
source "$SCRIPT_DIR/common-utils.sh"

# Parse command line options
FOREGROUND=0
NO_TESTS=0
CLEAN=0
FRESH=0
TEAR_DOWN=0
CONFIG_FILE=""

while [[ $# -gt 0 ]]; do
  case $1 in
    --foreground|-f)
      FOREGROUND=1
      shift
      ;;
    --config)
      CONFIG_FILE="$2"
      shift 2
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
    --tear-down)
      TEAR_DOWN=1
      shift
      ;;
    *)
      log_error "Unknown option: $1"
      ;;
  esac
done

# Configuration
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SRC_DIR="$ROOT_DIR/src"
API_DIR="$SRC_DIR/api"
DISCOVERY_DIR="$SRC_DIR/printer-discovery"
REACT_DIR="$SRC_DIR/Web/ReactApp"

API_URL=${API_URL:-http://localhost:5245}
DISCOVERY_URL=${DISCOVERY_URL:-http://localhost:5246}
REACT_URL=${REACT_URL:-http://localhost:3000}

# Auto-admin defaults
AUTO_ADMIN=${AUTO_ADMIN:-false}
AUTO_ADMIN_USERNAME=${AUTO_ADMIN_USERNAME:-admin}
AUTO_ADMIN_PASSWORD=${AUTO_ADMIN_PASSWORD:-}
AUTO_ADMIN_EMAIL=${AUTO_ADMIN_EMAIL:-admin@printfarmer.local}

# Auto-detect and load auto-admin config file (separate from main config)
# Check for config in common locations (in order of priority)
for admin_config_location in ~/.auto-admin-config ~/.config/printfarmer/auto-admin-config ./.auto-admin-config; do
  if [[ -f "$admin_config_location" ]]; then
    log_info "Found auto-admin config at $admin_config_location, loading..."
    # shellcheck disable=SC1090
    source "$admin_config_location"
    break
  fi
done

# Logging and PID management
LOG_DIR=${LOG_DIR:-"$ROOT_DIR/logs"}
PID_DIR=${PID_DIR:-"$ROOT_DIR/.pids"}
API_LOG="$LOG_DIR/api.log"
DISCOVERY_LOG="$LOG_DIR/discovery.log"
REACT_LOG="$LOG_DIR/react.log"
META_FILE="$PID_DIR/services.meta"

# Auto-detect config file if not provided
if [[ -z "$CONFIG_FILE" ]]; then
  # Check for config in common locations (in order of priority)
  for default_location in ~/.start-local-config ~/.config/printfarmer/start-local-config ./.start-local-config; do
    if [[ -f "$default_location" ]]; then
      CONFIG_FILE="$default_location"
      break
    fi
  done
fi

# Load config file if found
if [[ -n "$CONFIG_FILE" ]] && [[ -f "$CONFIG_FILE" ]]; then
  log_info "Loading config from $CONFIG_FILE..."
  source "$CONFIG_FILE"
  log_success "Config loaded"
fi

# Fresh cleanup function - terminates all existing containers and processes
fresh_cleanup() {
  log_info "Starting fresh cleanup - terminating all existing containers and processes..."
  
  # First, try to kill services from metadata file if it exists
  if [[ -f "$META_FILE" ]]; then
    # Source metadata to get PIDs
    local API_PID DISCOVERY_PID REACT_PID
    if [[ -r "$META_FILE" ]]; then
      eval "$(grep -E '^(API_PID|DISCOVERY_PID|REACT_PID)=' "$META_FILE" || true)"
    fi
    
    # Kill processes if they're still running
    if [[ -n "${API_PID:-}" ]] && kill -0 "$API_PID" 2>/dev/null; then
      log_warn "Terminating API process (PID: $API_PID)"
      kill "$API_PID" 2>/dev/null || true
    fi
    
    if [[ -n "${DISCOVERY_PID:-}" ]] && kill -0 "$DISCOVERY_PID" 2>/dev/null; then
      log_warn "Terminating Discovery service process (PID: $DISCOVERY_PID)"
      kill "$DISCOVERY_PID" 2>/dev/null || true
    fi
    
    if [[ -n "${REACT_PID:-}" ]] && kill -0 "$REACT_PID" 2>/dev/null; then
      log_warn "Terminating React dev server process (PID: $REACT_PID)"
      kill "$REACT_PID" 2>/dev/null || true
    fi
    
    sleep 1
  fi
  
  # Kill any processes on PrintFarmer ports (backup method)
  local ports=(5245 5246 3000)  # API HTTP, Discovery, and React ports
  log_info "Checking ports: ${ports[*]}"
  for port in "${ports[@]}"; do
    if is_port_in_use "$port"; then
      log_warn "Terminating processes on port $port"
      free_port "$port" || true
      sleep 1
    else
      log_info "Port $port is free"
    fi
  done
  
  # Remove any existing metadata file
  rm -f "$META_FILE" 2>/dev/null || true
  
  # Clean up database files and persistent resources
  log_info "Cleaning up database files and persistent resources..."
  
  # Clean up database files
  if [[ -f "$SRC_DIR/api/farm.db" ]]; then
    rm -f "$SRC_DIR/api/farm.db"
    log_warn "Removed main database file: farm.db"
  fi
  
  if [[ -f "$SRC_DIR/api/bin/Debug/net9.0/farm.db" ]]; then
    rm -f "$SRC_DIR/api/bin/Debug/net9.0/farm.db"
    log_warn "Removed build output database file"
  fi
  
  # Clean up test database files
  if [[ -d "$SRC_DIR/tests/_temp" ]]; then
    rm -rf "$SRC_DIR/tests/_temp"
    log_warn "Removed test temporary database files"
  fi
  
  # Clean up log files
  if [[ -d "$LOG_DIR" ]] && [[ "$(ls -A $LOG_DIR 2>/dev/null)" ]]; then
    rm -f "$LOG_DIR"/*.log 2>/dev/null || true
    log_warn "Cleared log files"
  fi
  
  # Clean up PID directory
  if [[ -d "$PID_DIR" ]] && [[ "$(ls -A $PID_DIR 2>/dev/null)" ]]; then
    rm -f "$PID_DIR"/* 2>/dev/null || true
    log_warn "Cleared PID files"
  fi
  
  # Clean up Vite cache
  if [[ -d "$REACT_DIR/node_modules/.vite" ]]; then
    rm -rf "$REACT_DIR/node_modules/.vite"
    log_warn "Cleared Vite cache"
  fi
  
  log_success "Fresh cleanup completed - ready for clean startup"
}

# Cleanup function for graceful shutdown
cleanup() {
  log_info "Shutting down services..."
  
  if [[ -f "$META_FILE" ]]; then
    source "$META_FILE" 2>/dev/null || true
    
    # Stop services
    if [[ -n "${API_PID:-}" ]] && kill -0 "$API_PID" 2>/dev/null; then
      kill "$API_PID" 2>/dev/null || true
      log_warn "Stopped API server (PID: $API_PID)"
    fi
    
    if [[ -n "${DISCOVERY_PID:-}" ]] && kill -0 "$DISCOVERY_PID" 2>/dev/null; then
      kill "$DISCOVERY_PID" 2>/dev/null || true
      log_warn "Stopped Discovery service (PID: $DISCOVERY_PID)"
    fi
    
    if [[ -n "${REACT_PID:-}" ]] && kill -0 "$REACT_PID" 2>/dev/null; then
      kill "$REACT_PID" 2>/dev/null || true
      log_warn "Stopped React dev server (PID: $REACT_PID)"
    fi
  fi
  
  # Clean up files
  rm -f "$META_FILE" 2>/dev/null || true
  
  log_success "All services stopped"
}

# Set up signal handlers
trap cleanup EXIT INT TERM

# Handle tear-down mode (stop services without starting)
if [[ $TEAR_DOWN -eq 1 ]]; then
  log_info "Tear-down mode: stopping all PrintFarmer services..."
  
  if [[ -f "$META_FILE" ]]; then
    source "$META_FILE" 2>/dev/null || true
    
    # Kill services
    if [[ -n "${API_PID:-}" ]] && kill -0 "$API_PID" 2>/dev/null; then
      log_info "Stopping API server (PID: $API_PID)..."
      kill "$API_PID" 2>/dev/null || true
      sleep 1
      log_success "API server stopped"
    fi
    
    if [[ -n "${DISCOVERY_PID:-}" ]] && kill -0 "$DISCOVERY_PID" 2>/dev/null; then
      log_info "Stopping Discovery service (PID: $DISCOVERY_PID)..."
      kill "$DISCOVERY_PID" 2>/dev/null || true
      sleep 1
      log_success "Discovery service stopped"
    fi
    
    if [[ -n "${REACT_PID:-}" ]] && kill -0 "$REACT_PID" 2>/dev/null; then
      log_info "Stopping React dev server (PID: $REACT_PID)..."
      kill "$REACT_PID" 2>/dev/null || true
      sleep 1
      log_success "React dev server stopped"
    fi
    
    rm -f "$META_FILE"
  else
    log_warn "No running services found (no PID metadata file)"
  fi
  
  # Also try to kill by port in case PIDs don't match
  log_info "Clearing ports..."
  for port in 5245 5246 3000; do
    if is_port_in_use "$port"; then
      log_info "Force-killing process on port $port..."
      free_port "$port" || true
      sleep 1
    fi
  done
  
  # Delete databases to force fresh schema creation on next startup
  log_info "Deleting databases..."
  rm -f "$API_DIR/farm.db" 2>/dev/null || true
  rm -f "$API_DIR/farm.db-shm" 2>/dev/null || true
  rm -f "$API_DIR/farm.db-wal" 2>/dev/null || true
  log_success "Databases deleted - will be recreated on next startup"
  
  # Delete React .env file so it's regenerated on next startup
  log_info "Deleting React .env file..."
  rm -f "$REACT_DIR/.env" 2>/dev/null || true
  log_success "React .env file deleted - will be regenerated on next startup"
  
  log_success "🛑 All services have been stopped"
  exit 0
fi

# Handle fresh mode (cleanup before starting)
if [[ $FRESH -eq 1 ]]; then
  fresh_cleanup
fi

# Check prerequisites
log_info "Checking prerequisites..."
require_command dotnet
require_command npm
require_command node

# Docker checks and Redis startup are intentionally omitted from this simplified local script.

# Verify .NET version
if ! dotnet --version | grep -q "^9\.0\."; then
  log_error ".NET SDK 9.0+ required. Current version: $(dotnet --version)"
fi

# Verify Node.js version
node_version=$(node --version | sed 's/v//')
if ! printf '%s\n18.0.0\n' "$node_version" | sort -V | head -1 | grep -q "^18"; then
  log_error "Node.js >=20.19 required. Current version: $node_version"
fi

log_success "Prerequisites check passed"

# Create directories
mkdir -p "$LOG_DIR" "$PID_DIR"

# Clean build artifacts if requested
if [[ $CLEAN -eq 1 ]]; then
  log_info "Cleaning build artifacts..."
  cd "$SRC_DIR"
  find . -name "bin" -o -name "obj" | xargs rm -rf 2>/dev/null || true
  if [[ -d "$REACT_DIR/dist" ]]; then
    rm -rf "$REACT_DIR/dist"
  fi
  if [[ -d "$REACT_DIR/node_modules/.vite" ]]; then
    rm -rf "$REACT_DIR/node_modules/.vite"
  fi
  log_success "Build artifacts cleaned"
fi

# Fresh cleanup - terminate all existing containers and processes
if [[ $FRESH -eq 1 ]]; then
  fresh_cleanup
fi

# Check and free ports
log_info "Checking ports..."
free_port ${API_URL##*:}
free_port ${DISCOVERY_URL##*:}
free_port ${REACT_URL##*:}

# Bootstrap dependencies if needed
log_info "Bootstrapping dependencies..."
cd "$SRC_DIR"

if [[ ! -f "$API_DIR/bin/Debug/net9.0/Farm.Web.Api.dll" ]] || [[ $CLEAN -eq 1 ]]; then
  log_info "Restoring and building .NET solution..."
  dotnet restore ./farm-web.sln
  dotnet build ./farm-web.sln -c Debug
fi

cd "$REACT_DIR"
if [[ ! -d "node_modules" ]] || [[ $CLEAN -eq 1 ]]; then
  log_info "Installing React dependencies..."
  npm install --legacy-peer-deps
fi

# Always clear Vite cache to ensure fresh dev server with latest code
log_info "Clearing Vite cache for fresh development..."
rm -rf "$REACT_DIR/node_modules/.vite" "$REACT_DIR/dist" 2>/dev/null || true

cd "$SRC_DIR"

# Redis is not started by this script (slicing integration paused)
REDIS_CONTAINER_ID=""

# Environment setup
export ASPNETCORE_ENVIRONMENT=Development
export DEPLOYMENT_MODE=monolithic
export ASPNETCORE_URLS="$API_URL"
export ALLOWED_ORIGINS="$REACT_URL"
# Allow all local network origins in development (localhost, 127.0.0.1, and any 10.0.x.x addresses)
export ALLOW_LOCAL_NETWORK="true"

# Auto-admin setup (if enabled)
if [[ "$AUTO_ADMIN" == "true" ]]; then
  if [[ -z "$AUTO_ADMIN_PASSWORD" ]]; then
    log_error "AUTO_ADMIN_PASSWORD must be set when AUTO_ADMIN=true"
  fi
  log_info "Auto-admin setup enabled for user: $AUTO_ADMIN_USERNAME"
  # Export AUTO_ADMIN variables so API can use them
  export AUTO_ADMIN="true"
  export AUTO_ADMIN_USERNAME
  export AUTO_ADMIN_PASSWORD
  export AUTO_ADMIN_EMAIL
fi

# No Redis connection string exported in simplified local script

# Start API server
log_info "Starting API server on 0.0.0.0:5245..."
cd "$SRC_DIR"
if [[ $FOREGROUND -eq 1 ]]; then
  ASPNETCORE_URLS="http://0.0.0.0:5245" ALLOW_LOCAL_NETWORK="true" dotnet run --project api/Farm.Web.Api.csproj > "$API_LOG" 2>&1 &
  API_PID=$!
else
  ASPNETCORE_URLS="http://0.0.0.0:5245" ALLOW_LOCAL_NETWORK="true" dotnet run --project api/Farm.Web.Api.csproj > "$API_LOG" 2>&1 &
  API_PID=$!
fi

# Start Printer Discovery service (requires host network for mDNS/broadcast)
log_info "Starting Printer Discovery service on 0.0.0.0:5246..."
cd "$SRC_DIR"
if [[ $FOREGROUND -eq 1 ]]; then
  ASPNETCORE_URLS="http://0.0.0.0:5246" Discovery__ApiBaseUrl="http://localhost:5245" dotnet run --project printer-discovery/PrinterDiscoveryService.csproj > "$DISCOVERY_LOG" 2>&1 &
  DISCOVERY_PID=$!
else
  ASPNETCORE_URLS="http://0.0.0.0:5246" Discovery__ApiBaseUrl="http://localhost:5245" dotnet run --project printer-discovery/PrinterDiscoveryService.csproj > "$DISCOVERY_LOG" 2>&1 &
  DISCOVERY_PID=$!
fi

# Start React dev server
log_info "Starting React dev server on 0.0.0.0:3000..."
# Set API base URL - if running on remote host, detect it from environment or hostname
# This allows the React app to find the API regardless of access method (localhost vs network IP)
# IMPORTANT: Do NOT include /api in VITE_API_BASE_URL - it should be the base server URL only
# The getApiBaseUrl() and getHubUrl() utility functions will construct the full paths
CURRENT_HOST=$(hostname -I | awk '{print $1}' || echo "127.0.0.1")
VITE_API_BASE_URL="http://${CURRENT_HOST}:5245"

# Regenerate React .env file for local development (remove Docker-generated config)
log_info "Regenerating React .env file for local development..."
cd "$REACT_DIR"
rm -f .env
cat > .env << EOF
# Local Development Configuration
# Auto-generated by start-all-local.sh

# API Configuration
# Use base URL without /api - getApiBaseUrl() adds /api, getHubUrl() uses it for hubs
VITE_API_BASE_URL=${VITE_API_BASE_URL}

# Development settings
VITE_LOCALHOST=false

# Deployment type
DEPLOYMENT_TYPE=monolithic
EOF
log_success "React .env file regenerated"
if [[ $FOREGROUND -eq 1 ]]; then
  VITE_LOCALHOST=false VITE_API_BASE_URL="$VITE_API_BASE_URL" npm run dev > "$REACT_LOG" 2>&1 &
  REACT_PID=$!
else
  VITE_LOCALHOST=false VITE_API_BASE_URL="$VITE_API_BASE_URL" npm run dev > "$REACT_LOG" 2>&1 &
  REACT_PID=$!
fi

# Save service metadata
  cat > "$META_FILE" << EOF
API_PID=$API_PID
DISCOVERY_PID=$DISCOVERY_PID
REACT_PID=$REACT_PID
API_URL=$API_URL
DISCOVERY_URL=$DISCOVERY_URL
REACT_URL=$REACT_URL
STARTED_AT=$(date)
EOF

log_success "Services starting..."

# Wait for services to be ready
log_info "Waiting for services to be ready..."

# Use common utilities for waiting
# Increase timeout to account for first-run compilation/JIT
if ! wait_for_api "$API_URL" 120 2; then
  log_error "API server failed to start. Check logs: $API_LOG"
fi

# Wait for discovery service (uses separate health check endpoint)
if ! wait_for_discovery "$DISCOVERY_URL" 90 2; then
  log_error "Discovery service failed to start. Check logs: $DISCOVERY_LOG"
fi

if ! wait_for_react "$REACT_URL" 90 2; then
  log_error "React dev server failed to start. Check logs: $REACT_LOG"
fi

# Run initial tests unless disabled
if [[ $NO_TESTS -eq 0 ]]; then
  log_info "Running initial health checks..."
  run_health_check_suite "$API_URL" "$REACT_URL"
else
  log_info "Skipping initial tests (--no-tests specified)"
fi

# Setup initial admin user if AUTO_ADMIN is enabled
if [[ "$AUTO_ADMIN" == "true" ]]; then
  log_info "Setting up initial admin user..."
  if ! create_initial_admin "$API_URL" "$AUTO_ADMIN_USERNAME" "$AUTO_ADMIN_PASSWORD" "$AUTO_ADMIN_EMAIL"; then
    log_warn "Could not create initial admin user - this may be expected if setup is not needed"
  fi
fi

# Display summary
echo
log_success "🚀 All services are ready!"
echo
echo "📊 Service URLs:"
echo "  • API Backend:             $API_URL"
echo "  • Discovery Service:       $DISCOVERY_URL"
echo "  • React Frontend:          $REACT_URL"
echo
echo "🔍 Health Checks:"
echo "  • API Health:              $API_URL/healthz"
echo "  • API Detailed:            $API_URL/health"
echo "  • Discovery Health:        $DISCOVERY_URL/health"
echo "  • API Endpoints:           $API_URL/api/printers"
echo
echo "📝 Log Files:"
echo "  • API Logs:        $API_LOG"
echo "  • Discovery Logs:  $DISCOVERY_LOG"
echo "  • React Logs:      $REACT_LOG"
echo
echo "🛠️  Development URLs:"
echo "  • Main Application: $REACT_URL"
echo "  • API Documentation: $API_URL/swagger (if enabled)"
echo

# Show auto-admin info if enabled
if [[ "$AUTO_ADMIN" == "true" ]]; then
  echo "👤 Auto-Admin Credentials:"
  echo "  • Username: $AUTO_ADMIN_USERNAME"
  echo "  • Email: $AUTO_ADMIN_EMAIL"
  echo "  • Password: (set in config)"
  echo "  • Setup Wizard: SKIPPED"
  echo
fi

if [[ $FOREGROUND -eq 1 ]]; then
  log_info "Running in foreground mode. Press Ctrl+C to stop all services."
  echo
  
  # Switch to foreground mode
  wait $API_PID $REACT_PID
else
  log_info "Running in background mode."
  echo "To stop all services, run:"
  echo "  kill $API_PID $DISCOVERY_PID $REACT_PID"
  echo
  echo "Or stop all services cleanly with:"
  echo "  ./scripts/start-all-local.sh --tear-down"
  echo
  echo "To monitor logs:"
  echo "  tail -f $API_LOG"
  echo "  tail -f $DISCOVERY_LOG"
  echo "  tail -f $REACT_LOG"
  echo
  echo "Process IDs saved to: $META_FILE"
  
  # Don't exit immediately in background mode, let the services run
  # Remove the cleanup trap since we want services to continue
  trap - EXIT
fi