#!/bin/bash

# debug-deployment.sh - Debug PrintFarmer deployment issues on remote machine

echo "=================================="
echo "PrintFarmer Deployment Debug Info"
echo "=================================="
echo "Date: $(date)"
echo "Machine: $(hostname)"
echo "Architecture: $(uname -m)"
echo

# Check Docker
echo "=== Docker Status ==="
docker --version
docker compose version
echo "Docker daemon running: $(systemctl is-active docker || echo 'unknown')"
echo

# Check deployment directory
echo "=== Deployment Files ==="
if [ -f "docker-compose.yml" ]; then
    echo "✅ docker-compose.yml exists"
    echo "Services in compose file:"
    grep -E "^  [a-zA-Z][^:]*:" docker-compose.yml
    echo
else
    echo "❌ docker-compose.yml missing"
fi

if [ -f ".env" ]; then
    echo "✅ .env file exists"
    echo "Size: $(wc -l < .env) lines"
else
    echo "❌ .env file missing"
fi

echo "Dockerfiles present:"
ls -la Dockerfile* 2>/dev/null || echo "No Dockerfiles found"
echo

# Check running containers
echo "=== Container Status ==="
docker ps -a --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}\t{{.Image}}"
echo

# Check container logs for failed services
echo "=== Container Logs (last 20 lines each) ==="
for container in $(docker ps -a --format "{{.Names}}" | grep printfarmer); do
    echo "--- $container ---"
    docker logs --tail 20 "$container" 2>&1 | head -20
    echo
done

# Check system resources
echo "=== System Resources ==="
echo "Memory:"
free -h
echo "Disk space:"
df -h /
echo "CPU info:"
nproc
echo

# Check network
echo "=== Network Connectivity ==="
echo "Can reach Docker Hub:"
ping -c 2 index.docker.io > /dev/null 2>&1 && echo "✅ Docker Hub reachable" || echo "❌ Docker Hub unreachable"
echo "Can reach Microsoft Container Registry:"
ping -c 2 mcr.microsoft.com > /dev/null 2>&1 && echo "✅ MCR reachable" || echo "❌ MCR unreachable"
echo

# Check Docker daemon logs
echo "=== Recent Docker Daemon Logs ==="
journalctl -u docker --since "10 minutes ago" --no-pager | tail -10 2>/dev/null || echo "Cannot access Docker daemon logs"
echo

# Try to run a simple container test
echo "=== Docker Functionality Test ==="
echo "Testing basic Docker functionality..."
if docker run --rm hello-world > /dev/null 2>&1; then
    echo "✅ Docker can run basic containers"
else
    echo "❌ Docker cannot run basic containers"
    echo "Error details:"
    docker run --rm hello-world 2>&1 | tail -5
fi
echo

echo "=== Compose File Validation ==="
if [ -f "docker-compose.yml" ]; then
    echo "Validating compose file..."
    if docker compose config > /dev/null 2>&1; then
        echo "✅ Compose file is valid"
    else
        echo "❌ Compose file has errors:"
        docker compose config 2>&1 | head -10
    fi
else
    echo "❌ No compose file to validate"
fi

echo
echo "=== Debug Complete ==="
echo "Please share this output for troubleshooting."