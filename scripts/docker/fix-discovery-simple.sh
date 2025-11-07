#!/bin/bash

# Simple fix for PrinterDiscovery heartbeat issues
# Run from your deployment directory where docker-compose.yml is located
#
# This manually applies the discovery service fix without relying on complex path detection
# Works on: Linux VM, cloud servers, bare metal, Raspberry Pi, Docker Desktop, etc.

echo "=== PrinterDiscovery Heartbeat Fix ==="
echo ""

# Check if we're in the right directory
if [ ! -f "docker-compose.yml" ]; then
    echo "Error: docker-compose.yml not found in current directory"
    echo "Please change to your deployment directory and try again"
    echo ""
    echo "Example: cd /path/to/deployment"
    exit 1
fi

DEPLOYMENT_DIR="$(pwd)"
echo "Deployment directory: $DEPLOYMENT_DIR"
echo ""

# Verify compose files exist
if [ ! -f "scripts/docker/compose-templates/docker-compose.yml" ]; then
    echo "Error: scripts/docker/compose-templates/docker-compose.yml not found"
    echo "Make sure you're in the correct deployment directory"
    exit 1
fi

if [ ! -f "scripts/docker/compose-templates/docker-compose.discovery.yml" ]; then
    echo "Error: scripts/docker/compose-templates/docker-compose.discovery.yml not found"
    echo "Make sure you're in the correct deployment directory"
    exit 1
fi

echo "[1/5] Checking current configuration..."
if grep -q "host.docker.internal:host-gateway" scripts/docker/compose-templates/docker-compose.discovery.yml; then
    echo "✓ Configuration already has the fix"
else
    echo "⚠ Configuration may need updating"
fi

echo ""
echo "[2/5] Stopping old printer-discovery service..."
docker stop printfarmer-printer-discovery 2>/dev/null || echo "  (service not running)"

echo ""
echo "[3/5] Removing old printer-discovery container..."
docker rm -f printfarmer-printer-discovery 2>/dev/null || echo "  (no container to remove)"

echo ""
echo "[4/5] Starting printer-discovery service..."
docker-compose -f scripts/docker/compose-templates/docker-compose.yml \
                -f scripts/docker/compose-templates/docker-compose.discovery.yml \
                up -d --no-deps --build printer-discovery

echo ""
echo "[5/5] Waiting for service to initialize (30 seconds)..."
sleep 10

if docker ps | grep -q printfarmer-printer-discovery; then
    echo "✓ Printer-discovery container started"
    sleep 20
else
    echo "✗ Failed to start printer-discovery container!"
    echo ""
    echo "Container logs:"
    docker logs printfarmer-printer-discovery --tail 30
    exit 1
fi

echo ""
echo "=== Verification ==="
echo ""

# Check if API can be reached
echo -n "Testing API connectivity... "
if docker exec printfarmer-printer-discovery \
    wget -q -O- http://host.docker.internal:5245/healthz >/dev/null 2>&1; then
    echo "✓ OK"
else
    echo "✗ FAILED"
    echo ""
    echo "The discovery service cannot reach the API. Check:"
    echo "1. Is the API container running? docker ps | grep api"
    echo "2. Is the API healthy? curl http://localhost:5245/health"
    echo "3. Check discovery logs: docker logs printfarmer-printer-discovery --tail 50"
fi

echo ""
echo -n "Checking if heartbeat is recorded... "
HEARTBEAT=$(curl -s http://localhost:5245/api/settings/NetworkDiscovery 2>/dev/null | grep -o '"lastHeartbeat":"[^"]*"')
if [ -n "$HEARTBEAT" ]; then
    echo "✓ YES"
    echo "  Heartbeat: $HEARTBEAT"
    echo ""
    echo "=== SUCCESS ==="
    echo "The discovery service is now responding to heartbeats!"
    echo "Refresh your web browser to see discovery available."
else
    echo "⚠ NOT YET"
    echo ""
    echo "The heartbeat may take another 30 seconds to be recorded."
    echo "Run this command to check again:"
    echo ""
    echo "  curl http://localhost:5245/api/settings/NetworkDiscovery | grep lastHeartbeat"
fi

echo ""
echo "=== Service Details ==="
echo ""
echo "Network Mode: host (required for printer discovery)"
echo "API Connection: http://host.docker.internal:5245"
echo "Discovery Port: 5246"
echo "Heartbeat Interval: 30 seconds"
echo ""
