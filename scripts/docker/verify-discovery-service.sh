#!/bin/bash

# Diagnostic script to verify PrinterDiscovery service connectivity
# Usage: ./verify-discovery-service.sh

set -e

echo "=== PrinterDiscovery Service Diagnostics ==="
echo ""

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Check if discovery service container exists
echo "[*] Checking for printer-discovery container..."
if docker ps -a --format '{{.Names}}' | grep -q printfarmer-printer-discovery; then
    echo -e "${GREEN}✓ Container exists${NC}"
    
    # Check if it's running
    if docker ps --format '{{.Names}}' | grep -q printfarmer-printer-discovery; then
        echo -e "${GREEN}✓ Container is running${NC}"
        
        # Get container IP info
        echo ""
        echo "[*] Container network info:"
        docker inspect printfarmer-printer-discovery -f '
        Network Mode: {{index .HostConfig.NetworkMode}}
        Status: {{.State.Status}}
        Health: {{.State.Health.Status}}
        '
    else
        echo -e "${RED}✗ Container is NOT running${NC}"
        echo "Logs:"
        docker logs printfarmer-printer-discovery --tail 20
        exit 1
    fi
else
    echo -e "${RED}✗ Container does not exist${NC}"
    echo "Make sure you've run: docker-compose -f docker-compose.yml -f docker-compose.discovery.yml up -d"
    exit 1
fi

echo ""
echo "[*] Checking API connectivity from discovery service..."

# Check if discovery service can reach API
docker exec printfarmer-printer-discovery \
    sh -c "wget -q -O- http://host.docker.internal:5245/health >/dev/null 2>&1" && \
    echo -e "${GREEN}✓ Discovery service CAN reach API at http://host.docker.internal:5245${NC}" || \
    echo -e "${RED}✗ Discovery service CANNOT reach API${NC}"

echo ""
echo "[*] Checking discovery service health endpoint..."
curl -s http://localhost:5246/health | head -20 && echo "" || \
    echo -e "${YELLOW}⚠ Warning: Could not reach http://localhost:5246/health${NC}"

echo ""
echo "[*] Checking API health endpoint..."
curl -s http://localhost:5245/healthz && echo "" || \
    echo -e "${YELLOW}⚠ Warning: Could not reach http://localhost:5245/healthz${NC}"

echo ""
echo "[*] Checking if NetworkDiscovery LastHeartbeat is being updated..."
HEARTBEAT=$(curl -s http://localhost:5245/api/settings/NetworkDiscovery | grep -o '"lastHeartbeat":"[^"]*"' | head -1)
if [ -n "$HEARTBEAT" ]; then
    echo -e "${GREEN}✓ Last heartbeat: $HEARTBEAT${NC}"
else
    echo -e "${RED}✗ No heartbeat recorded${NC}"
fi

echo ""
echo "[*] Discovery service logs (last 20 lines):"
docker logs printfarmer-printer-discovery --tail 20

echo ""
echo "=== Diagnostics Complete ==="
echo ""
echo "If heartbeats are not updating, check:"
echo "1. API container is running and healthy"
echo "2. Discovery service can reach API via http://host.docker.internal:5245"
echo "3. Discovery service logs for connection errors"
echo "4. Firewall rules allow containers to communicate"
