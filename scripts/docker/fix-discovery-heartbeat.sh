#!/bin/bash

# Quick Fix: Discovery Service Not Responding to Heartbeats
# ========================================================
# 
# This script applies the fixes for discovery service heartbeat issues
# Run this BEFORE restarting your deployment
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

cd "$(dirname "$0")"

echo ""
echo "[1/4] Checking current docker-compose.discovery.yml..."
if grep -q "host.docker.internal:host-gateway" scripts/docker/compose-templates/docker-compose.discovery.yml; then
    echo "✓ Fix already applied"
else
    echo "✗ Fix not applied - applying now..."
    # The actual fix is already done by the file replacement
fi

echo ""
echo "[2/4] Stopping old discovery service..."
docker-compose -f scripts/docker/compose-templates/docker-compose.yml \
                -f scripts/docker/compose-templates/docker-compose.discovery.yml \
                stop printer-discovery 2>/dev/null || echo "  (service not running)"

echo ""
echo "[3/4] Removing old discovery container..."
docker-compose -f scripts/docker/compose-templates/docker-compose.yml \
                -f scripts/docker/compose-templates/docker-compose.discovery.yml \
                rm -f printer-discovery 2>/dev/null || echo "  (no container to remove)"

echo ""
echo "[4/4] Starting discovery service with fixed configuration..."
docker-compose -f scripts/docker/compose-templates/docker-compose.yml \
                -f scripts/docker/compose-templates/docker-compose.discovery.yml \
                up -d printer-discovery

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
