# Ubuntu Server Deployment - Quick Start

**For:** Production deployment with network discovery  
**Platform:** Ubuntu Server 20.04+ (Linux required)  
**Last Updated:** October 6, 2025

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

### Option 1: Interactive

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

### Option 2: Non-Interactive (Recommended)

```bash
export ARCHITECTURE=microservices
export DB_PROVIDER=Postgres
export DB_PASSWORD=YourSecurePassword123!
export ENABLE_DISCOVERY=yes
export NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
export NETWORK_MODE_CHOICE=2  # Host mode for full discovery
export HTTP_PORT=8080
export API_PORT=5245
export ENVIRONMENT=Production

./scripts/deploy-docker.sh --non-interactive
```

**Wait for:**
```
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
# 1. Verify host mode
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

## Security Checklist

- [ ] Changed default database password
- [ ] Configured firewall rules
- [ ] Restricted CORS to known origins
- [ ] Using HTTPS reverse proxy (nginx/traefik)
- [ ] Regular backups configured
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
