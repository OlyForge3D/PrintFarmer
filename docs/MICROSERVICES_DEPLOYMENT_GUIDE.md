# Microservices Deployment Guide

This guide explains how to deploy PrintFarmer using the **Microservices Architecture**, which is designed for advanced networking scenarios and device discovery requirements.

## Architecture Overview

The PrintFarmer microservices architecture separates components into dedicated containers:

```
┌─────────────────────────────────────────────────────────────┐
│ HOST NETWORK (Direct Host Access)                           │
│  ┌────────────────────────────────────────────────────────┐ │
│  │ API Container (PrintFarmer Backend)                    │ │
│  │ • Listens on host port 5245                            │ │
│  │ • Full network access for device discovery             │ │
│  │ • Connects to database via bridge network service name │ │
│  └────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ BRIDGE NETWORK (Docker Internal)                            │
│  ┌──────────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │ Frontend Service │  │ Database     │  │ OrcaSlicer   │  │
│  │ (Nginx/React)    │  │ (PostgreSQL/ │  │ Workers     │  │
│  │ Port 80/8080     │  │  MySQL/MSSQL)│  │ (Optional)  │  │
│  │                  │  │ Port 5432/  │  │ Port 8081   │  │
│  │ Proxies to:      │  │ 3306/1433    │  │             │  │
│  │ http://api:5245  │  └──────────────┘  └──────────────┘  │
│  └──────────────────┘                                       │
│                                                              │
│  Internal DNS:                                              │
│  • api (resolves to API container on host network)         │
│  • database (resolves to DB container)                     │
│  • orcaslicer-worker (resolves to worker container)        │
└─────────────────────────────────────────────────────────────┘
```

## Key Benefits

### API on Host Network
- **Full Network Access**: API can broadcast to local network for printer discovery
- **Device Discovery**: Supports Moonraker/PrusaLink printer auto-discovery on network
- **Direct Port Access**: API listens directly on host port (default 5245)
- **Multicast Support**: Can send/receive multicast packets for printer discovery

### Services on Bridge Network
- **Isolation**: Database, frontend, and workers are isolated from host
- **Container Communication**: Services communicate via Docker internal DNS
- **Port Mapping**: Only frontend (80/8080) and API (5245) exposed to host
- **Security**: Database credentials not exposed to host network

## Deployment Steps

### 1. Quick Start (Interactive)
```bash
cd /path/to/PrintFarmer
./scripts/deploy-docker.sh
```

When prompted, select:
- **Architecture**: `2` (Microservices)
- **Database**: PostgreSQL (recommended) or SQL Server/MySQL
- **Network Mode**: Bridge (default - recommended)
- **HTTP Port**: `8080` (or any available port)
- **API Port**: `5245` (direct API access)

### 2. Non-Interactive Deployment
```bash
export DB_PROVIDER=postgres
export ARCHITECTURE=microservices
export HTTP_PORT=8080
export API_PORT=5245
export ENABLE_ORCA_WORKER=yes
export ORCA_WORKER_COUNT=2

./scripts/deploy-docker.sh --non-interactive
```

### 3. Dry-Run (Planning Only)
```bash
./scripts/deploy-docker.sh --dry-run --architecture microservices
```

## Access Points

After deployment, access PrintFarmer at:

| Service | URL | Purpose |
|---------|-----|---------|
| **Web UI** | `http://localhost:8080` | React dashboard and setup wizard |
| **API** | `http://localhost:5245` | Direct API access (for integrations) |
| **API Docs** | `http://localhost:5245/swagger` | OpenAPI documentation (development) |
| **Health** | `http://localhost:5245/health` | API health/diagnostics |

## Network Communication Flow

### Inside the Microservices Network

1. **Frontend → API**:
   ```
   nginx (bridge network) → http://api:5245/api/*
   ```
   - Frontend proxy routes `/api/*` requests to API service
   - API is accessible via DNS name `api` on bridge network
   - Connection tunnels through bridge network tunnel to host network

2. **API → Database**:
   ```
   api (host network) → database:5432 (bridge network)
   ```
   - API container connects to database via DNS name
   - Host network API can reach bridge network services
   - Connection uses Docker network tunnel

3. **Frontend → SignalR**:
   ```
   browser (host) → nginx:8080 (bridge) → /hubs/* → api:5245 (host) via WebSocket
   ```
   - WebSocket connections tunneled through nginx proxy
   - SignalR hub on API handles real-time printer updates

4. **Device Discovery**:
   ```
   api (host network) → broadcast to local network:5353 (mDNS)
   ```
   - API discovers printers via mDNS on host network
   - Reads local network configuration from ALLOWED_NETWORK_RANGES

## Database Setup

### PostgreSQL (Recommended)
```bash
DB_PROVIDER=postgres
POSTGRES_DB=printfarmer
POSTGRES_USER=postgres
POSTGRES_PASSWORD=your_secure_password
```

### SQL Server
```bash
DB_PROVIDER=sqlserver
SQLSERVER_DB=printfarmer
SQLSERVER_PASSWORD=your_secure_password
SQLSERVER_EDITION=Developer  # or Standard/Enterprise (requires license)
```

### MySQL
```bash
DB_PROVIDER=mysql
MYSQL_DB=printfarmer
MYSQL_USER=root
MYSQL_PASSWORD=your_secure_password
```

## Distributed Slicing (Optional)

Enable OrcaSlicer workers for distributed slicing jobs:

```bash
ENABLE_DISTRIBUTED_SLICING=true
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2  # Number of worker replicas
```

Workers communicate with API via container DNS: `api:5245`

## Monitoring & Troubleshooting

### View Container Status
```bash
docker compose ps
```

### View Logs
```bash
# All services
docker compose logs -f

# Specific service
docker compose logs -f api           # API backend
docker compose logs -f frontend      # Web UI
docker compose logs -f database      # Database
docker compose logs -f orcaslicer-worker  # Slicer worker
```

### Test API Health
```bash
curl http://localhost:5245/health

curl http://localhost:5245/api/printers
```

### Test Device Discovery
```bash
# Check if API sees local network
curl http://localhost:5245/api/system/discovery

# Monitor discovery logs
docker compose logs api | grep -i discovery
```

### Network Troubleshooting

**Frontend can't reach API**:
- Verify nginx is proxying correctly: `docker compose logs frontend`
- Check API is running: `docker compose logs api`
- Test direct API connection: `curl http://localhost:5245/healthz`

**API can't reach database**:
- Verify database is running: `docker compose ps database`
- Check database connection string in logs: `docker compose logs api | grep -i connection`
- Verify database port: `docker compose exec database psql -U postgres -c "SELECT 1"`

**Device discovery not working**:
- Verify API is on host network: `docker inspect $(docker ps -q -f "name=.*api") | grep NetworkMode`
- Check network ranges: `curl http://localhost:5245/api/system/discovery`
- Verify firewall allows mDNS: `sudo netstat -ln | grep 5353`

## Cleanup & Teardown

### Stop Services
```bash
docker compose down
```

### Stop and Remove Volumes (Full Cleanup)
```bash
./scripts/deploy-docker.sh --tear-down
```

### Manual Cleanup
```bash
# Stop and remove containers
docker compose down --volumes --remove-orphans

# Remove images
docker rmi printfarmer-api:latest printfarmer-frontend:latest

# Remove generated files
rm -f docker-compose.yml .env docker-compose.override.yml
```

## Migration from Monolithic to Microservices

If migrating from monolithic deployment:

1. **Backup existing data**:
   ```bash
   # Export current database if using embedded SQLite
   ```

2. **Tear down monolithic deployment**:
   ```bash
   docker compose down --volumes
   ```

3. **Deploy microservices**:
   ```bash
   ./scripts/deploy-docker.sh --architecture microservices
   ```

4. **Restore data** (if needed):
   - Copy database dump to new database service
   - Re-add printers and configurations

## Performance Tuning

### API Container Resources
Edit docker-compose.yml to add resource limits:
```yaml
services:
  api:
    deploy:
      resources:
        limits:
          cpus: '2.0'
          memory: 2G
        reservations:
          cpus: '1.0'
          memory: 1G
```

### Database Container Resources
```yaml
services:
  database:
    deploy:
      resources:
        limits:
          cpus: '4.0'
          memory: 4G
        reservations:
          cpus: '2.0'
          memory: 2G
```

### Worker Scaling
```bash
# Scale workers to 4 instances
docker compose up -d --scale orcaslicer-worker=4
```

## Security Best Practices

1. **Change Default Passwords**:
   ```bash
   POSTGRES_PASSWORD=your_very_secure_password_here
   ```

2. **Restrict Network Access**:
   - Run firewall to limit port access
   - Only expose ports 80, 8080, and 5245
   - Restrict SSH and other services

3. **Use HTTPS**:
   - Set up reverse proxy with SSL/TLS
   - Configure Let's Encrypt certificates
   - Reference: See SECURITY.md

4. **API Authentication**:
   - Configure API keys in setup wizard
   - Restrict API access by IP if possible
   - Monitor API access logs

## See Also

- [DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md) - General Docker deployment guide
- [LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md) - Local development setup
- [SECURITY.md](../SECURITY.md) - Security hardening guide
- [DEPLOYMENT_TESTING_CHECKLIST.md](DEPLOYMENT_TESTING_CHECKLIST.md) - Deployment validation
