# PrinterDiscovery Service Troubleshooting Guide

## Issue: "Discovery is enabled but service is not responding to heartbeats"

This error appears in the React frontend when the Network Discovery service (running in a separate container) is not sending heartbeat updates to the API backend.

**Affected Deployments**: Linux VM, cloud servers, bare metal, Raspberry Pi, and other Docker environments

### Root Cause

The printer discovery service runs as a **separate microservice in host network mode** to access the local network for printer discovery. It needs to:
1. Run in **host network mode** (for broadcast/multicast access)
2. Send periodic heartbeats to the API backend
3. The API needs to be reachable from the discovery service

**The Problem**: When deployed on a remote machine, the discovery service (in host network) cannot reach the API (in container network) because they use different networking modes.

### Solution: Cross-Network Communication

Docker provides `host.docker.internal` on macOS/Windows and `host-gateway` on Linux to bridge the gap between host network and container networks.

#### Recent Fix (Applied to docker-compose.discovery.yml)

```yaml
printer-discovery:
  network_mode: host
  extra_hosts:
    - "host.docker.internal:host-gateway"
  environment:
    - Discovery__ApiBaseUrl=http://host.docker.internal:5245
```

#### Additional Fix (Applied to docker-compose.yml - nginx-proxy healthcheck)

Fixed invalid healthcheck syntax in nginx-proxy service:
- Changed `start-period` to `start_period` (Docker Compose requires underscore, not hyphen)
- Ensures compose files are valid and services start correctly

**What this does:**
- `network_mode: host` - Runs discovery service with access to host network (needed for printer scanning)
- `extra_hosts` - Maps `host.docker.internal` to the Docker host gateway for cross-network communication
- `Discovery__ApiBaseUrl` - Points discovery service to API at `http://host.docker.internal:5245` (accessible from host network)

### Verification Steps

Run the diagnostic script to verify the setup:

```bash
./scripts/docker/verify-discovery-service.sh
```

This script checks:
1. ✅ Discovery container is running
2. ✅ Discovery service can reach API at http://host.docker.internal:5245
3. ✅ Discovery service health endpoint responds
4. ✅ API health endpoint responds
5. ✅ NetworkDiscovery LastHeartbeat is being updated

### Manual Verification

If you need to troubleshoot manually:

```bash
# 1. Check if discovery container is running
docker ps | grep printer-discovery

# 2. Test connectivity from discovery service to API
docker exec printfarmer-printer-discovery \
  wget -q -O- http://host.docker.internal:5245/healthz

# 3. Check discovery service logs
docker logs printfarmer-printer-discovery --tail 50

# 4. Verify API is responding
curl http://localhost:5245/healthz

# 5. Check if heartbeat is being recorded
curl http://localhost:5245/api/settings/NetworkDiscovery | grep lastHeartbeat
```

### Common Issues & Solutions

#### Issue 1: "Cannot resolve host.docker.internal"

**Symptom**: Discovery logs show "Could not resolve host.docker.internal"

**Solution**: 
- On Linux: Ensure you have Docker 20.10+ (added host-gateway support)
- Check compose file has `extra_hosts: - "host.docker.internal:host-gateway"`
- Restart containers: `docker-compose restart printer-discovery`

#### Issue 2: "Connection refused" from discovery to API

**Symptom**: Discovery logs show "Connection refused" or "Unreachable"

**Possible causes**:
- API container is not running: `docker ps | grep api`
- API port is not 5245: Check `docker port printfarmer-api`
- Firewall blocking localhost traffic (rare on Docker Desktop)

**Solution**:
1. Ensure API is running and healthy: `docker ps | grep api`
2. Test API health: `curl http://localhost:5245/health`
3. Check API logs: `docker logs printfarmer-api --tail 50`

#### Issue 3: Heartbeat updates but discovery not working

**Symptom**: `LastHeartbeat` is recent but discovery is still disabled

**Possible causes**:
- Discovery scan is failing (network misconfiguration)
- Discovery service is not enabled
- Discovery subnets are incorrectly configured

**Solution**:
1. Check discovery is enabled: `curl http://localhost:5245/api/settings/NetworkDiscovery | grep enableDiscovery`
2. Check logs for scan errors: `docker logs printfarmer-printer-discovery --tail 100`
3. Verify subnets: Check the DISCOVERY_SUBNETS environment variable

#### Issue 4: "host.docker.internal" works for API but not for other services

**Symptom**: Some services can reach the host, others cannot

**Solution**: All host-network containers need the `extra_hosts` mapping. Check:
```yaml
extra_hosts:
  - "host.docker.internal:host-gateway"
```

### Deployment Script Configuration

When using the deploy script (`deploy-docker.sh`), the configuration is automatically handled:

```bash
# The script creates proper environment variables:
export DISCOVERY__API_BASE_URL="http://host.docker.internal:5245"

# And adds host-gateway to Nginx proxy:
--add-host=host.docker.internal:host-gateway
```

### Environment Variables

These can be customized in `.env` file or passed to docker-compose:

| Variable | Default | Purpose |
|----------|---------|---------|
| `DISCOVERY__API_BASE_URL` | `http://host.docker.internal:5245` | API endpoint for discovery service |
| `ENABLE_PERIODIC_DISCOVERY` | `true` | Enable automatic scanning |
| `SCAN_INTERVAL_SECONDS` | `300` | Scan frequency (5 minutes) |
| `DISCOVERY_SUBNETS` | `192.168.0.0/16,10.0.0.0/8` | Network ranges to scan |
| `PROBE_TIMEOUT_MS` | `1000` | TCP probe timeout |
| `MAX_CONCURRENT_PROBES` | `50` | Concurrent connections limit |

### Understanding the Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ Host Machine (Docker Host)                                  │
│                                                             │
│  Local Network: 192.168.1.0/24                             │
│  └─ Printers, IoT devices, broadcast traffic               │
│                                                             │
│  PrinterDiscovery Service (Host Network Mode)              │
│  ├─ Can access: Local network, broadcast, printers         │
│  ├─ Cannot access: Container network directly              │
│  ├─ Accesses API via: host.docker.internal:5245 ──┐        │
│  └─ Sends heartbeats via HTTP to API               │        │
│                                                      │        │
│  ┌──────────────────────────────────────────────────┼──┐    │
│  │ Docker Container Network (Bridge Mode)           │  │    │
│  │                                                  ▼  │    │
│  │  API Backend (Bridge Network)                      │    │
│  │  ├─ Port: 5245                                     │    │
│  │  ├─ Endpoint: /api/settings/NetworkDiscovery      │    │
│  │  ├─ Receives heartbeat from discovery service     │    │
│  │  └─ Updates LastHeartbeat timestamp               │    │
│  │                                                     │    │
│  │  Frontend (Bridge Network)                         │    │
│  │  └─ Polls: GET /api/settings/NetworkDiscovery     │    │
│  │     └─ Checks if lastHeartbeat is < 60 seconds    │    │
│  │        └─ Shows discovery as "available"          │    │
│  └──────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### Files Modified

- `scripts/docker/compose-templates/docker-compose.discovery.yml`
  - Added `extra_hosts` for Linux compatibility
  - Updated `Discovery__ApiBaseUrl` to use `host.docker.internal`

- `scripts/docker/verify-discovery-service.sh`
  - New diagnostic script to verify all components

### Fix Scripts Available

Three automated scripts are available to help:

**Option 1: Simple Fix** (if you can navigate to deployment directory)
```bash
cd /path/to/deployment
./scripts/docker/fix-discovery-simple.sh
```

**Option 2: Auto-Detection Fix** (if running from repository)
```bash
./scripts/docker/fix-discovery-heartbeat.sh
```

**Option 3: Diagnostics** (check status without making changes)
```bash
./scripts/docker/verify-discovery-service.sh
```

### Manual Fix Steps

If you prefer to fix manually:

**Step 1: Navigate to Deployment**
```bash
cd /path/to/deployment  # wherever docker-compose.yml is located
```

**Step 2: Restart Discovery Service**
```bash
# Stop old service
docker-compose -f scripts/docker/compose-templates/docker-compose.yml \
                -f scripts/docker/compose-templates/docker-compose.discovery.yml \
                down printer-discovery 2>/dev/null || true

# Start new service with fixed configuration
docker-compose -f scripts/docker/compose-templates/docker-compose.yml \
                -f scripts/docker/compose-templates/docker-compose.discovery.yml \
                up -d printer-discovery
```

**Step 3: Verify It's Working**

Wait 30 seconds, then check:

```bash
# Check container is running
docker ps | grep printer-discovery

# Check logs
docker logs printfarmer-printer-discovery --tail 20

# Test connection from discovery to API
docker exec printfarmer-printer-discovery \
  wget -q -O- http://host.docker.internal:5245/healthz 2>&1 | head -1

# Check if heartbeat is recorded (should be recent)
curl -s http://localhost:5245/api/settings/NetworkDiscovery | grep lastHeartbeat
```

**Step 4: Reload React Frontend**

Refresh your web browser. Discovery should now be available! ✓

### Verify Fix is Complete

Run full diagnostic check:

```bash
# 1. Check discovery container
docker ps --format "table {{.Names}}\t{{.Status}}" | grep printer-discovery

# 2. Check discovery service health
docker exec printfarmer-printer-discovery \
  wget -q -O- http://localhost:5246/health 2>&1 | head -20

# 3. Check if API is reachable from discovery
docker exec printfarmer-printer-discovery \
  wget -q -O- http://host.docker.internal:5245/healthz 2>&1

# 4. Check last recorded heartbeat
curl -s http://localhost:5245/api/settings/NetworkDiscovery | \
  grep -o '"lastHeartbeat":"[^"]*"'

# 5. Check heartbeat age (should be < 60 seconds)
curl -s http://localhost:5245/api/settings/NetworkDiscovery | \
  python3 -c "import sys, json, datetime; \
  d=json.load(sys.stdin); \
  hb=d.get('lastHeartbeat'); \
  age=(datetime.datetime.now(datetime.timezone.utc) - datetime.datetime.fromisoformat(hb.replace('Z','+00:00'))).total_seconds(); \
  print(f'Heartbeat age: {age:.0f} seconds')"
```

If heartbeat age is < 60 seconds, everything is working! ✓

### Next Steps

1. Rebuild containers with new configuration (if needed):
   ```bash
   docker-compose -f docker-compose.yml -f docker-compose.discovery.yml up -d --build
   ```

2. Wait 30-60 seconds for discovery service to start and send first heartbeat

3. Check React frontend - discovery should now be available in Printers > Admin page

### Still Having Issues?

**Problem: "Cannot reach http://host.docker.internal:5245"**

On Linux, `host.docker.internal` needs to be mapped to the host gateway:

```yaml
extra_hosts:
  - "host.docker.internal:host-gateway"
```

Verify it's configured:
```bash
grep -A 5 "extra_hosts" scripts/docker/compose-templates/docker-compose.discovery.yml
```

**Problem: "API container not running"**

```bash
# Check API is running
docker ps | grep printfarmer-api

# If not, start API
docker-compose -f scripts/docker/compose-templates/docker-compose.yml up -d api
```

**Problem: "Permission denied - Docker socket"**

```bash
# Make sure user can access Docker
sudo usermod -aG docker $USER

# Restart Docker socket
sudo systemctl restart docker

# Re-login or source bashrc
newgrp docker
```

**Problem: "Compose files don't exist"**

The docker-compose templates need to be present:

```bash
# If you have git access
cd /path/to/deployment
git pull origin feature/orcaslicer-reimplementation

# Or manually copy from repository
cp /path/to/repo/scripts/docker/compose-templates/* scripts/docker/compose-templates/
```

### Key Configuration Files

After the fix, these should be in place:

**`scripts/docker/compose-templates/docker-compose.discovery.yml`**
```yaml
services:
  printer-discovery:
    network_mode: host
    extra_hosts:
      - "host.docker.internal:host-gateway"
    environment:
      - Discovery__ApiBaseUrl=http://host.docker.internal:5245
```

**`scripts/docker/compose-templates/docker-compose.yml`** - Should have nginx-proxy with correct port mapping:
```yaml
  nginx-proxy:
    ports:
      - "8080:80"  # External:Internal mapping
```

### Advanced Debugging

1. Enable debug logging:
   ```bash
   docker exec printfarmer-printer-discovery \
     sh -c "echo 'Logging__LogLevel__PrinterDiscoveryService=Debug' >> /etc/environment"
   ```

2. Check container networking:
   ```bash
   docker network inspect printfarmer-network
   docker inspect printfarmer-api | grep -A 20 NetworkSettings
   ```

3. Verify DNS resolution:
   ```bash
   docker exec printfarmer-printer-discovery nslookup host.docker.internal
   docker exec printfarmer-printer-discovery ping -c 1 host.docker.internal
   ```

4. Check API discovery settings endpoint directly:
   ```bash
   curl -v http://localhost:5245/api/settings/NetworkDiscovery
   ```

5. Manually test heartbeat endpoint:
   ```bash
   curl -X POST http://localhost:5245/api/settings/NetworkDiscovery/heartbeat \
        -H "Content-Type: application/json" \
        -d '{"timestamp":"'$(date -u +%Y-%m-%dT%H:%M:%SZ)'"}'
   ```

6. Validate compose file:
   ```bash
   docker-compose -f scripts/docker/compose-templates/docker-compose.yml \
                   -f scripts/docker/compose-templates/docker-compose.discovery.yml \
                   config
   ```

7. Review full logs:
   ```bash
   docker logs -f printfarmer-api --tail 100
   docker logs -f printfarmer-printer-discovery --tail 100
   ```

### References

- [Docker Documentation: host.docker.internal](https://docs.docker.com/desktop/networking/#use-host-docker-internal-to-connect-to-the-host)
- [Docker Compose: extra_hosts](https://docs.docker.com/compose/compose-file/compose-file-v3/#extra_hosts)
- PrintFarmer Discovery Service: `src/printer-discovery/`
- PrintFarmer API Settings Controller: `src/api/Controllers/UnifiedSettingsController.cs`
