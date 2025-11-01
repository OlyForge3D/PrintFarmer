# PrintFarmer - Deployment Guide Overview

This document provides a complete overview of PrintFarmer deployment options and helps you choose the right approach for your needs.

## 🎯 Choose Your Deployment Path

### Quick Decision Matrix

| Use Case | Recommended Approach | Why |
|----------|---------------------|-----|
| **Active Development** | [Local Development](#local-development) | Fast builds, WiFi access, easier debugging |
| **macOS + WiFi Printers** | [Local Development](#local-development) | Docker on macOS can't reach WiFi devices |
| **Production Deployment** | [Docker (Automated)](#docker-automated) | Consistent, scalable, easy maintenance |
| **Team/Staging Environment** | [Docker (Microservices)](#docker-microservices) | Separate services, better monitoring |
| **Quick Testing** | [Docker (Monolithic)](#docker-monolithic) | Single container, minimal setup |

## 🚀 Local Development

**Best for:** Active development, debugging, macOS users with WiFi printers

### Quick Start
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer

# Unified helper script (bootstrap + start)
chmod +x scripts/pf-dev.sh
./scripts/pf-dev.sh bootstrap
./scripts/pf-dev.sh start
```

### Manual Setup
```bash
cd PrintFarmer/src

# Terminal 1 - API Server
dotnet run --project api/Farm.Web.Api.csproj

# Terminal 2 - React Client  
cd Web/ReactApp && npm run dev
```

### Access Points
- **React App**: http://localhost:3000
- **API Server**: http://localhost:5245
- **API Health**: http://localhost:5245/healthz (alias: http://localhost:5245/api/healthz)

### Advantages
✅ **Full WiFi Access** - Can discover printers on WiFi networks  
✅ **Fast Development** - Hot reload, quick builds  
✅ **Easy Debugging** - Native debugging tools  
✅ **No Docker Overhead** - Direct execution on your machine  

### Requirements
- .NET 9.0.302 SDK
- Node.js >=20.19 (recommend v20.19.0)
- 2GB+ RAM

📖 **Detailed Guide**: [LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md)

---

## 🐳 Docker Deployment

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
- **Simpler**: One container to manage
- **Lighter**: Fewer resources required  
- **Faster**: Quick to start and deploy

### Docker (Microservices)

**Best for:** Production, team environments, scalability

```bash  
# Microservices deployment
docker compose --env-file .env.microservices up -d --build
```

**Architecture**: Separate API, Web, Database, Redis containers
- **Scalable**: Independent container scaling
- **Robust**: Better fault isolation
- **Monitoring**: Separate service health checks
- **Optional distributed slicing workers** (OrcaSlicer / PrusaSlicer) with horizontal scaling

### Platform Considerations

| Platform | Network Discovery | Recommendation |
|----------|------------------|----------------|
| **Linux** | ✅ Full support | Docker (any architecture) |
| **Windows** | ✅ Good support | Docker (monolithic recommended) |
| **macOS** | ⚠️ Limited WiFi access | Local development preferred |

📖 **Detailed Guide**: [DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md)

---

## 🔧 Configuration Options

### Database Providers

| Provider | Use Case | Connection Example |
|----------|----------|-------------------|
| **SQLite** | Development, small deployments | `Data Source=/data/farm.db` |
| **PostgreSQL** | Production, high concurrency | `Host=postgres;Database=printfarmer;...` |
| **SQL Server** | Enterprise environments | `Server=sqlserver;Database=printfarmer;...` |
| **MySQL** | Popular open-source option | `Server=mysql;Database=printfarmer;...` |

#### Validation Status (Sept 2025)
All four providers are actively validated via integration tests for the catalog subsystem (manufacturer/model CRUD, normalization, duplicate conflict handling, weak ETag conditional GET). Behavior parity matrix:

| Provider | Status | Notes |
|----------|--------|-------|
| SQLite | ✅ | Baseline & default |
| PostgreSQL | ✅ | Full catalog tests pass |
| MySQL | ✅ | Full catalog tests pass |
| SQL Server | ✅ | Full catalog tests pass (health probe may report unhealthy under amd64 emulation on arm64 hosts) |

During the current soft-freeze the schema is created with `EnsureCreated()` (no migrations). Case-insensitive uniqueness relies on shadow lowercase columns (`NameLowered`) + unique indexes automatically defined per provider. Migrations will be introduced post-freeze.

### Network Discovery

Configure IP ranges to scan for printers:
```bash
ALLOW_LOCAL_NETWORK=true
ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
```

**Common Network Ranges:**
- `192.168.0.0/16` - Home networks (192.168.x.x)
- `10.0.0.0/8` - Corporate networks (10.x.x.x)
- `172.16.0.0/12` - Docker networks (172.16-31.x.x)

### Environment Configuration

Use `.env.template` as a starting point:
```bash
cp .env.template .env.monolithic
# Edit .env.monolithic with your settings
```

### Email (Mailjet) Configuration

PrintFarmer supports transactional email (password reset, email confirmation) via a pluggable provider system. For production you can enable **Mailjet**; for development the `console` provider simply logs email payloads.

#### 1. Choose Provider

Set the provider to `mailjet` (default in production) or keep `console` for non-sending development environments:

```bash
# .env (Docker) or shell export (local)
Email__Provider=mailjet          # mailjet | console
```

#### 2. Obtain Mailjet API Keys

1. Create a free Mailjet account: https://www.mailjet.com/
2. Navigate to: Account > API Keys
3. Copy your public (API Key) and private (Secret Key)

#### 3. Required Environment Variables

Use **double underscores** (`__`) to map hierarchical configuration keys to .NET options (Docker / container friendly):

```bash
Email__Enabled=true
Email__Provider=mailjet
Email__FromAddress=noreply@yourdomain.com
Email__FromName=PrintFarmer
Email__BaseUrl=https://yourdomain.com
Email__Mailjet__ApiKey=YOUR_MAILJET_API_KEY
Email__Mailjet__ApiSecret=YOUR_MAILJET_SECRET_KEY
Email__Mailjet__Sandbox=false        # true = do not actually send (test mode)
```

If you are using Docker Compose, place these in your `.env` file or inline under the `environment:` section for the `api` service:

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

Then supply secrets securely (never commit them):

```bash
export MAILJET_API_KEY="pk_live_xxxxxxxxx"
export MAILJET_API_SECRET="sk_live_yyyyyyyy"
```

#### 4. Production vs Sandbox

| Mode | Setting | Behavior |
|------|---------|----------|
| Sandbox | `Email__Mailjet__Sandbox=true` | Mailjet accepts payload but does not deliver emails |
| Live | `Email__Mailjet__Sandbox=false` | Emails are sent to recipients |

Always disable sandbox (`false`) once you are ready for real user notifications.

#### 5. Verifying Configuration

After deploying with Mailjet enabled:

```bash
# Trigger a password reset (does not reveal user existence)
curl -X POST "https://yourdomain.com/api/auth/forgot-password" \
	-H 'Content-Type: application/json' \
	-d '{"email":"testuser@yourdomain.com"}'

# Check API logs for dispatch
docker compose logs -f api | grep EMAIL
```

On success you should see a log line similar to:
```
Mailjet email sent to testuser@yourdomain.com. Status=200
```

If keys are missing you will see a fallback log entry:
```
Mailjet API keys missing. Email logged only.
```

#### 6. Common Pitfalls

| Issue | Symptom | Fix |
|-------|---------|-----|
| Missing keys | Fallback logging only | Set `Email__Mailjet__ApiKey` / `Email__Mailjet__ApiSecret` |
| Wrong base URL | Broken links in emails | Set `Email__BaseUrl` to public HTTPS origin |
| Sandbox left on | No real emails sent | Set `Email__Mailjet__Sandbox=false` |
| Provider still console | Emails not delivered | Set `Email__Provider=mailjet` |

#### 7. Security Recommendations

* Use secrets manager or Docker Swarm/K8s secret injection for API keys (avoid plain `.env` in production).
* Rotate Mailjet keys periodically (every 90 days).
* Monitor send rate & failures in Mailjet dashboard for early detection of abuse.
* Consider SPF/DKIM alignment for `FromAddress` domain to reduce spam filtering.

#### 8. Quick Local Test (Console Provider)

Leave provider as `console` locally:
```bash
Email__Provider=console
```
Trigger a password reset and observe a structured log containing the email body (no external call). This speeds up development without consuming Mailjet quota.

---

### Distributed Slicing Flags

Add these to enable slicer workers (microservices or monolithic with profiles):
```bash
ENABLE_DISTRIBUTED_SLICING=true
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
ENABLE_PRUSA_WORKER=no
PRUSA_WORKER_COUNT=0
```

Scale later:
```bash
docker compose up -d --scale orcaslicer-worker=3
docker compose up -d --scale prusaslicer-worker=2
```

Disable entirely:
```bash
ENABLE_DISTRIBUTED_SLICING=false
```

Pause slicer builds (new):

If you want to pause any automatic slicer builds and prevent the deploy scripts/CI from building or starting Orca/Prusa workers, set:

```bash
DISABLE_SLICER_BUILDS=true
```

When this is set the deploy script will force-disable worker flags and set worker counts to 0. To re-enable, set `DISABLE_SLICER_BUILDS=false` and then configure `ENABLE_ORCA_WORKER` / `ENABLE_PRUSA_WORKER` with desired counts.

---
## 📚 Catalog Normalization & Duplicate Handling (Deployment Notes)

The API enforces canonical normalization for Manufacturer and Printer Model names. Each create/update response includes `X-Normalized-Name` so external systems (or the React frontend) can reconcile user-entered values with the server’s canonical form.

Deployment operators should be aware:
* Duplicate submissions differing only by case or whitespace return `409 Conflict` with ProblemDetails; the header still supplies the canonical name.
* Case-insensitive uniqueness is enforced at two layers: in-memory pre-check (human friendly error) and DB unique index on a shadow lowered column for durability.
* List endpoints (`/api/catalog/manufacturers`, `/api/catalog/models`) emit weak ETags and honor `If-None-Match`—reverse proxies/CDNs can leverage this to reduce chatter.
* No migrations are used during soft-freeze; indexes + shadow columns are created dynamically. Ensure your deployment platform does not attempt to run `dotnet ef database update` (unnecessary until migrations ship).

Operational Tips:
* Scale-out: Because normalization + duplicate detection are pure application + DB uniqueness operations, horizontal API scaling is safe (no in-memory global locks required). The rare race between concurrent identical inserts falls back cleanly to DB unique constraint handling.
* Monitoring: A spike in 409 responses on catalog endpoints may indicate a UI or integration repeatedly retrying with unnormalized values.
* Future Migration Phase: When migrations are introduced, a no-op transition script will preserve existing `NameLowered` columns and indexes—no manual action expected.

---

## 🎛️ Management & Operations

### Health Monitoring
```bash
# Basic health check (either path works)
curl http://localhost:8080/healthz
# or
curl http://localhost:8080/api/healthz

# Comprehensive health status (either path works)
curl http://localhost:8080/health | jq '.'
# or
curl http://localhost:8080/api/health | jq '.'
```

### Log Management
```bash
# Docker logs
docker compose logs -f api

# Local development
# Logs appear in terminal output
```

### Updates & Maintenance
```bash
# Docker update
git pull origin main
docker compose up -d --build

# Local development update  
git pull origin main
cd src && dotnet build ./farm-web.sln
```

---

## 🔍 Troubleshooting Quick Reference

### Common Issues

**"External service unavailable"**
```bash
# Check if API is running
curl http://localhost:5245/healthz  # Local
curl http://localhost:8080/healthz  # Docker
```

**Network discovery not finding printers**  
```bash
# Test direct printer access
curl http://YOUR_PRINTER_IP:7125/printer/info

# Check network configuration
# macOS + Docker: Use local development instead
```

**Build failures**
```bash
# Check .NET version
dotnet --info  # Should show 9.0.x

# Clean rebuild
cd src
dotnet clean && dotnet build ./farm-web.sln
```

### Platform-Specific

**macOS Users:**
- Use local development for WiFi printer access
- Docker Desktop has WiFi networking limitations
- Consider local development for active development

**Linux Users:**
- Docker provides full networking capabilities
- Can use enhanced networking features
- Best platform for Docker deployment

**Windows Users:**
- Good Docker compatibility
- Linux containers recommended over Windows containers
- WSL2 provides better Docker performance

---

## 📊 Performance & Scaling

### Resource Requirements

| Deployment | CPU | RAM | Storage | Network |
|------------|-----|-----|---------|---------|
| **Local Dev** | 2+ cores | 2GB+ | 1GB+ | WiFi/Ethernet |
| **Docker Mono** | 2+ cores | 4GB+ | 5GB+ | Ethernet preferred |
| **Docker Micro** | 4+ cores | 8GB+ | 10GB+ | Ethernet/WiFi |

### Scaling Considerations

**Local Development:**
- Single instance only
- Good for 1-10 printers
- Limited by machine resources

**Docker Monolithic:**
- Single container scaling
- Good for 10-50 printers  
- Easier management

**Docker Microservices:**
- Independent service scaling
- Good for 50+ printers
- Can scale API and Web separately

---

## 📚 Additional Resources

### Documentation
- **[LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md)** - Complete local setup guide
- **[DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md)** - Complete Docker guide  
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Development guidelines

### Scripts
- **`scripts/pf-dev.sh`** - Unified local dev helper (bootstrap/start/stop/status/logs/test)
- **`scripts/deploy-docker.sh`** - Automated Docker deployment
- **`test-providers.sh`** - Test database providers

### Configuration Files
- **`.env.template`** - Environment configuration template
- **`docker-compose.yml`** - Docker Compose configuration
- **`global.json`** - .NET SDK version requirements

---

## 🎉 Getting Started

### For Developers
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
./scripts/pf-dev.sh bootstrap && ./scripts/pf-dev.sh start
```

### For Production
```bash  
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
./scripts/deploy-docker.sh
```

### Need Help?
1. Check the appropriate detailed guide (LOCAL_DEVELOPMENT.md or DOCKER_DEPLOYMENT.md)
2. Review troubleshooting sections
3. Check GitHub Issues for known problems
4. Create a new issue with detailed error information

---

**Happy printing! 🖨️🎉**
