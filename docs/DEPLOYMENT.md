# Deployment Guide

PrintFarmer supports multiple deployment architectures designed for different use cases. Choose the approach that fits your needs.

## Quick Decision Matrix

| Use Case | Recommended Approach | Why |
|----------|---------------------|-----|
| **Active Development** | Local Development | Fast builds, direct network access, easier debugging |
| **macOS + WiFi Printers** | Local Development | Docker on macOS can't reach WiFi devices |
| **Production Deployment** | Docker (Automated) | Consistent, scalable, easy maintenance |
| **Team/Staging** | Docker (Microservices) | Separate services, better monitoring |
| **Quick Testing** | Docker (Monolithic) | Single container, minimal setup |

## Local Development

**Best for:** Active development, debugging, macOS users with WiFi printers

### Quick Start
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer/src

# Terminal 1 - API Server
dotnet run --project api/Farm.Web.Api.csproj

# Terminal 2 - React Client  
cd Web/ReactApp && npm run dev
```

### Access Points
- **React App**: http://localhost:3000
- **API Server**: http://localhost:5245
- **API Health**: http://localhost:5245/healthz

### Requirements
- .NET 10.0.102 SDK
- Node.js >=20.19
- 2GB+ RAM

### Advantages
✅ Full WiFi access for printer discovery  
✅ Fast development with hot reload  
✅ Native debugging tools  
✅ No Docker overhead  

## Docker Deployment

### Docker (Automated)

**Best for:** Most users, production deployment, quick setup

```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer

# Automated setup with prompts
chmod +x scripts/deploy-docker.sh
./scripts/deploy-docker.sh

# Preview only (no containers started)
./scripts/deploy-docker.sh --dry-run

# Non-interactive (supply config via environment)
ENABLE_DISTRIBUTED_SLICING=true ENABLE_ORCA_WORKER=yes ORCA_WORKER_COUNT=1 \
DB_PROVIDER=sqlite ./scripts/deploy-docker.sh --non-interactive
```

The script will:
1. Detect your environment (macOS/Linux/Windows)
2. Guide you through architecture selection
3. Configure database and networking
4. Deploy and verify everything works

### Docker (Monolithic)

**Best for:** Simple deployments, testing, single-server setups

```bash
# Quick monolithic deployment
docker compose --env-file .env.monolithic up -d --build
```

**Architecture**: Single container with API + React + Nginx
- API and frontend in one container
- Simplified management
- Single port (8080)
- Good for testing or small deployments

### Docker (Microservices)

**Best for:** Team deployments, staging environments, better monitoring

```bash
# Microservices deployment
docker compose --env-file .env.microservices up -d --build
```

**Architecture**: 
- **Frontend**: Nginx + React SPA (Port 3000)
- **Backend**: ASP.NET API + SignalR (Port 5000)
- **Load Balancer**: Nginx proxy (Port 8080)
- Separate container scaling
- Independent health monitoring
- Better resource isolation

## Deployment Architectures

### Architecture Comparison

#### Monolithic
```
┌─────────────────────────────────────┐
│           Single Container          │
│  ┌─────────────────────────────┐   │
│  │        React SPA            │   │
│  └─────────────────────────────┘   │
│  ┌─────────────────────────────┐   │
│  │       ASP.NET API           │   │
│  │       SignalR Hubs          │   │
│  └─────────────────────────────┘   │
└─────────────────────────────────────┘
```

#### Microservices
```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│  Load Balancer  │    │    Frontend     │    │    Backend      │
│     (Nginx)     │    │   Container     │    │   Container     │
│────────────────────────────────────────────────────────────────│
```

## Database Configuration

PrintFarmer supports multiple database providers:

- **SQLite** (default): File-based, no setup required
- **PostgreSQL**: Advanced open-source database
- **SQL Server**: Enterprise database
- **MySQL**: Popular open-source database

Set `DB_PROVIDER` environment variable during deployment:
```bash
export DB_PROVIDER=postgresql  # or sqlserver, mysql, sqlite
```

## Network Configuration

### Option 1: Bridge Network with Host Gateway (Recommended)
```yaml
api:
  extra_hosts:
    - "host.docker.internal:host-gateway"
  environment:
    - ALLOW_LOCAL_NETWORK=true
    - ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8,172.16.0.0/12
```

**Benefits:**
- Maintains container isolation
- Allows selective network access
- Works cross-platform (Linux, macOS, Windows)

## Offline Deployment

For deployments without internet access:

```bash
# On machine WITH internet:
./scripts/deploy-docker.sh --prepare-offline

# Transfer ./docker-images to offline machine, then:

# On machine WITHOUT internet:
./scripts/deploy-docker.sh
# Script auto-detects and loads cached images
```

### Smart Image Caching
- Images automatically cached on first deployment
- Cache location: `~/.printfarmer/images-cache.json`
- Auto-reused on subsequent deployments
- No manual flag needed

## Advanced Features

### Distributed Slicing (OrcaSlicer Worker)
Enable distributed gcode generation across multiple worker containers:

```bash
ENABLE_DISTRIBUTED_SLICING=true \
ORCA_WORKER_COUNT=3 \
./scripts/deploy-docker.sh --non-interactive
```

### Redis Harvest Queue
For high-volume gcode harvesting, configure Redis backend:

```bash
REDIS_CONNECTION_STRING=redis://redis:6379 \
HARVEST_QUEUE_ENABLED=true \
./scripts/deploy-docker.sh --non-interactive
```

The Redis queue uses a 3-tier data structure:
1. **Primary Queue**: Sorted set with timestamp scores for FIFO ordering
2. **Processing Set**: Tracks jobs being processed (crash recovery)
3. **Completed Set**: Retains completed jobs for 24 hours (audit trail)

### Monitoring & Telemetry
Enable observability features:

```bash
ENABLE_MONITORING=true \
ENABLE_TELEMETRY=true \
./scripts/deploy-docker.sh --non-interactive
```

## Verification & Health Checks

### Verify API Health
```bash
# Basic health check
curl http://localhost:5245/healthz

# Comprehensive health status
curl http://localhost:5245/health

# API endpoints
curl http://localhost:5245/api/printers
```

### Verify Docker Deployment
```bash
# List running containers
docker compose ps

# View API logs
docker compose logs api

# View frontend logs
docker compose logs frontend

# Check container health
docker compose exec api curl http://localhost:5245/healthz
```


### Docker Build Failures
1. **Verify Docker version**: `docker --version` (requires recent version)
2. **Clear Docker cache**: `docker system prune -a`
3. **Rebuild images**: `docker compose build --no-cache`

### Connection Issues
1. **Check network mode**: Verify DOCKER_NETWORK_MODE setting
2. **Verify ports**: `lsof -i :8080` (check for conflicts)
3. **Check firewall**: Ensure ports are accessible

### Database Issues
1. **Connection string**: Verify DB_PROVIDER and connection settings
2. **Database files**: Check SQLite file permissions for file-based databases
3. **Migrations**: Application auto-runs EF Core migrations on startup

## Troubleshooting
### Printer Discovery Not Working
1. **Network access**: Verify ALLOW_LOCAL_NETWORK=true in Docker config
2. **Network range**: Ensure ALLOWED_NETWORK_RANGES covers your network
3. **Firewall**: Check that mDNS (port 5353) isn't blocked
4. **Host access**: Configure `host-gateway` and `extra_hosts` for bridge mode to allow host network access when needed

## Production Readiness Checklist

- [ ] Database provider configured and tested
- [ ] Network configuration suitable for your environment
- [ ] Firewall rules allow required ports
- [ ] SSL/TLS certificates configured (if needed)
- [ ] Health checks passing
- [ ] Backup strategy in place (for database)
- [ ] Logging and monitoring configured
- [ ] Performance tested under expected load
- [ ] Disaster recovery plan documented
