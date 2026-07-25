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
git clone https://github.com/OlyForge3D/PrintFarmer.git
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
- .NET 10.0.101 SDK
- Node.js >=24.13
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
git clone https://github.com/OlyForge3D/PrintFarmer.git
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

### iPhone / iPad HTTPS trust

If you are using local HTTPS for the iOS app, generate or regenerate the certificates with:

```bash
./scripts/generate-certs.sh
```

This now creates a private CA (`ca.cer`) plus the nginx server certificate (`tls.crt` / `tls.key`).

After deployment, users can open:

```text
http://<your-server>/install-ca
```

That page downloads `ca.cer` and walks them through the required iPhone steps:

1. Install the downloaded profile.
2. Open `Settings > General > About > Certificate Trust Settings`.
3. Enable trust for `PrintFarmer Local CA`.

The HTTP install page is intentional: users need the CA installed before Safari and the app will trust your local HTTPS endpoint.

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

The deployment script generates and preserves `DISCOVERY_SHARED_API_KEY` for
the printer-discovery service boundary. Compose injects it as
`DiscoveryAuth__SharedKey` into the API and `Discovery__SharedKey` into the
discovery service. Treat it as a secret, keep it distinct from worker and user
credentials, and rotate both services together. It is accepted only as
`X-Discovery-Service-Key` on internal discovery-event ingestion routes and is
never a user-facing API credential.

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

### Migration-safe upgrades

Automatic provider-aware startup migrations are supported for SQLite,
PostgreSQL, and SQL Server. Core `AppDbContext` and slicer `SlicerDbContext`
use independent provider-specific migration assemblies. Startup and readiness
fail if either context cannot apply or validate its migration set; the service
does not fall back to `EnsureCreated` or continue with a partial schema.

Before upgrading, stop writers and take a provider-native backup:

```bash
# SQLite: stop PrintFarmer before copying the database and sidecar files.
cp farm.db "farm.db.$(date +%Y%m%d%H%M%S).bak"

# PostgreSQL
pg_dump --format=custom --file=printfarmer.backup printfarmer

# SQL Server
sqlcmd -S "$SQLSERVER_HOST" -Q \
  "BACKUP DATABASE [printfarmer] TO DISK = N'/var/opt/mssql/backup/printfarmer.bak' WITH COPY_ONLY"
```

Previously supported databases created without migration history are adopted
only after every expected table and column is validated. The migration history
baseline is recorded transactionally and does not rewrite application data.
A partial legacy schema, missing migration assembly, unsupported provider, or
post-migration validation failure stops startup with one of these stable
diagnostic codes:

- `legacy_schema_incomplete`
- `schema_validation_failed`
- `migration_assembly_missing`
- `provider_unsupported`
- `migration_failed`

Do not manually insert migration-history rows or use `EnsureCreated` to recover.
Preserve the failed database for diagnosis. Correct permissions or deployment
configuration and restart only when the schema is known to match the release;
otherwise restore the provider-native backup and the previous application
version. Rollback is backup restoration, not an automatic down-migration.

Calibration photo bytes are stored outside the database under the private
`Calibration:BlobStorage:RootPath` configuration value. In environment
variables, use `PFARM__Calibration__BlobStorage__RootPath`. Mount this directory
as persistent storage, do not expose it through a static-file server, and back
it up at the same consistency point as the database. Restore the database and
blob root together; the hosted reconciliation service retries pending
two-phase deletes and removes orphaned blobs recorded during failed metadata
writes.

The migration-safe contract in this release does not include MySQL. Do not
upgrade an existing MySQL deployment to this release until a provider-correct
MySQL migration assembly is available.

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

## ARM / Raspberry Pi Deployment

PrintFarmer supports ARM64 platforms (Raspberry Pi 4/5, Orange Pi, etc.) with automatic platform detection and graceful feature degradation.

### How It Works

The installer and deploy scripts detect the CPU architecture at startup. On ARM64:
- **Slicer services** (OrcaSlicer, PrusaSlicer workers) are excluded from Docker Compose
- **3D model file handling** (upload, thumbnails, STL/OBJ/STEP/3MF) is disabled
- **G-code upload, printer management, and all other features** work normally
- The API exposes `GET /api/system/capabilities` so the frontend hides unavailable UI

### Docker Deployment on ARM

```bash
# Auto-detects ARM and configures accordingly
./scripts/deploy-docker.sh --non-interactive
```

The compose generator automatically:
- Excludes `orcaslicer-worker` and `slicer-host` services
- Sets `PFARM__Slicer__Enabled=false` in the API container environment
- Sets `PFARM__Platform__ModelFilesEnabled=false`
- Skips OrcaSlicer AppImage download (no ARM64 build available)

### Bare Metal Deployment on ARM

```bash
# Installer detects ARM and creates appsettings.Platform.json
./install.sh
```

The installer creates an `appsettings.Platform.json` override:
```json
{
  "Slicer": { "Enabled": false },
  "Platform": {
    "ModelFilesEnabled": false,
    "ThumbnailGenerationEnabled": false,
    "Architecture": "arm64"
  }
}
```

### Minimum Hardware Requirements

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| Board | Raspberry Pi 4 | Raspberry Pi 5 |
| RAM | 4 GB | 8 GB |
| Storage | 32 GB microSD | 64 GB+ SSD (USB boot) |
| OS | Raspberry Pi OS 64-bit | Ubuntu Server 24.04 ARM64 |

### Feature Comparison: x86 vs ARM

| Feature | x86/x64 | ARM64 (Pi) |
|---------|---------|------------|
| Printer fleet management | ✅ | ✅ |
| G-code upload & print jobs | ✅ | ✅ |
| Real-time status (SignalR) | ✅ | ✅ |
| Auto-dispatch & bed-clear | ✅ | ✅ |
| Network discovery | ✅ | ✅ |
| Spoolman integration | ✅ | ✅ |
| Analytics & reporting | ✅ | ✅ |
| Multi-database support | ✅ | ✅ |
| 3D model upload (STL/OBJ/3MF) | ✅ | ❌ |
| Slicing (OrcaSlicer/PrusaSlicer) | ✅ | ❌ |
| Model thumbnail generation | ✅ | ❌ |

### Force-Enable (Advanced)

If you've compiled the native libraries (`lib3mf.so`, Assimp) for ARM64 yourself:

```bash
# Docker
PFARM__Platform__ModelFilesEnabled=true \
PFARM__Slicer__Enabled=true \
./scripts/deploy-docker.sh --non-interactive

# Bare metal
PFARM__Platform__ModelFilesEnabled=true \
PFARM__Slicer__Enabled=true \
./install.sh
```

### Checking Platform Capabilities

Query the system capabilities endpoint to verify what's enabled:
```bash
curl -s http://localhost:5245/api/system/capabilities | jq
```

Response on ARM64:
```json
{
  "architecture": "Arm64",
  "slicingEnabled": false,
  "modelFilesEnabled": false,
  "thumbnailGenerationEnabled": false,
  "gcodeUploadEnabled": true,
  "platformNote": "Running on ARM64 — 3D model and slicing features are disabled"
}
```

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
