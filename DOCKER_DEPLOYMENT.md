# PrintFarmer - Docker Deployment Guide

This guide covers deploying PrintFarmer using Docker containers for production or testing environments. For local development, see [LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md).

## Architecture Overview

PrintFarmer supports two Docker deployment architectures:

### 1. Monolithic Deployment (Recommended for most users)
- **Single container** with API + React frontend served by Nginx
- **Simpler configuration** and networking
- **Better for small-scale deployments**

### 2. Microservices Deployment (Advanced)
- **Separate containers** for API, Frontend, Redis, Database
- **Enhanced networking capabilities** for device discovery
- **Better for large-scale or development team deployments**
- **Supports distributed slicing workers** (OrcaSlicer / PrusaSlicer) with horizontal scaling

## Prerequisites

### Required Software
- **Docker** 20.10+ and Docker Compose v2
- **Git** for source control
- **Linux, Windows, or macOS** (Note: macOS has WiFi networking limitations in Docker)

### Verify Installation
```bash
# Check Docker version
docker --version
docker compose version

# Verify Docker is running
docker ps
```

## Quick Start - Automated Setup

Use our automated setup script for easy deployment:

```bash
# Clone repository
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer

# Run automated setup script
chmod +x scripts/deploy-docker.sh
./scripts/deploy-docker.sh

# Dry-run (plan only, no containers started)
./scripts/deploy-docker.sh --dry-run

# Non-interactive example (export env + run)
DB_PROVIDER=postgres ENABLE_DISTRIBUTED_SLICING=true ENABLE_ORCA_WORKER=yes ORCA_WORKER_COUNT=2 \
ENABLE_PRUSA_WORKER=no PRUSA_WORKER_COUNT=0 HTTP_PORT=8080 API_PORT=5245 \
 ./scripts/deploy-docker.sh --non-interactive
```

The script will:
1. Detect your environment and suggest optimal configuration
2. Prompt for network settings with sensible defaults
3. Set up database and Redis if using microservices
4. Build and start all containers
5. Verify deployment health
6. (Optional) Enable and scale distributed slicer workers (Orca / Prusa)
7. (Optional) Perform a dry-run planning pass without launching containers

## Deployment Script Modes

The `scripts/deploy-docker.sh` launcher supports multiple execution modes to fit interactive use, CI pipelines, and planning workflows.

| Mode | Invocation | Prompts | Builds Images | Starts Containers | Scaling / Profiles | Port Remap Suggestions | Env Generation | Typical Use Cases |
|------|------------|---------|---------------|-------------------|--------------------|------------------------|----------------|------------------|
| Interactive (default) | `./scripts/deploy-docker.sh` | Yes (guided) | Yes | Yes | Yes (if enabled) | Ask & confirm | Yes | First-time setup, exploratory deployment |
| Dry-run | `./scripts/deploy-docker.sh --dry-run` | Yes | No | No | Simulated only | Suggestions printed (no confirm) | Yes | Preview changes, review planned config before running in prod |
| Non-interactive | `NON_INTERACTIVE=1 ./scripts/deploy-docker.sh --non-interactive` | No (uses env/defaults) | Yes | Yes | Yes (based on env) | Auto-accept remaps | Yes | CI/CD automation, scripted infra updates |
| Non-interactive Dry-run | `NON_INTERACTIVE=1 ./scripts/deploy-docker.sh --non-interactive --dry-run` | No | No | No | Simulated | Auto-remap silently | Yes | Pipeline validation / config drift detection |

### Selecting a Mode

- Use **Interactive** when you want guided prompts and safe defaults.
- Use **Dry-run** to see exactly what would happen without modifying Docker state.
- Use **Non-interactive** for reproducible automation (export or inject env vars beforehand).
- Combine **Non-interactive + Dry-run** in CI to validate configuration, then run a second real step if unchanged.

### Key Environment Variables for Non-Interactive Mode

You can predefine any of the variables normally collected via prompts. Common examples:

```bash
export DB_PROVIDER=postgres
export ENABLE_DISTRIBUTED_SLICING=true
export ENABLE_ORCA_WORKER=yes
export ORCA_WORKER_COUNT=2
export ENABLE_PRUSA_WORKER=no
export HTTP_PORT=8080
export API_PORT=5245
export ALLOW_LOCAL_NETWORK=true
export ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
NON_INTERACTIVE=1 ./scripts/deploy-docker.sh --non-interactive
```

### Port Remapping Behavior

- In **interactive** mode, if a requested host port is busy you are prompted to accept a suggested alternative.
- In **non-interactive** mode, the script automatically accepts the first free suggested port within +200 of the original.
- In **monolithic** deployments, worker ports (8081/8082) cannot be remapped (host networking) — warnings are emitted instead.

### Generated Artifacts

All modes (including dry-run) still produce or overwrite the environment file (`.env.monolithic` or `.env.microservices`) so you can inspect the resolved configuration. Dry-run simply skips Docker build / up operations.

### Exit Characteristics

| Mode | Exit Code on Success | Skips Build | Skips Up | Writes .env | Displays Health Checks |
|------|----------------------|-------------|---------|-------------|------------------------|
| Interactive | 0 | No | No | Yes | Yes |
| Dry-run | 0 | Yes | Yes | Yes | No |
| Non-interactive | 0 | No | No | Yes | Yes |
| Non-interactive Dry-run | 0 | Yes | Yes | Yes | No |

Non-zero exit codes indicate validation, build, or Docker orchestration errors (unless dry-run, where only validation issues can fail).

---

## Manual Setup

### Monolithic Deployment

**Step 1: Configuration**
```bash
# Copy template environment file
cp .env.template .env.monolithic

# Edit configuration (optional)
nano .env.monolithic
```

**Key settings in .env.monolithic:**
```bash
# Database provider (sqlite, postgres, sqlserver, mysql)
DB_PROVIDER=sqlite
ConnectionStrings__Default=Data Source=/data/farm.db

# Network discovery settings
ALLOW_LOCAL_NETWORK=true
ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8,172.16.0.0/12

# Application URLs
ASPNETCORE_URLS=http://0.0.0.0:8080
ASPNETCORE_ENVIRONMENT=Production
```

**Step 2: Deploy**
```bash
# Build and start
docker compose --env-file .env.monolithic up -d --build

# Verify deployment (basic health)
curl http://localhost:8080/healthz   # or curl http://localhost:8080/api/healthz
# Should return: {"status":"ok"}

# Access application
open http://localhost:8080
```

### Microservices Deployment

**Step 1: Configuration**
```bash
# Copy template environment file  
cp .env.template .env.microservices

# Edit configuration
nano .env.microservices
```

**Key settings in .env.microservices:**
```bash
# Database settings
DB_PROVIDER=postgres
ConnectionStrings__Default=Host=postgres;Database=printfarmer;Username=postgres;Password=postgres

# Redis settings
REDIS_CONNECTION=redis:6379

# Distributed slicing (workers & profiles)
ENABLE_DISTRIBUTED_SLICING=true
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
ENABLE_PRUSA_WORKER=no
PRUSA_WORKER_COUNT=0

# Worker endpoint overrides (normally not required)
# ORCA_WORKER_ENDPOINT=http://orcaslicer-worker:8080
# PRUSA_WORKER_ENDPOINT=http://prusaslicer-worker:8080
```

**Step 2: Deploy**
```bash
# Start infrastructure services first
docker compose --env-file .env.microservices up -d postgres redis

# Wait for services to be ready
sleep 10

# Start application services
docker compose --env-file .env.microservices up -d --build

# Verify deployment (comprehensive health)
curl http://localhost:8080/api/health   # or curl http://localhost:8080/health
# Should return detailed health status (JSON)

# Access application
open http://localhost:8080
```

## Database Providers

PrintFarmer supports multiple database backends:

### SQLite (Default - Simplest)
```bash
DB_PROVIDER=sqlite
ConnectionStrings__Default=Data Source=/data/farm.db
```
- **Pros:** No additional setup, good for single-instance deployments
- **Cons:** Not suitable for multiple containers or high concurrency

### PostgreSQL (Recommended for Production)
```bash
DB_PROVIDER=postgres
ConnectionStrings__Default=Host=postgres;Database=printfarmer;Username=postgres;Password=your_password
```
- **Pros:** Robust, supports high concurrency, excellent performance
- **Cons:** Requires separate container

### SQL Server
```bash
DB_PROVIDER=sqlserver
ConnectionStrings__Default=Server=sqlserver;Database=printfarmer;User Id=sa;Password=YourStrong!Password;TrustServerCertificate=True;
```
- **Pros:** Enterprise features, excellent tooling
- **Cons:** Larger resource requirements, licensing considerations

### MySQL
```bash
DB_PROVIDER=mysql
ConnectionStrings__Default=Server=mysql;Database=printfarmer;User=root;Password=your_password;
```
- **Pros:** Widely supported, good performance
- **Cons:** Some compatibility considerations

**Important:** All database providers use the unified `ConnectionStrings__Default` environment variable. The connection string format varies based on the selected provider.

## Network Configuration

### WiFi Device Discovery (Important for macOS users)

**Docker Limitations on macOS:**
- Docker Desktop on macOS cannot access WiFi-connected devices directly
- This affects network discovery of printers on your WiFi network
- **Recommended:** Use local development on macOS, Docker on Linux for production

**Linux/Windows Docker:**
- Full network access capabilities
- Can discover devices on local network with proper configuration
- Use `host` networking mode for best compatibility

### Network Discovery Settings

Configure network ranges to scan for printers:

**Environment Variables:**
```bash
ALLOW_LOCAL_NETWORK=true
ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8,172.16.0.0/12
```

**Docker Networking Modes:**

1. **Bridge Mode (Default):**
   ```yaml
   networks:
     - bridge
   ```
   - Isolated container network
   - May not reach WiFi devices on macOS

2. **Host Mode (Linux only):**
   ```yaml
   network_mode: host
   ```
   - Direct access to host network
   - Best for device discovery
   - Not available on macOS/Windows

3. **Enhanced Bridge with Capabilities:**
   ```yaml
   cap_add:
     - NET_ADMIN
     - NET_RAW
   privileged: true
   ```
   - Enhanced network capabilities
   - Better device discovery
   - Works on Linux

## Container Services

### API Container
- **Base Image:** mcr.microsoft.com/dotnet/aspnet:9.0
- **Port:** 5245 (internal), mapped to host
- **Health Check:** `/healthz` endpoint
- **Volumes:** `/data` for database persistence

### Frontend Container (Microservices only)
- **Base Image:** nginx:alpine
- **Port:** 80 (internal), mapped to host
- **Configuration:** Serves React app, proxies API calls
- **Health Check:** HTTP GET to root

### Redis Container (Microservices only)
### Distributed Slicer Workers (Optional)

Two worker types can perform slicing jobs offloaded from the API orchestrator:

| Worker | Profile | Default Port (Micro) | Purpose |
|--------|---------|----------------------|---------|
| OrcaSlicer | `orca` | 8081 (host mapped) | General-purpose slicing with OrcaSlicer |
| PrusaSlicer | `prusa` | 8082 (host mapped) | Slicing with PrusaSlicer engine |

Enable workers via environment flags or interactive script prompts:
```bash
ENABLE_DISTRIBUTED_SLICING=true
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2   # scale horizontally
ENABLE_PRUSA_WORKER=no
PRUSA_WORKER_COUNT=0
```

Compose profiles ensure unused worker images aren't started unless explicitly requested. Scaling uses `docker compose up -d --scale orcaslicer-worker=NUM` under the hood.

Override endpoints only if custom networking or external worker cluster:
```bash
ORCA_WORKER_ENDPOINT=http://orca-fleet.local:8080
PRUSA_WORKER_ENDPOINT=http://prusa-fleet.local:8080
MONO_API_ENDPOINT=http://localhost:5001   # Monolithic API endpoint (used by worker containers)
```

If distributed slicing is disabled (`ENABLE_DISTRIBUTED_SLICING=false`) workers are ignored even if counts are set.

### Pause slicer builds (new)

If you want to pause automatic Orca/Prusa worker builds (for example when keeping slicing on hold), set the following in `.env.microservices` or export before running the deploy script:

```bash
DISABLE_SLICER_BUILDS=true
```

This causes the deploy script to force-disable worker flags and set worker counts to zero. To re-enable, set `DISABLE_SLICER_BUILDS=false` and configure the `ENABLE_*` flags and counts as required.

- **Base Image:** redis:7-alpine
- **Port:** 6379
- **Purpose:** SignalR backplane for real-time updates
- **Persistence:** Optional volume for data persistence

### Database Containers (Optional)
- **PostgreSQL:** postgres:15-alpine
- **SQL Server:** mcr.microsoft.com/mssql/server:2022-latest
- **MySQL:** mysql:8.0

## Monitoring and Health Checks

### Health Endpoints
```bash
# Basic health check (either original or /api alias)
curl http://localhost:8080/healthz
# or
curl http://localhost:8080/api/healthz

# Comprehensive health check (either original or /api alias)
curl http://localhost:8080/health | jq '.'
# or
curl http://localhost:8080/api/health | jq '.'
```

### Container Health
```bash
# Check container status
docker compose ps

# View container logs
docker compose logs api
docker compose logs web
docker compose logs redis

# Follow logs in real-time
docker compose logs -f api
```

### Performance Monitoring
```bash
# Container resource usage
docker stats

# Specific service stats
docker stats printfarmer-api-1 printfarmer-web-1
```

## Common Operations

### Starting/Stopping Services
```bash
# Start all services
docker compose up -d

# Stop all services
docker compose down

# Restart specific service
docker compose restart api

# Update and restart
docker compose up -d --build

# Run deployment script in dry-run (preview) mode
./scripts/deploy-docker.sh --dry-run

# Run non-interactive (environment-driven) deployment
NON_INTERACTIVE=1 ENABLE_DISTRIBUTED_SLICING=true ENABLE_ORCA_WORKER=yes ORCA_WORKER_COUNT=1 \
 ./scripts/deploy-docker.sh --non-interactive
```

### Database Management
```bash
# Backup database (SQLite)
docker compose exec api cp /data/farm.db /data/backup.db

# Access database container (PostgreSQL)
docker compose exec postgres psql -U postgres -d printfarmer

# Reset database (WARNING: destroys data)
docker compose down -v
docker compose up -d
```

### Viewing Logs
```bash
# All services
docker compose logs

# Specific service with follow
docker compose logs -f api

# Last 50 lines
docker compose logs --tail 50 api

# Logs with timestamps
docker compose logs -t api
```

## Troubleshooting

### Common Issues

**"Service unavailable" errors:**
```bash
# Check if all containers are running
docker compose ps

# Check container health
docker compose exec api curl localhost:5245/healthz  # or localhost:5245/api/healthz

# Restart unhealthy services
docker compose restart api
```

**Network discovery not working:**
```bash
# Check networking mode
docker compose config | grep network

# Verify capabilities (Linux)
docker compose exec api ip addr show

# Test direct connectivity
docker compose exec api ping 192.168.1.1
```

**Database connection issues:**
```bash
# Check database container
docker compose logs postgres

# Verify connection string
docker compose exec api env | grep Connection

# Test database connectivity
docker compose exec api dotnet ef database update
```

**Performance issues:**
```bash
# Check resource usage
docker stats

# Scale services (microservices)
docker compose up -d --scale api=2

# Optimize Docker resources
docker system prune -a
```

### Log Analysis
```bash
# Find errors in logs
docker compose logs api 2>&1 | grep -i error

# Monitor real-time errors
docker compose logs -f api 2>&1 | grep -i "error\|exception\|failed"

# Export logs for analysis
docker compose logs api > api-logs.txt
```

### Recovery Procedures

**Complete Reset:**
```bash
# Stop all services and remove volumes
docker compose down -v

# Remove all images
docker compose rm
docker rmi $(docker images printfarmer* -q)

# Rebuild from scratch
docker compose up -d --build
```

**Partial Reset (keep data):**
```bash
# Stop services
docker compose down

# Rebuild without removing volumes
docker compose up -d --build
```

## Production Considerations

### Security
- Change default passwords in environment files
- Use Docker secrets for sensitive data
- Configure proper firewall rules
- Use HTTPS certificates for production
- Regularly update base images

### Performance
- Allocate adequate resources (CPU, memory, storage)
- Use SSD storage for database volumes
- Monitor container resource usage
- Configure log rotation
- Use health checks for automatic recovery

### Backup Strategy
- Regular database backups
- Volume snapshots
- Configuration file backups
- Test restore procedures

### Updates
```bash
# Update to latest version
git pull origin main
docker compose down
docker compose up -d --build
```

## Environment Differences

### Development vs Production

**Development:**
```bash
ASPNETCORE_ENVIRONMENT=Development
ENABLE_DETAILED_LOGGING=true
ENABLE_SWAGGER=true
```

**Production:**
```bash
ASPNETCORE_ENVIRONMENT=Production
ENABLE_DETAILED_LOGGING=false
ENABLE_SWAGGER=false
USE_HTTPS_REDIRECT=true
```

### Platform-Specific Notes

**Linux (Recommended for production):**
- Full networking capabilities
- Host networking mode available  
- Best performance and compatibility

**macOS (Development only):**
- Limited network access to WiFi devices
- Use for testing containerized builds
- Prefer local development for active work

**Windows:**
- Windows containers available
- Linux containers recommended
- Docker Desktop configuration important

## Next Steps

- **Local Development:** See [LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md) for development setup
- **Contributing:** See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines
- **Advanced Configuration:** See [DOCKER_NETWORK_CONFIG.md](DOCKER_NETWORK_CONFIG.md) for network details
