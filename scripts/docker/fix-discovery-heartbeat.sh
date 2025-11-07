#!/bin/bash

# Discovery Service Heartbeat Fix
# ========================================================
# 
# Fixes: "Discovery is enabled but service is not responding to heartbeats"
# Can be run from anywhere in the PrintFarmer repository or deployment
# 
# Works on: Linux VM, cloud servers, bare metal, Raspberry Pi, Docker Desktop, etc.
#

set -e

echo "=== PrinterDiscovery Service Fix ==="
echo ""
echo "This script will:"
echo "1. Update docker-compose.discovery.yml with cross-network communication fix"
echo "2. Rebuild and restart the discovery service"
echo ""
read -p "Continue? (y/n) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 1
fi

# Find the repository root by looking for docker-compose.yml
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$SCRIPT_DIR"

# Walk up directories looking for docker-compose.yml or farm-web.sln
while [ "$REPO_ROOT" != "/" ]; do
    if [ -f "$REPO_ROOT/docker-compose.yml" ] || [ -f "$REPO_ROOT/farm-web.sln" ]; then
        break
    fi
    REPO_ROOT="$(dirname "$REPO_ROOT")"
done

# Final check
if [ ! -f "$REPO_ROOT/docker-compose.yml" ]; then
    # Could not auto-detect location
    echo "Error: Could not find repository root"
    echo "Expected to find docker-compose.yml starting from: $SCRIPT_DIR"
    echo ""
    echo "Try running from your deployment directory:"
    echo "  cd /path/to/deployment"
    echo "  $0"
    exit 1
fi

echo "Repository root: $REPO_ROOT"
cd "$REPO_ROOT"

COMPOSE_DIR="$REPO_ROOT/scripts/docker/compose-templates"
if [ ! -d "$COMPOSE_DIR" ]; then
    echo "Error: Could not find compose templates directory at $COMPOSE_DIR"
    echo "Directory contents of $REPO_ROOT:"
    ls -la "$REPO_ROOT" | head -20
    exit 1
fi

echo ""
echo "[1/4] Checking current docker-compose.discovery.yml..."
if grep -q "host.docker.internal:host-gateway" "$COMPOSE_DIR/docker-compose.discovery.yml"; then
    echo "✓ Fix already applied"
else
    echo "⚠ Fix not yet applied (file may not exist or be outdated)"
fi

echo ""
echo "[2/4] Stopping old discovery service..."
# Stop just the discovery container directly (simplest approach)
docker stop printfarmer-printer-discovery 2>/dev/null || echo "  (service not running)"

echo ""
echo "[3/4] Removing old discovery container..."
docker rm -f printfarmer-printer-discovery 2>/dev/null || echo "  (no container to remove)"

echo ""
echo "[4/4] Starting discovery service with fixed configuration..."
# Use full docker-compose from repo root
cd "$REPO_ROOT"
# Use --no-deps to skip rebuilding dependencies, and --build to rebuild discovery image
docker-compose -f "scripts/docker/compose-templates/docker-compose.yml" \
                -f "scripts/docker/compose-templates/docker-compose.discovery.yml" \
                up -d --no-deps --build printer-discovery

echo ""
echo "=== Waiting for discovery service to initialize ==="
echo "This typically takes 30-60 seconds..."
sleep 10

echo ""
echo "=== Verifying fix ==="

# Check if container is running
if docker ps | grep -q printfarmer-printer-discovery; then
    echo "✓ Discovery service container is running"
else
    echo "✗ Discovery service failed to start!"
    echo ""
    echo "Logs:"
    docker logs printfarmer-printer-discovery --tail 30
    exit 1
fi

# Give it time to send first heartbeat
echo "Waiting 30 seconds for first heartbeat..."
sleep 30

# Check if API is reachable
echo ""
echo "Testing API connectivity..."
if docker exec printfarmer-printer-discovery \
    wget -q -O- http://host.docker.internal:5245/healthz >/dev/null 2>&1; then
    echo "✓ Discovery service CAN reach API"
else
    echo "✗ Discovery service CANNOT reach API"
    echo ""
    echo "Troubleshooting:"
    echo "1. Check API is running: docker ps | grep api"
    echo "2. Check API health: curl http://localhost:5245/health"
    echo "3. Check discovery logs: docker logs printfarmer-printer-discovery --tail 50"
fi

echo ""
echo "Checking if heartbeat is being recorded..."
HEARTBEAT=$(curl -s http://localhost:5245/api/settings/NetworkDiscovery 2>/dev/null | grep -o '"lastHeartbeat":"[^"]*"' | head -1)
if [ -n "$HEARTBEAT" ]; then
    echo "✓ Heartbeat recorded: $HEARTBEAT"
    echo ""
    echo "=== SUCCESS ==="
    echo "The discovery service is now responding to heartbeats!"
    echo "You can view the status in: Printers > Admin > Discovery"
else
    echo "⚠ Heartbeat not yet recorded (may take another 30 seconds)"
    echo "Run this command to check again:"
    echo "  curl http://localhost:5245/api/settings/NetworkDiscovery | grep lastHeartbeat"
fi

echo ""
echo "For full diagnostics, run:"
echo "  ./scripts/docker/verify-discovery-service.sh"
