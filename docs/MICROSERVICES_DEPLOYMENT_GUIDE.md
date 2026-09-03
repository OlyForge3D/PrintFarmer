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

Calibration project creation and attempt handling (`/api/calibration-projects`,
`/api/calibration-projects/{projectId}/attempts`) resolve the printer's exact machine, process, and
filament profile identifiers in one bounded internal request to `slicer-host`. In a split deployment
the profile store lives behind `slicer-host`, so both the API and slicer host must be configured for
this request.
Configure the profile-resolution hop as follows:

| Service | Setting | Value |
| --- | --- | --- |
| `api` | `SlicerHost__BaseUrl` | `http://slicer-host:5246` (compose default; override with `SLICER_HOST_URL`) |
| `api` + `slicer-host` | `Jwt__Key` | identical, unique, 32+ byte secret in both services |
| `api` + `slicer-host` | `Jwt__Issuer` / `Jwt__Audience` | identical in both services |
| `api` | `ASPNETCORE_ENVIRONMENT` | `Production` |
| `api` | `DEVMODE_BYPASS_AUTH` | `false` |

Optional bounds (`SlicerHost__ResolveTimeoutSeconds`, `SlicerHost__HealthTimeoutSeconds`,
`SlicerHost__MaxResponseBytes`) have safe defaults; an out-of-range or malformed value fails the API
startup rather than degrading silently. Leaving `SlicerHost__BaseUrl` unset keeps profile resolution
fail-closed with `503 profile_service_unavailable`, and `calibrationContextEnabled` stays `false`.

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
   ```
   The capability document never contains the slicer-host address, and neither service logs the
   forwarded token.

### Caller permissions

Calibration project creation and attempt requests require an authenticated JWT carrying
`calibration:read`/`calibration:create` (or the `farm_admin` role). A Desktop API-key exchange token
satisfies this **only when its key was explicitly created with the matching calibration scope and
its owner independently holds the corresponding permission** — the exchange then emits the mapped
permission claim alongside the scope claim (see `docs/SLICER_CONFIGURATION.md`). The slicer host
enforces `calibration:read` identically for normal login/session tokens and Desktop exchange tokens.
Keys created before those scopes existed, and any key without the calibration scope, carry no
permission claims and cannot resolve profiles; those clients must use a normal login/session token or
be reissued a calibration-scoped key.

### Environment correction

Production deployments must run the API with `ASPNETCORE_ENVIRONMENT=Production` and
`DEVMODE_BYPASS_AUTH=false`. Running production on `Development` relaxes JWT signing-key validation
(`AuthenticationStartup.ValidateJwtKey` skips the placeholder and minimum-length checks outside
Production) and, with the dev bypass enabled, allows unauthenticated GET requests. Neither is
acceptable on a production farm.

## G-code artifact staging and promotion

A completed slice produces a staged artifact on slicer-host. Completion does
not automatically import it into the File Library, and no service scans
completed jobs for historical backfill.

- **Preview** and **Download G-code** use the authenticated artifact API while
  the file remains staged.
- **Save to Library** explicitly persists the artifact as a durable
  `GcodeFile`.
- **Print** invokes the same idempotent persistence operation before queueing
  or sending the durable file.

If the operator takes neither action, no File Library entry is created.
Durable `GcodeFile` entries retain the farm-wide visibility model used by the
existing File Library; they are not per-user library partitions.

### Split-host transport

The main API reads pinned artifact bytes from:

```text
http://slicer-host:5246/api/internal/slicer-promotion/artifacts/{artifactId}/content
```

This is private service-network traffic. The route is intentionally absent
from nginx and must not be added to the public proxy. The response contains
only artifact bytes and `Content-Length`; metadata and ownership lineage remain
in the shared database. The API sends only `X-Slicer-Promotion-Key` and the
server-derived operation key. It does not forward the end-user bearer token.

Configure the transport as follows:

| Service | Setting | Value |
| --- | --- | --- |
| `api` | `SlicerHost__BaseUrl` | `http://slicer-host:5246` by default |
| `api` + `slicer-host` | `SlicerPromotion__SharedKey` | Same dedicated secret, supplied by `PROMOTION_SHARED_API_KEY` |
| `api` | `SlicerPromotion__StreamTimeoutSeconds` | `240` by default |
| `slicer-host` | `ArtifactStorage__RootPath` | `/data/artifacts` |

The 240-second stream timeout must remain strictly below nginx's 300-second
`location /api/` read timeout so the API can return a retryable transport error
instead of nginx emitting an opaque 504.

The deployment script generates and preserves `PROMOTION_SHARED_API_KEY`
without printing it. Direct Compose users must generate a strong random secret
and provide the same value to both services. Do not reuse
`WORKER_SHARED_API_KEY`: workers do not need artifact promotion access.

Missing or mismatched promotion configuration fails closed. The API reports
the staged source as unavailable, and slicer-host never returns content without
both the matching shared key and the operation that currently pins the
artifact. To rotate the credential, replace it in the secret manager or
`.env`, then recreate `api` and `slicer-host` together. A temporary mismatch
causes retryable failures rather than exposing bytes.

Slicer-host stages data under `/data/artifacts`, which is covered by its
persistent `/data` mount. Monolith deployments use
`/app/data/artifacts`, covered by the persistent `/app/data` volume. Mounting
slicer-host's artifact directory into the API is not a supported workaround:
it creates shared-volume coupling and bypasses the authenticated,
operation-bound transfer contract.

### Delivery scope

The split-host contract is delivered with the explicit-action workflow:
issue #2401 closes in the same change as #2398. Issue #2402 is closed and is
not planned; this work preserves the current farm-wide File Library model.

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

## Completed Slice Artifacts

After a slice completes, PrintFarmer exposes the resulting G-code through authenticated
Preview and Download actions on the slice job itself. Operators and users do not need to
know the host filesystem path, and none is required to open, inspect, or retrieve the
artifact.

- **All topologies (monolith and microservices).** Preview and Download call the
  authenticated slicer artifact API (`/api/artifacts/...`). In the default microservices
  Docker topology generated by `scripts/deploy-docker.sh`, these requests are proxied by
  nginx to the `slicer-host` service, which streams the artifact bytes to the caller's
  authenticated browser session. Artifacts persist under the slicer's configured data
  directory (`/data/artifacts` in the shipped compose templates). This directory is an
  internal storage location for the slicer service; it is not a user-facing API, and
  operators should not read, move, or delete files there by hand — do so and you will
  break artifact pin/ack and cleanup guarantees.
- **Monolith mode only — automatic G-code library promotion.** When the API and slicer
  module run in a single process (`DEPLOYMENT_MODE=monolith`, which is the default for
  local development and the monolithic Docker image), the hosted
  `SliceLibraryPromotionService` asynchronously and idempotently promotes each completed
  G-code artifact into a durable `GcodeFile` in the File Library, and backfills eligible
  historical jobs on the same cadence. Promotion is safe to run repeatedly: it keys on
  the source artifact ID and skips artifacts that have already been promoted or that a
  prior attempt has terminally failed.
- **Microservices mode — automatic library promotion is intentionally unavailable
  today.** The default `scripts/deploy-docker.sh` topology runs the main API and
  `slicer-host` as separate services. In that split-host configuration the promotion
  service reports its capability as `artifact_source_unroutable` and takes no action:
  the main API cannot read slicer-owned artifact bytes over an authenticated
  service-to-service channel yet. Completed slices remain fully previewable and
  downloadable from the slice job, but they will not appear in the File Library
  automatically. This is tracked by [#2401](https://github.com/OlyForge3D/PrintFarmer/issues/2401),
  which will add the authenticated service-to-service artifact contract required to
  enable promotion across hosts.

Mounting the slicer artifact volume into the `api` service is **not** a supported
workaround — the artifact byte reader is not part of the API's compile-time graph and
loading the slicer plugin into the API would double-run cleanup, timeout, and worker
health services against the shared database. Wait for #2401 rather than sharing state
by hand.

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
