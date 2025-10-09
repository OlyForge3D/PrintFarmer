# Host Network Mode Deployment Guide

**Last Updated:** October 6, 2025  
**Status:** ✅ Fully Implemented in deployment script

---

## Overview

PrintFarmer now supports **host network mode** for the API container on Linux hosts, enabling full network discovery capabilities including broadcast and multicast protocols.

---

## Why Host Networking?

### Bridge Mode (Default)
- ✅ Works on all platforms (Linux, macOS, Windows)
- ✅ Container isolation
- ❌ Limited broadcast/multicast support
- ❌ May miss automatic printer discovery

### Host Mode (Linux Only)
- ✅ Full network access (broadcast/multicast)
- ✅ Optimal printer discovery
- ✅ No NAT overhead
- ⚠️ Linux hosts only (not Docker Desktop)
- ⚠️ Requires available port on host

---

## Using the Deployment Script

### Interactive Mode (Linux)

```bash
./scripts/deploy-docker.sh
```

When prompted for **Network Mode**, you'll see:

```
Network Mode for API Container:
  1. Bridge (default) - Works on all platforms, limited broadcast/multicast
  2. Host (advanced) - Direct host network access, full discovery support

For optimal network discovery (broadcast/multicast), choose host mode.
Bridge mode works for known IP addresses but may miss auto-discovery.

Network mode [1=Bridge, 2=Host]: 2
```

**Choose option 2 for host networking.**

### Non-Interactive Mode

```bash
export ENABLE_DISCOVERY=yes
export NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
export NETWORK_MODE_CHOICE=2  # or "host"
export HTTP_PORT=8080
export API_PORT=5245

./scripts/deploy-docker.sh --non-interactive
```

### What Gets Generated

When host mode is selected, the script creates:

1. **`.env.microservices`** with network configuration:
   ```bash
   NETWORK_MODE=host
   DOCKER_HOST_NETWORK=true
   CORS__AllowedOrigins=http://localhost:3000,http://localhost:8080,http://localhost:5245
   ```

2. **`docker-compose.host-network.yml`** override file:
   ```yaml
   version: '3.8'
   
   services:
     api:
       network_mode: "host"
       ports: []
       networks: []
       environment:
         - ASPNETCORE_URLS=http://0.0.0.0:5245
         - ConnectionStrings__Redis=localhost:6379
         - DOCKER_HOST_NETWORK=true
         - NETWORK_MODE=host
   ```

---

## Deployment Commands

### Starting Services

The script automatically uses the host network override:

```bash
# Script handles this automatically
docker compose --env-file .env.microservices \
  -f docker-compose.microservices.yml \
  -f docker-compose.override.yml \
  -f docker-compose.host-network.yml \
  up -d
```

### Stopping Services

```bash
docker compose --env-file .env.microservices down
```

### Viewing Logs

```bash
# API logs
docker compose --env-file .env.microservices logs -f api

# All services
docker compose --env-file .env.microservices logs -f
```

---

## Port Configuration

### Default Ports

- **API (host mode):** 5245 (directly on host)
- **Frontend:** 8080 (mapped)
- **Redis:** 6379 (container, accessed via localhost from API)
- **PostgreSQL:** 5432 (container)

### Custom Ports

Set before deployment:

```bash
export API_PORT=9000  # Custom API port
export HTTP_PORT=8080 # Frontend port

./scripts/deploy-docker.sh --non-interactive
```

**Important:** When using custom ports, CORS is automatically configured!

---

## Network Discovery

### How It Works

**Bridge Mode:**
- API can only access known IP addresses
- No broadcast/multicast packet reception
- Manual IP entry required

**Host Mode:**
- API receives ALL network traffic (same as host)
- Supports mDNS/Bonjour announcements
- Supports SSDP/UPnP discovery
- Automatic printer detection

### Configuring Discovery Ranges

The script prompts for IP ranges to scan:

```
Network ranges to scan (comma-separated): 192.168.0.0/16,10.0.0.0/8
```

**Common ranges:**
- `192.168.0.0/16` - Home networks (192.168.x.x)
- `10.0.0.0/8` - Corporate networks (10.x.x.x)
- `172.16.0.0/12` - Docker networks (172.16-31.x.x)

---

## CORS Configuration

### Automatic CORS

The deployment script automatically configures CORS based on your ports:

**Microservices Architecture:**
```bash
CORS__AllowedOrigins=http://localhost:3000,http://localhost:${HTTP_PORT},http://localhost:${API_PORT}
```

**Monolithic Architecture:**
```bash
CORS__AllowedOrigins=http://localhost:3000,http://localhost:${HTTP_PORT}
```

### Manual CORS (if needed)

Edit `.env.microservices`:

```bash
CORS__AllowedOrigins=http://localhost:3000,http://192.168.1.100:8080,http://localhost:5245
```

Then restart:

```bash
docker compose --env-file .env.microservices down
docker compose --env-file .env.microservices up -d
```

---

## Troubleshooting

### Port Already in Use

**Error:** `bind: address already in use`

**Solution:**
```bash
# Check what's using the port
sudo lsof -i :5245

# Kill the process or choose different port
export API_PORT=5246
./scripts/deploy-docker.sh --non-interactive
```

### Cannot Access API

**Bridge Mode:**
```bash
curl http://localhost:8080/healthz  # Via frontend port mapping
```

**Host Mode:**
```bash
curl http://localhost:5245/healthz  # Direct to API port
```

### Network Discovery Not Working

1. **Verify host mode is active:**
   ```bash
   docker inspect printfarmer-api-1 | grep NetworkMode
   # Should show: "NetworkMode": "host"
   ```

2. **Check firewall:**
   ```bash
   # Ubuntu/Debian
   sudo ufw status
   sudo ufw allow 5245/tcp
   
   # CentOS/RHEL
   sudo firewall-cmd --list-ports
   sudo firewall-cmd --add-port=5245/tcp --permanent
   sudo firewall-cmd --reload
   ```

3. **Verify discovery settings:**
   ```bash
   docker compose --env-file .env.microservices exec api printenv | grep NETWORK
   # Should show:
   # ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
   # ALLOW_LOCAL_NETWORK=true
   # DOCKER_HOST_NETWORK=true
   ```

### Redis Connection Failed

**Problem:** API can't connect to Redis in host mode

**Solution:** The override file already sets `ConnectionStrings__Redis=localhost:6379`

If still failing, verify Redis is accessible:
```bash
docker compose --env-file .env.microservices exec api sh -c "nc -zv localhost 6379"
```

---

## Platform Requirements

### ✅ Supported Platforms (Host Mode)

- Ubuntu 20.04+
- Debian 11+
- CentOS 8+
- RHEL 8+
- Fedora 35+
- Any Linux with Docker Engine

### ❌ Unsupported Platforms (Host Mode)

- macOS (Docker Desktop) - **Forces bridge mode**
- Windows (Docker Desktop) - **Forces bridge mode**
- Windows (WSL2) - **May work but not officially supported**

### Bridge Mode Alternative

If host networking doesn't work, use bridge mode with known IPs:

```bash
export ENABLE_DISCOVERY=yes
export NETWORK_MODE_CHOICE=1  # Bridge
export NETWORK_RANGES=192.168.1.100/32,192.168.1.101/32  # Specific printer IPs

./scripts/deploy-docker.sh --non-interactive
```

---

## Security Considerations

### Host Network Exposure

⚠️ **Important:** Host mode exposes the API directly on the host network.

**Recommendations:**
1. Use firewall rules to restrict access
2. Don't expose to public internet
3. Use reverse proxy (nginx/traefik) for external access
4. Consider VPN for remote management

### Firewall Configuration

**Ubuntu/Debian (ufw):**
```bash
# Allow only local network
sudo ufw allow from 192.168.0.0/16 to any port 5245

# Allow specific IP
sudo ufw allow from 192.168.1.100 to any port 5245
```

**CentOS/RHEL (firewalld):**
```bash
# Add rich rule for local network only
sudo firewall-cmd --permanent --add-rich-rule='rule family="ipv4" source address="192.168.0.0/16" port port="5245" protocol="tcp" accept'
sudo firewall-cmd --reload
```

---

## Migration Guide

### From Bridge to Host

1. **Stop existing deployment:**
   ```bash
   docker compose --env-file .env.microservices down
   ```

2. **Re-run deployment script:**
   ```bash
   export NETWORK_MODE_CHOICE=2
   ./scripts/deploy-docker.sh --non-interactive
   ```

3. **Verify host mode:**
   ```bash
   docker inspect printfarmer-api-1 | grep NetworkMode
   curl http://localhost:5245/healthz
   ```

### From Host to Bridge

1. **Remove host network override:**
   ```bash
   rm docker-compose.host-network.yml
   ```

2. **Update environment:**
   ```bash
   sed -i 's/NETWORK_MODE=host/NETWORK_MODE=bridge/' .env.microservices
   sed -i 's/DOCKER_HOST_NETWORK=true/DOCKER_HOST_NETWORK=false/' .env.microservices
   ```

3. **Restart:**
   ```bash
   docker compose --env-file .env.microservices down
   docker compose --env-file .env.microservices up -d
   ```

---

## Testing Network Discovery

### Test Bridge Mode

```bash
# Should work for specific IPs
curl -X POST http://localhost:8080/api/printers/discover \
  -H "Content-Type: application/json" \
  -d '{"ipRanges": ["192.168.1.100/32"]}'
```

### Test Host Mode

```bash
# Should work for entire subnets
curl -X POST http://localhost:5245/api/printers/discover \
  -H "Content-Type: application/json" \
  -d '{"ipRanges": ["192.168.1.0/24"]}'
```

### Verify Broadcast Reception

```bash
# Inside API container (host mode)
docker compose --env-file .env.microservices exec api tcpdump -i any udp port 8089
# Should see Klipper/Moonraker announcements if printers are broadcasting
```

---

## Summary

**For Ubuntu Server Deployment (Recommended):**

```bash
# 1. Clone and navigate to repo
cd /path/to/PrintFarmer

# 2. Run deployment with host networking
export ENABLE_DISCOVERY=yes
export NETWORK_MODE_CHOICE=2  # Host mode
export NETWORK_RANGES=192.168.0.0/16  # Your network
export API_PORT=5245
export HTTP_PORT=8080

./scripts/deploy-docker.sh --non-interactive

# 3. Verify deployment
curl http://localhost:5245/healthz
curl http://localhost:8080/

# 4. Test discovery
curl -X POST http://localhost:5245/api/printers/discover \
  -H "Content-Type: application/json" \
  -d '{"ipRanges": ["192.168.0.0/16"]}'
```

**Access:**
- Frontend: http://YOUR_SERVER_IP:8080
- API: http://YOUR_SERVER_IP:5245
- Health: http://YOUR_SERVER_IP:5245/healthz

---

**Status:** ✅ Production Ready  
**Next Steps:** Deploy to Ubuntu server and verify network discovery works across your subnet
