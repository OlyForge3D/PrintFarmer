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

## Deployment System Components

PrintFarmer's Docker deployment system consists of three coordinated layers that work together to enable flexible, repeatable deployments:

### 1. Orchestration Layer: `scripts/deploy-docker.sh`

The main user-facing deployment script that handles interactive setup, validation, and container orchestration.

**Key Responsibilities:**
- Environment detection (OS, Docker versions)
- Interactive configuration prompts with sensible defaults
- Configuration validation and constraint enforcement
- Calling the compose generator with appropriate options
- Health checks and diagnostics after deployment
- Safe deployment teardown

**Key Features:**
- Multiple execution modes (interactive, non-interactive, dry-run)
- Configuration persistence (`.deploy-config` file)
- Automatic password generation and validation
- Database credential management
- Port conflict detection and remapping
- Pre/post-deployment health verification

### 2. Generation Layer: `scripts/docker/compose-generator.sh`

Dynamically generates `docker-compose.yml` from reusable YAML templates based on deployment options.

**Key Responsibilities:**
- Reads common YAML anchors from `common.yml`
- Assembles base compose file from microservices template
- Injects optional service configurations (monitoring, telemetry, discovery)
- Validates final YAML structure
- Copies required Dockerfiles to output directory

**Input Options:**
- `--db-provider` - postgres | sqlserver | mysql | sqlite
- `--enable-orca-worker` - yes | no (enable distributed slicing)
- `--include-monitoring`, `--include-telemetry`, `--include-discovery` - optional services
- `--output-dir` - where to write generated files

**Output Files:**
- `docker-compose.yml` - Generated Compose configuration
- `docker-entrypoint-config.sh` - Database initialization script
- `Dockerfile.multistage` - Copied from dockerfiles/ directory

### 3. Build Layer: `dockerfiles/Dockerfile.multistage`

Single multi-stage Dockerfile containing all build targets for all deployment modes.

**Build Targets:**
- `api-runtime` - Compiled .NET API server (used in both architectures)
- `frontend-runtime` - React TypeScript web UI (used in microservices only)
- `orcaslicer-worker` - Distributed slicing worker (optional microservice)
- `slicer-base` - Common base for slicing workers
- `printer-discovery-runtime` - Network discovery service
- `orcaslicer-binaries` - Pre-built OrcaSlicer binaries

**Advantages of Multi-Stage Design:**
- Single source of truth for all build logic
- Efficient layer caching across builds (faster rebuilds)
- Unused targets don't affect deployment performance
- Clear separation of concerns (each target is independent)

### Template Structure

```
scripts/docker/compose-templates/
├── common.yml                    # Shared YAML anchors & x- definitions
├── monolithic.yml               # Monolithic architecture base
├── microservices.yml            # Microservices architecture base
├── services/
│   ├── monitoring.yml           # Prometheus, Grafana
│   ├── telemetry.yml            # OpenTelemetry collector
│   ├── security.yml             # Security configurations
│   ├── registry.yml             # Docker registry service
│   └── discovery.yml            # Network printer discovery
└── dockerfiles/
    └── Dockerfile.multistage    # Single multi-stage build file
```

**common.yml** defines reusable YAML anchors for:
- Health checks (API, database)
- Resource limits (CPU, memory)
- Security contexts
- Networks
- Restart policies

Example:
```yaml
x-health-check-api: &health-check-api
  test: ["CMD", "curl", "-f", "http://localhost:5245/healthz"]
  interval: 10s
  timeout: 5s
  retries: 5
  start_period: 30s
```

### Data Flow

```
┌─────────────────────────────────────┐
│ User: ./scripts/deploy-docker.sh    │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│ 1. Detect environment               │
│ 2. Load/prompt configuration        │
│ 3. Validate settings                │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│ Call compose-generator.sh with:     │
│ • --db-provider <provider>          │
│ • --enable-orca-worker <yes/no>     │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│ compose-generator.sh:               │
│ 1. Load common.yml anchors          │
│ 2. Select architecture template     │
│ 3. Inject optional services         │
│ 4. Validate YAML                    │
│ 5. Output: docker-compose.yml       │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│ Docker Compose:                     │
│ • Builds images using Dockerfile    │
│ • Starts containers                 │
│ • Initializes database              │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│ Health checks & validation          │
│ • API responsive                    │
│ • Database connections work         │
│ • Services healthy                  │
└─────────────────────────────────────┘
```

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
git clone https://github.com/OlyForge3D/printfarmer.git
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

2. **Network Considerations:**
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

## Data Persistence & Storage

### Overview

PrintFarmer stores all persistent data on the host filesystem using **Docker bind mounts**. This ensures:
- ✅ Data survives container restarts and rebuilds
- ✅ Easy backups of application data
- ✅ Clear separation between application and data
- ✅ Easy access to files from host system

### External Storage Paths

All storage is configurable via environment variables with sensible defaults under `~/.printfarmer/`:

| Data Type | Environment Variable | Default Path | Purpose | Monolithic | Microservices |
|-----------|---------------------|---------------|---------|-----------|--------------|
| **G-code Files** | `EXTERNAL_GCODE_PATH` | `~/.printfarmer/gcode/` | Uploaded & sliced G-code files | ✅ | ✅ |
| **3D Models** | `EXTERNAL_MODELS_PATH` | `~/.printfarmer/models/` | Uploaded 3D model files | ✅ | ✅ |
| **Slicer Profiles** | `EXTERNAL_PROFILES_PATH` | `~/.printfarmer/slicer-profiles/` | OrcaSlicer/PrusaSlicer profiles | ✅ | ✅ |
| **Application Data** | `EXTERNAL_APP_DATA_PATH` | `~/.printfarmer/data/` | SQLite database (monolithic) | ✅ | ❌ |
| **Database** | `EXTERNAL_DATABASE_PATH` | `~/.printfarmer/database/` | PostgreSQL/MySQL/SQL Server data | ❌ | ✅ |

### Volume Mounts in Docker Compose

**Monolithic Architecture**:
```yaml
services:
  printfarmer-api:
    volumes:
      - ${EXTERNAL_GCODE_PATH:-.volumes/printfarmer-gcode}:/app/gcode:Z
      - ${EXTERNAL_MODELS_PATH:-.volumes/printfarmer-models}:/app/uploads:Z
      - ${EXTERNAL_PROFILES_PATH:-.volumes/printfarmer-profiles}:/app/slicer-profiles:Z
      - ${EXTERNAL_APP_DATA_PATH:-.volumes/printfarmer-app-data}:/data:Z
    environment:
      - GCODE_STORAGE_PATH=/app/gcode
      - MODEL_UPLOAD_PATH=/app/uploads
      - ASPNETCORE_Kestrel__Certificates__Default__Path=/data/certificates/server.pfx
```

**Microservices Architecture**:
```yaml
services:
  postgres:
    volumes:
      - ${EXTERNAL_DATABASE_PATH:-.volumes/printfarmer-database}:/var/lib/postgresql/data:Z
  
  api-worker:
    volumes:
      - ${EXTERNAL_GCODE_PATH:-.volumes/printfarmer-gcode}:/app/gcode:Z
      - ${EXTERNAL_MODELS_PATH:-.volumes/printfarmer-models}:/app/uploads:Z
      - ${EXTERNAL_PROFILES_PATH:-.volumes/printfarmer-profiles}:/app/slicer-profiles:Z
```

**Note**: The `:Z` flag enables SELinux shared bind mount for multi-container access. It's safe to use even on non-SELinux systems.

### Deployment Script Setup

The `deploy-docker.sh` script handles storage configuration automatically:

**Interactive Mode** (prompts for each path):
```bash
./scripts/deploy-docker.sh
# Prompts:
# 1. Models directory path (default: ~/.printfarmer/models)
# 2. G-code directory path (default: ~/.printfarmer/gcode)
# 3. Profiles directory path (default: ~/.printfarmer/slicer-profiles)
# 4. App data directory path (default: ~/.printfarmer/data)
# 5. Database directory path (default: ~/.printfarmer/database)
```

**Non-Interactive Mode** (uses environment variables or `.deploy-config`):
```bash
EXTERNAL_GCODE_PATH=/mnt/storage/gcode \
EXTERNAL_MODELS_PATH=/mnt/storage/models \
./scripts/deploy-docker.sh --non-interactive
```

**Configuration Persistence**:
- Settings saved to `~/.deploy-config` for future deployments
- Use the same paths on subsequent runs without re-entering
- Edit `~/.deploy-config` to change storage locations

### Data Persistence Examples

**Verify storage setup**:
```bash
# Check that directories were created
ls -lah ~/.printfarmer/

# Expected output:
# data/                 - Application data (SQLite database)
# gcode/                - G-code files from uploads/slicing
# models/               - 3D model uploads
# slicer-profiles/      - OrcaSlicer/PrusaSlicer profile data
# database/             - External database data (microservices only)
```

**Files survive container restart**:
```bash
# Upload a file via PrintFarmer UI
# File appears in storage directory
ls ~/.printfarmer/gcode/

# Stop and restart containers
docker compose down && docker compose up -d

# File still accessible
ls ~/.printfarmer/gcode/  # File is still there!
```

**Backup application data**:
```bash
# Backup entire PrintFarmer data
tar -czf ~/printfarmer-backup-$(date +%Y%m%d).tar.gz ~/.printfarmer/

# Backup only database
cp ~/.printfarmer/data/farm.db ~/farm.db.backup
```

### Changing Storage Locations

To use a different storage location (e.g., external NAS, separate SSD):

**1. Update `.deploy-config`**:
```bash
nano ~/.deploy-config

# Change paths, e.g.:
EXTERNAL_GCODE_PATH=/mnt/nas/printfarmer-gcode
EXTERNAL_MODELS_PATH=/mnt/nas/printfarmer-models
EXTERNAL_APP_DATA_PATH=/mnt/nas/printfarmer-data
```

**2. Teardown and redeploy**:
```bash
./scripts/deploy-docker.sh --tear-down
# Creates directories and generates new docker-compose.yml
```

**3. Optional: Migrate existing data**:
```bash
# Copy data from old location
cp -r ~/.printfarmer/gcode/* /mnt/nas/printfarmer-gcode/
cp -r ~/.printfarmer/models/* /mnt/nas/printfarmer-models/
```

### Troubleshooting Storage Issues

**Problem: "Permission denied" when uploading files**

```bash
# Fix permissions on storage directories
chmod -R 755 ~/.printfarmer/gcode
chmod -R 755 ~/.printfarmer/models
chmod -R 755 ~/.printfarmer/data

# Or fix ownership if running as different user
chown -R $(id -u):$(id -g) ~/.printfarmer/
```

**Problem: Disk space errors during uploads**

```bash
# Check available space
df -h ~/.printfarmer/

# If low, move storage to larger partition
mkdir -p /mnt/larger-storage/printfarmer
./scripts/deploy-docker.sh --tear-down
# Edit ~/.deploy-config with new paths
./scripts/deploy-docker.sh --non-interactive
```

**Problem: Files missing after container restart**

```bash
# Verify bind mounts are active
docker inspect -f '{{json .Mounts}}' $(docker ps -q -f label=com.docker.compose.service=printfarmer-api) | jq

# Check docker-compose.yml was generated with volume mounts
grep -A 5 "volumes:" docker-compose.yml

# Redeploy if necessary
./scripts/deploy-docker.sh --non-interactive
```

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

## Email (Mailjet) Configuration

PrintFarmer can send transactional emails (password reset, email confirmation). In Docker production deployments you typically enable the `mailjet` provider. For development or staging without real delivery you can leave the provider as `console` which logs the payload only.

### 1. Provider Selection

```
Email__Provider=mailjet   # mailjet | console
Email__Enabled=true       # ensure email features are active
```

Use `console` for local / test environments to avoid consuming Mailjet quota.

### 2. Required Environment Variables

Place these in your `.env.monolithic` or `.env.microservices` file (or export prior to non-interactive deploy). Double underscores (`__`) map hierarchical configuration into .NET options binding.

```
# Core sender identity
Email__Enabled=true
Email__Provider=mailjet
Email__FromAddress=noreply@yourdomain.com
Email__FromName=PrintFarmer
Email__BaseUrl=https://yourdomain.com    # Public HTTPS origin used in email links

# Mailjet credentials (do NOT commit real values)
Email__Mailjet__ApiKey=${MAILJET_API_KEY}
Email__Mailjet__ApiSecret=${MAILJET_API_SECRET}
Email__Mailjet__Sandbox=false            # true = accept but do not send
```

You may export secrets separately (recommended):

```
export MAILJET_API_KEY="pk_live_xxxxxxxxx"
export MAILJET_API_SECRET="sk_live_yyyyyyyy"
```

### 3. Docker Compose Inline Example (Microservices `api` service)

```yaml
services:
   api:
      environment:
         Email__Enabled: "true"
         Email__Provider: "mailjet"
         Email__FromAddress: "noreply@yourdomain.com"
         Email__FromName: "PrintFarmer"
         Email__BaseUrl: "https://yourdomain.com"
         Email__Mailjet__ApiKey: "${MAILJET_API_KEY}"
         Email__Mailjet__ApiSecret: "${MAILJET_API_SECRET}"
         Email__Mailjet__Sandbox: "false"
```

### 4. Optional Rate Limiting Overrides

Defaults are sane; override only if needed:

```
RateLimiting__PasswordReset__MaxPerHour=5
RateLimiting__PasswordReset__MaxPerDay=20
RateLimiting__EmailConfirmation__MaxPerHour=5
RateLimiting__EmailConfirmation__MaxPerDay=20
```

### 5. Sandbox vs Live

| Mode    | `Email__Mailjet__Sandbox` | Behavior                            |
|---------|---------------------------|-------------------------------------|
| Sandbox | true                      | Payload accepted, not delivered     |
| Live    | false                     | Emails sent to recipients           |

Always set to `false` before real user onboarding.

### 6. Verifying Deployment

After bringing containers up:

```
curl -X POST "http://localhost:8080/api/auth/forgot-password" \
   -H 'Content-Type: application/json' \
   -d '{"email":"testuser@yourdomain.com"}'

docker compose logs -f api | grep -i mailjet
```

Successful Mailjet send example:
```
Mailjet email sent to testuser@yourdomain.com. Status=200
```

Missing key fallback:
```
Mailjet API keys missing. Email logged only.
```

### 7. Common Pitfalls

| Issue | Symptom | Fix |
|-------|---------|-----|
| Missing API keys | Fallback logging only | Set `Email__Mailjet__ApiKey` / `Email__Mailjet__ApiSecret` |
| Wrong `Email__BaseUrl` | Broken links | Point to public HTTPS origin |
| Sandbox left enabled | No real emails | Set `Email__Mailjet__Sandbox=false` |
| Provider still console | No delivery | Set `Email__Provider=mailjet` |

### 8. Security Recommendations

* Use Docker secrets or orchestrator secret injection for API keys (avoid plain text in version control)
* Rotate Mailjet keys periodically (e.g. every 90 days)
* Configure SPF/DKIM for the `FromAddress` domain to reduce spam filtering
* Restrict access to logs containing email payloads in production

### 9. Local / Test Mode

Leave provider as console:
```
Email__Provider=console
```
Trigger flows (password reset / email confirmation) and observe structured log output without external calls.

### 10. Example `.env.mailjet.example`

See the generated `env.mailjet.example` file at repo root for a ready-to-copy reference.

## Raspberry Pi / ARM64 Deployment

PrintFarmer supports deployment on ARM64 platforms including Raspberry Pi 4 and Pi 5.

### Supported Hardware

| Device | RAM | Status |
|--------|-----|--------|
| Raspberry Pi 5 | 4GB+ | ✅ Recommended |
| Raspberry Pi 4 | 4GB+ | ✅ Supported |
| Raspberry Pi 4 | 2GB | ⚠️ May work with reduced performance |
| Other ARM64 SBCs | 4GB+ | ✅ Should work (untested) |

### What Works on ARM64

- ✅ **API server** — Full functionality including all printer backends (Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge)
- ✅ **React frontend** — Full UI served via Nginx
- ✅ **Printer discovery** — Network scanning and auto-detection
- ✅ **Database** — SQLite (default) and PostgreSQL
- ✅ **SignalR** — Real-time printer status updates
- ✅ **Spoolman integration** — Filament tracking
- ✅ **G-code file management** — Upload, storage, and printing

### What's Disabled on ARM64

- ❌ **OrcaSlicer worker** — No ARM64 binary available from upstream
- ❌ **Slicer Host** — Depends on OrcaSlicer
- ❌ **3D model file processing** — Requires x86-only native libraries (lib3mf, AssimpNetter)
- ❌ **STL/3MF thumbnail generation** — Requires native rendering libraries

The compose generator automatically detects ARM architecture and excludes slicing services. No manual configuration needed.

### Deploying on Raspberry Pi

```bash
# Standard deployment — ARM is auto-detected
./scripts/deploy-docker.sh --non-interactive

# Or with explicit native architecture flag
./scripts/deploy-docker.sh --non-interactive --native-arch
```

The deploy script detects `aarch64` and automatically:
- Sets `DOCKER_BUILD_PLATFORM=linux/arm64`
- Skips OrcaSlicer AppImage download
- Excludes slicer services from docker-compose

### Pulling Pre-Built ARM64 Images

If using pre-built images from GHCR (instead of building locally):

```bash
# Multi-arch manifests resolve automatically — same tags for both architectures
docker pull ghcr.io/olyforge3d/printfarmer-api:latest
docker pull ghcr.io/olyforge3d/printfarmer-frontend:latest
docker pull ghcr.io/olyforge3d/printfarmer-printer-discovery:latest
```

Docker automatically pulls the ARM64 variant when running on an ARM64 host.

### Performance Notes

- **First startup** may take 30-60 seconds for database initialization
- **Memory usage**: API typically uses 200-400MB; plan for 1GB+ total with database and frontend
- **Swap**: Enable at least 1GB swap on 4GB Pi devices for headroom
- Consider using an **SSD** instead of SD card for database storage and improved I/O performance

## Next Steps

- **Local Development:** See [LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md) for development setup
- **Contributing:** See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines
- **Advanced Configuration:** See [DOCKER_NETWORK_CONFIG.md](DOCKER_NETWORK_CONFIG.md) for network details
