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
│  │ Port 80/8080     │  │  MSSQL)      │  │ (Optional)  │  │
│  │                  │  │ Port 5432/   │  │ Port 8081   │  │
│  │ Proxies to:      │  │ 1433         │  │             │  │
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
- **Database**: PostgreSQL (recommended) or SQL Server
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
./scripts/deploy-docker.sh --dry-run
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

5. **API → Slicer Host (calibration profile resolution)**:
   ```
   api → http://slicer-host:5246/api/slicer/calibration/resolved-profiles
   api → http://slicer-host:5246/healthz/calibration-resolver
   ```
   - In split/microservices mode the API does **not** load the slicer module, so it has no
     in-process calibration profile store. It resolves the three explicitly selected
     machine/process/filament profiles over this authenticated internal hop instead.
   - The API forwards the **end user's own bearer token**; the slicer host validates it, requires
     `calibration:read`, and derives ownership scope (including the audited farm-admin bypass)
     from that token. No service-to-service credential is minted and the caller can never supply
     a user id or ownership bypass.
   - The availability probe carries no end-user token and returns no profile data. It is the only
     thing `calibrationContextEnabled` trusts, so an unreachable slicer host fails closed.
   - Configured by `SlicerHost__BaseUrl` on the `api` service (see below).

## Calibration Profile Resolution (split deployments)

`GET /api/printers/calibration-candidates` lists enabled printers from API-owned status, firmware,
adapter, geometry, slicer identity, and hardware metadata. It does not contact the profile store, so
operators can still choose a printer while `slicer-host` is unavailable.

After selection,
`GET /api/printers/{id}/calibration-context?slicerType=OrcaSlicer` resolves that printer's exact
machine, process, and filament profile identifiers in one bounded request. In a split deployment the
profile store lives behind `slicer-host`, so both the API and slicer host must be configured for this
selected-context hop:

| Service | Setting | Value |
| --- | --- | --- |
| `api` | `SlicerHost__BaseUrl` | `http://slicer-host:5246` (compose default; override with `SLICER_HOST_URL`) |
| `api` + `slicer-host` | `Jwt__Key` | identical, unique, 32+ byte secret in both services |
| `api` + `slicer-host` | `Jwt__Issuer` / `Jwt__Audience` | identical in both services |
| `api` | `ASPNETCORE_ENVIRONMENT` | `Production` |
| `api` | `DEVMODE_BYPASS_AUTH` | `false` |

Optional bounds (`SlicerHost__ResolveTimeoutSeconds`, `SlicerHost__HealthTimeoutSeconds`,
`SlicerHost__MaxResponseBytes`) have safe defaults; an out-of-range or malformed value fails the API
startup rather than degrading silently. Leaving `SlicerHost__BaseUrl` unset keeps selected-context
resolution fail-closed with `503 profile_service_unavailable`, while candidate listing remains
available and `calibrationContextEnabled` stays `false`.

### Rollout

1. Apply database migrations for both the core and slicer schemas as usual for the release.
2. Set the values in the table above in `.env` (or your secret manager). `Jwt__Key` **must** be the
   same string in both services — a mismatch makes the slicer host reject every forwarded token and
   calibration stays unavailable.
3. Restart both services together so the API picks up the resolver configuration and the slicer host
   exposes the resolution routes:
   ```bash
   docker compose up -d --force-recreate api slicer-host
   ```
4. Verify without leaking secrets:
   ```bash
   # From the API container: resolver availability (no end-user token needed).
   docker exec printfarmer-api curl -fsS http://slicer-host:5246/healthz/calibration-resolver
   # Expect: Healthy

   # Public capability document must now report the context feature as operational.
   curl -fsS http://<host>:5245/api/system/capabilities | jq '.deploymentMode, .calibrationContextEnabled'
   # Expect: "split" and true

   # Authenticated discovery (JWT must carry calibration:read).
   curl -fsS -H "Authorization: Bearer <session-jwt>" \
     http://<host>:5245/api/printers/calibration-candidates | jq 'length'
   ```
   The capability document never contains the slicer-host address, and neither service logs the
   forwarded token.

### Caller permissions

Candidate and context requests require an authenticated JWT carrying `calibration:read` (or the
`farm_admin` role). A Desktop API-key exchange token satisfies this **only when its key was
explicitly created with the `CalibrationRead` scope and its owner independently holds
`calibration:read`** — the exchange then emits the mapped permission claim alongside the scope
claim (see `docs/SLICER_CONFIGURATION.md`). The slicer host enforces `calibration:read` identically
for normal login/session tokens and Desktop exchange tokens. Keys created before those scopes
existed, and any key without `CalibrationRead`, carry no permission claims and cannot read
calibration candidates; those clients must use a normal login/session token or be reissued a
calibration-scoped key.

### Environment correction

Production deployments must run the API with `ASPNETCORE_ENVIRONMENT=Production` and
`DEVMODE_BYPASS_AUTH=false`. Running production on `Development` relaxes JWT signing-key validation
(`AuthenticationStartup.ValidateJwtKey` skips the placeholder and minimum-length checks outside
Production) and, with the dev bypass enabled, allows unauthenticated GET requests. Neither is
acceptable on a production farm.

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

MySQL is not supported in this release because migration-safe application and
slicer migration assemblies are unavailable.

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
   # Use the current provider's native backup tooling.
   ```

2. **Tear down monolithic deployment**:
   ```bash
   docker compose down --volumes
   ```

3. **Deploy microservices**:
   ```bash
   ./scripts/deploy-docker.sh
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
- [Development Guide](DEVELOPMENT.md#running-locally) - Local development setup
- [SECURITY.md](../SECURITY.md) - Security hardening guide
- [DEPLOYMENT_TESTING_CHECKLIST.md](DEPLOYMENT_TESTING_CHECKLIST.md) - Deployment validation
