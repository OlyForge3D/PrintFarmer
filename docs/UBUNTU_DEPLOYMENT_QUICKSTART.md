# Ubuntu Server Deployment - Quick Start

**For:** Production deployment with network discovery  
**Platform:** Ubuntu Server 20.04+ (Linux required)  
**Last Updated:** October 6, 2025  
**New:** Configuration persistence - settings auto-saved for easy re-deployment!

---

## Prerequisites

```bash
# 1. Install Docker
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh
sudo usermod -aG docker $USER
newgrp docker

# 2. Verify installation
docker --version
docker compose version

# 3. Clone repository
git clone https://github.com/yourusername/PrintFarmer.git
cd PrintFarmer
```

---

## Quick Deployment (Host Network Mode)

### Option 1: Interactive (First Time)

```bash
./scripts/deploy-docker.sh
```

**Answer prompts:**
- Architecture: `2` (Microservices)
- Database: Your choice (PostgreSQL recommended)
- Enable discovery: `yes`
- Network ranges: `192.168.0.0/16,10.0.0.0/8` (adjust for your network)
- **Network mode: `2` (Host)** ← **IMPORTANT for discovery**
- HTTP Port: `8080`
- API Port: `5245`

**✨ New:** Your settings are automatically saved to `.deploy-config`!

### Option 2: Interactive Re-Deployment

```bash
# Run script again - previous settings used as defaults!
./scripts/deploy-docker.sh

# Just press Enter to accept previous values
# Or type new values to change settings
```

**No need to remember your choices - they're loaded automatically!**

### Option 3: Non-Interactive (Hands-Off)

**First deployment:**
```bash
export ARCHITECTURE=microservices
export DB_PROVIDER=Postgres
export DB_PASSWORD=YourSecurePassword123!
export ENABLE_DISCOVERY=yes
export NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
export NETWORK_MODE_CHOICE=1
export HTTP_PORT=8080
export API_PORT=5245
export ENVIRONMENT=Production

./scripts/deploy-docker.sh --non-interactive
```

**Re-deployment (uses saved config):**
```bash
# That's it! Config automatically loaded
./scripts/deploy-docker.sh --non-interactive
```

**Wait for:**
```
💾 Saving Deployment Configuration
✅ Configuration saved to .deploy-config
✅ Deployment successful!
Frontend: http://localhost:8080
API: http://localhost:5245
Health: http://localhost:5245/healthz
```

---

## Verify Deployment

```bash
# 1. Check containers
docker ps

# 2. Health check
curl http://localhost:5245/healthz
# Expected: {"status":"ok"}

# 3. Verify host networking
docker inspect printfarmer-api-1 | grep NetworkMode
# Expected: "NetworkMode": "host"

# 4. Check logs
docker compose --env-file .env.microservices logs -f api
```

---

## Test Network Discovery

```bash
# Discover printers on your network
curl -X POST http://localhost:5245/api/printers/discover \
  -H "Content-Type: application/json" \
  -d '{"ipRanges": ["192.168.0.0/24"]}'

# Should return discovered printers with Moonraker/PrusaLink
```

---

## Access URLs

**Replace `YOUR_SERVER_IP` with your Ubuntu server's IP address**

- **Frontend:** `http://YOUR_SERVER_IP:8080`
- **API:** `http://YOUR_SERVER_IP:5245`
- **Health:** `http://YOUR_SERVER_IP:5245/healthz`
- **API Docs:** `http://YOUR_SERVER_IP:5245/swagger` (if Development mode)

---

## Firewall Configuration

```bash
# Allow API and frontend ports
sudo ufw allow 8080/tcp comment 'PrintFarmer Frontend'
sudo ufw allow 5245/tcp comment 'PrintFarmer API'

# Or restrict to local network only
sudo ufw allow from 192.168.0.0/16 to any port 8080
sudo ufw allow from 192.168.0.0/16 to any port 5245

# Enable firewall
sudo ufw enable
sudo ufw status
```

---

## Common Commands

```bash
# View logs
docker compose --env-file .env.microservices logs -f api

# Restart services
docker compose --env-file .env.microservices restart

# Stop services
docker compose --env-file .env.microservices down

# Start services
docker compose --env-file .env.microservices up -d

# Update and redeploy
git pull
docker compose --env-file .env.microservices down
docker compose --env-file .env.microservices build --no-cache
docker compose --env-file .env.microservices up -d
```

---

## Troubleshooting

### Port Already in Use

```bash
# Find what's using the port
sudo lsof -i :5245

# Kill the process or change port
export API_PORT=5246
./scripts/deploy-docker.sh --non-interactive
```

### Network Discovery Not Working

```bash
# 1. Verify network configuration
docker inspect printfarmer-api-1 | grep NetworkMode

# 2. Check environment
docker compose --env-file .env.microservices exec api printenv | grep NETWORK

# 3. Check firewall
sudo ufw status

# 4. Test broadcast (requires tcpdump)
docker compose --env-file .env.microservices exec api sh -c "apt update && apt install -y tcpdump"
docker compose --env-file .env.microservices exec api tcpdump -i any udp port 8089
```

### Cannot Access from Another Computer

```bash
# 1. Check server IP
ip addr show

# 2. Verify firewall allows external access
sudo ufw allow from any to any port 8080
sudo ufw allow from any to any port 5245

# 3. Update CORS if needed
docker compose --env-file .env.microservices down

# Edit .env.microservices
nano .env.microservices
# Add: CORS__AllowedOrigins=http://localhost:3000,http://192.168.1.100:8080,http://localhost:5245

docker compose --env-file .env.microservices up -d
```

---

## Configuration Persistence

### Automatic Configuration Saving

**Every deployment automatically saves your settings to `.deploy-config`**

**Benefits:**
- ✅ **Re-deployment is instant** - Run script with no prompts
- ✅ **No need to remember settings** - Everything saved automatically
- ✅ **Easy troubleshooting** - Review your exact configuration
- ✅ **Consistent deployments** - Same settings every time

### View Your Configuration

```bash
# See your saved settings
cat .deploy-config

# Example output:
# ARCHITECTURE=microservices
# DB_PROVIDER=Postgres
# NETWORK_MODE=host
# HTTP_PORT=8080
# ...
```

### Quick Re-Deployment

```bash
# Stop containers
docker compose --env-file .env.microservices down

# Update code
git pull

# Re-deploy with saved config (no prompts!)
./scripts/deploy-docker.sh --non-interactive

# That's it! Uses all your previous settings
```

### Update Specific Settings

```bash
# Option 1: Edit config file directly
nano .deploy-config
# Change: ORCA_WORKER_COUNT=1 → ORCA_WORKER_COUNT=4
./scripts/deploy-docker.sh --non-interactive

# Option 2: Override with environment variable
export ORCA_WORKER_COUNT=4
./scripts/deploy-docker.sh --non-interactive

# Option 3: Run interactively (previous values as defaults)
./scripts/deploy-docker.sh
# Press Enter to keep old values, or type new ones
```

### Security Note

⚠️ **`.deploy-config` contains passwords** - keep it secure!

```bash
# Check permissions (should be 600)
ls -la .deploy-config
# -rw------- 1 user user ... .deploy-config

# Already gitignored - won't be committed
git status .deploy-config
# fatal: pathspec '.deploy-config' did not match any files
```

**See:** `docs/DEPLOYMENT_CONFIG_PERSISTENCE.md` for complete documentation

---

## Security Checklist

- [ ] Changed default database password
- [ ] Configured firewall rules
- [ ] Restricted CORS to known origins
- [ ] Using HTTPS reverse proxy (nginx/traefik)
- [ ] Regular backups configured
- [ ] Secured `.deploy-config` file (permissions 600)
- [ ] Monitoring/alerting set up

---

## Next Steps

1. ✅ Access frontend at `http://YOUR_SERVER_IP:8080`
2. ✅ Complete setup wizard (admin account)
3. ✅ Test printer discovery
4. ✅ Add printers manually if discovery doesn't find them
5. ✅ Configure print profiles and slicing settings

---

## Support

- **Documentation:** `/docs/HOST_NETWORK_DEPLOYMENT.md`
- **Implementation Details:** `/docs/HOST_NETWORK_IMPLEMENTATION.md`
- **Issues:** Check logs with `docker compose logs -f`

---

**Status:** ✅ Ready for production deployment!  
**Deployment Time:** ~10-15 minutes (including Docker installation)
