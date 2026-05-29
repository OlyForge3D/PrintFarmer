# Local Docker Registry Setup Guide

This guide shows how to set up a private Docker registry in your home network for building images on your development machine and deploying them on other servers.

**Port Considerations:**
- **Linux**: Port 5000 is typically available (standard Docker registry port)
- **macOS**: Port 5000 may be used by AirPlay Receiver (Monterey+), script auto-detects and uses 5001
- **Windows**: Port 5000 is typically available

## Quick Start

### 1. Setup Registry (Dev Machine)

```bash
# Run the setup script
./scripts/setup-local-registry.sh

# Or manual setup:
docker run -d --name local-registry --restart=always \
  -p 5000:5000 \
  -v ~/docker-registry-data:/var/lib/registry \
  registry:2
```

### 2. Configure Client Machines

On each machine that needs to access the registry (including your dev machine), configure Docker to allow insecure registries.

**Option A: Docker Desktop (Mac/Windows)**
1. Open Docker Desktop settings
2. Go to "Docker Engine"
3. Add to the JSON configuration:
```json
{
  "insecure-registries": ["YOUR_DEV_MACHINE_IP:5000"]
}
```
4. Click "Apply & Restart"

**Option B: Linux Docker Daemon**
Edit `/etc/docker/daemon.json`:
```json
{
  "insecure-registries": ["YOUR_DEV_MACHINE_IP:5000"]
}
```
Then restart Docker: `sudo systemctl restart docker`

### 3. Find Your Registry IP

```bash
# On your dev machine, find the IP address
hostname -I | awk '{print $1}'

# Or use:
ifconfig | grep "inet " | grep -v 127.0.0.1
```

## Usage Examples

### Build and Push OrcaSlicer Images

```bash
# Preferred: generate Dockerfile from canonical sources then build

# Generate the Dockerfile for the current scenario (writes ./Dockerfile.orcaslicer-binaries)
./scripts/docker/dockerfile-generator.sh --generate-config \
  --architecture amd64 \
  --enable-orca-worker yes \
  --out ./Dockerfile.orcaslicer-binaries

# 1. Build the optimized binary layer
docker build -f Dockerfile.orcaslicer-binaries \
  -t localhost:5000/orcaslicer-binaries:2.3.2 \
  --build-arg ORCASLICER_VERSION=2.3.2 \
  .

# 2. Push binary layer to local registry
docker push localhost:5000/orcaslicer-binaries:2.3.2

# 3. Build worker using cached binaries
docker build -f Dockerfile.orcaslicer \
  -t localhost:5000/printfarmer-orcaslicer-worker:latest \
  --build-arg ORCASLICER_VERSION=2.3.2 \
  .

# 4. Push worker to local registry
docker push localhost:5000/printfarmer-orcaslicer-worker:latest

# 5. Tag as latest for easy reference
docker tag localhost:5000/orcaslicer-binaries:2.3.2 localhost:5000/orcaslicer-binaries:latest
docker push localhost:5000/orcaslicer-binaries:latest

# Legacy note: The canonical Dockerfile files are stored under
# `scripts/docker/dockerfiles/Dockerfile.orcaslicer-binaries` and may be copied manually
# if you need to reference them directly. Using the generator is recommended.
```

### Deploy on Another Server

```bash
# On deployment server, pull the images
docker pull YOUR_DEV_IP:5000/orcaslicer-binaries:2.3.2
docker pull YOUR_DEV_IP:5000/printfarmer-orcaslicer-worker:latest

# Tag them for local use
docker tag YOUR_DEV_IP:5000/orcaslicer-binaries:2.3.2 orcaslicer-binaries:2.3.2
docker tag YOUR_DEV_IP:5000/printfarmer-orcaslicer-worker:latest printfarmer-orcaslicer-worker:latest

# Run with docker-compose (images are now local)
docker compose up orcaslicer-worker
```

## PrintFarmer Integration

### Modified Docker Compose for Registry

Create a `docker-compose.registry.yml` for registry-based deployments:

```yaml
services:
  orcaslicer-binaries:
    image: YOUR_DEV_IP:5000/orcaslicer-binaries:${ORCASLICER_VERSION:-2.3.2}
    profiles:
      - orca-binaries

  orcaslicer-worker:
    image: YOUR_DEV_IP:5000/printfarmer-orcaslicer-worker:latest
    profiles:
      - orca
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:8081
      - ConnectionStrings__Redis=redis:6379
      - Worker__StorageEndpoint=${MONO_API_ENDPOINT:-http://api:5001}
      - Worker__WorkingDirectory=/app/temp
      - Worker__OrcaSlicerPath=/usr/local/bin/orcaslicer
      - Worker__WorkerId=orcaslicer-worker-1
      - Worker__MaxConcurrentJobs=1
      - Worker__MaxMemoryMb=1024
    volumes:
      - orcaslicer_temp:/app/temp
      - gcode_storage:/app/gcode
    depends_on:
      redis:
        condition: service_healthy
      api:
        condition: service_healthy

volumes:
  orcaslicer_temp:
  gcode_storage:
```

### Development Workflow

1. **Build on Dev Machine**:
   ```bash
   ./scripts/build-orcaslicer-optimized.sh
   
   # Tag for registry
   docker tag orcaslicer-binaries:2.3.2 localhost:5000/orcaslicer-binaries:2.3.2
   docker tag printfarmer-orcaslicer-worker localhost:5000/printfarmer-orcaslicer-worker:latest
   
   # Push to registry
   docker push localhost:5000/orcaslicer-binaries:2.3.2
   docker push localhost:5000/printfarmer-orcaslicer-worker:latest
   ```

2. **Deploy on Server**:
   ```bash
   # Pull latest images
   docker pull YOUR_DEV_IP:5000/orcaslicer-binaries:2.3.2
   docker pull YOUR_DEV_IP:5000/printfarmer-orcaslicer-worker:latest
   
   # Deploy with compose
   docker compose -f docker-compose.registry.yml up -d
   ```

## Registry Management

### View Registry Contents
```bash
# List repositories
curl -s http://localhost:5000/v2/_catalog | jq

# List tags for a repository
curl -s http://localhost:5000/v2/orcaslicer-binaries/tags/list | jq

# Get image manifest
curl -s http://localhost:5000/v2/printfarmer-orcaslicer-worker/manifests/latest
```

### Clean Up Registry
```bash
# Remove unused images (requires registry with delete enabled)
curl -X DELETE http://localhost:5000/v2/old-image/manifests/DIGEST

# Or restart registry to clear everything
docker restart local-registry
```

### Registry with Authentication (Optional)

For added security, create an authenticated registry:

```bash
# Create auth directory
mkdir -p ~/docker-registry-auth

# Create password file
docker run --rm --entrypoint htpasswd httpd:2 -Bbn myuser mypassword > ~/docker-registry-auth/htpasswd

# Run registry with auth
docker run -d --name local-registry-auth --restart=always \
  -p 5000:5000 \
  -v ~/docker-registry-data:/var/lib/registry \
  -v ~/docker-registry-auth:/auth \
  -e "REGISTRY_AUTH=htpasswd" \
  -e "REGISTRY_AUTH_HTPASSWD_REALM=Registry Realm" \
  -e "REGISTRY_AUTH_HTPASSWD_PATH=/auth/htpasswd" \
  registry:2

# Login on client machines
docker login localhost:5000
```

### Troubleshooting

### Common Issues

1. **"http: server gave HTTP response to HTTPS client"**
   - Add registry to insecure-registries in Docker daemon config

2. **Connection refused**
   - Check if registry is running: `docker ps | grep registry`
   - Verify firewall allows port 5000
   - Check network connectivity: `telnet YOUR_DEV_IP 5000`

3. **Port 5000 already in use (macOS)**
   - macOS Monterey+ uses port 5000 for AirPlay Receiver
   - The setup script automatically detects this and uses port 5001
   - Or manually set a different port: `REGISTRY_PORT=5001 ./scripts/setup-local-registry.sh`
   - Linux systems typically don't have this conflict

3. **Images not found**
   - List registry contents: `curl -s http://localhost:5000/v2/_catalog`
   - Verify correct IP address and port

4. **Slow builds**
   - The binary layer optimization still applies!
   - Binary layer is cached and reused across machines

### Registry Status Check
```bash
# Check if registry is healthy
curl -f http://localhost:5000/v2/ && echo "Registry is healthy"

# View registry logs
docker logs local-registry

# Monitor registry storage
du -sh ~/docker-registry-data
```

## Benefits for PrintFarmer

1. **Fast Deployment**: Pre-built images with binary optimization
2. **Consistent Environments**: Same images across dev/staging/prod
3. **Network Efficiency**: Binary layers cached once, used everywhere
4. **Version Control**: Tagged images for rollback capability
5. **Offline Deployment**: No external registry dependencies

## Security Considerations

- Registry runs on HTTP (insecure) - suitable for home networks only
- For production, use HTTPS with proper certificates
- Consider VPN access for remote machines
- Use authentication for multi-user environments