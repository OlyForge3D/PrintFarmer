# PrintFarmer Deployment Guide: Host-Network + SQL Server

## Overview

This guide provides step-by-step instructions for deploying PrintFarmer with:
- **Architecture**: Host-Network mode (API runs directly on host network)
- **Database**: SQL Server 2022 (Microsoft SQL Server in Docker)
- **Worker**: OrcaSlicer distributed slicing
- **Integration**: Spoolman filament management

This configuration is optimized for **reliability and 100% repeatability**.

---

## Prerequisites

### System Requirements
- Docker & Docker Compose (latest version)
- Linux system (or Docker Desktop on macOS/Windows)
- 4GB RAM minimum (8GB+ recommended)
- Available ports: 1433 (SQL Server), 5245 (API), 8080 (HTTP), 8081 (OrcaSlicer)

### Verify Prerequisites
```bash
# Check Docker installation
docker --version
docker compose version

# Check available ports
lsof -i :1433   # SQL Server
lsof -i :5245   # API
lsof -i :8080   # HTTP
lsof -i :8081   # OrcaSlicer

# If any ports are in use, stop the conflicting service
kill -9 <PID>
```

---

## Deployment Configuration

The `.deploy-config` file contains all settings for your deployment.

### Key Variables Explained

**Database Configuration:**
```bash
DB_PROVIDER=sqlserver                           # Use SQL Server
SQLSERVER_PASSWORD='L0rWItvZR9KLaoYl!'        # SA password (strong password!)
ConnectionStrings__Default="Server=localhost,1433;Database=printfarmer;User Id=sa;Password=L0rWItvZR9KLaoYl!;TrustServerCertificate=True;"
```

**Network Configuration:**
```bash
ARCHITECTURE=host-network                       # API runs on host network
NETWORK_MODE=host                              # Direct network access
API_PORT=5245                                  # API port
NETWORK_RANGES="10.0.0.0/24,10.0.5.0/24"      # Networks to scan for printers
```

**Worker Configuration:**
```bash
ENABLE_ORCA_WORKER=yes                         # Enable OrcaSlicer worker
ORCA_WORKER_COUNT=1                            # Number of workers (1 recommended)
ORCASLICER_VERSION=2.3.1                       # OrcaSlicer version
```

**Integration Configuration:**
```bash
ENABLE_SPOOLMAN=yes                            # Enable Spoolman
SPOOLMAN_BASE_URL=http://10.0.0.70:7912       # Spoolman server address
```

---

## Step-by-Step Deployment

### Step 1: Verify Configuration

```bash
cd /Users/jpapiez/s/PFarm1

# Check current configuration
cat .deploy-config | grep -E "^(ARCHITECTURE|DB_PROVIDER|NETWORK_MODE|ENABLE_ORCA|ENABLE_SPOOLMAN)"

# Expected output:
# ARCHITECTURE=host-network
# DB_PROVIDER=sqlserver
# NETWORK_MODE=host
# ENABLE_ORCA_WORKER=yes
# ENABLE_SPOOLMAN=yes
```

### Step 2: Generate Docker Compose Configuration

```bash
# Option A: Non-interactive (uses .deploy-config exactly)
./scripts/deploy-docker.sh --non-interactive

# Option B: Interactive (uses .deploy-config as defaults, prompts for changes)
./scripts/deploy-docker.sh

# Option C: Dry-run (validates without building)
export DRY_RUN=true
./scripts/deploy-docker.sh --non-interactive
```

### Step 3: Verify Generated Compose File

```bash
# Check that compose file was generated
ls -lh docker-compose.yml

# Validate YAML syntax
docker compose config --quiet

# Should output nothing if valid, or error if problems
```

### Step 4: Build Docker Images

```bash
# Build all images (multistage build handles both API and workers)
docker compose build

# Monitor build progress (takes 5-15 minutes depending on system)
# Look for: "Successfully built" and "Successfully tagged"
```

### Step 5: Start Services

```bash
# Start all services in background
docker compose up -d

# Wait 10 seconds for services to initialize
sleep 10

# Check service status
docker compose ps

# Expected output:
# NAME                            STATUS
# printfarmer-database-sqlserver  Up
# printfarmer-api                 Up
# printfarmer-nginx               Up
# printfarmer-orcaslicer-worker   Up
```

### Step 6: Verify Deployment

```bash
# Check API health
curl http://localhost:5245/healthz
# Expected: {"status":"ok"}

# Check database connectivity
sqlcmd -S localhost,1433 -U sa -P 'L0rWItvZR9KLaoYl!' -Q "SELECT @@VERSION"
# Expected: SQL Server version info (e.g., "Microsoft SQL Server 2022...")

# Check API logs
docker compose logs api | tail -50
# Look for: "Now listening on: http://0.0.0.0:5245"

# Check database logs
docker compose logs database | tail -20
# Look for: "SQL Server is now ready for client connections"
```

### Step 7: Access PrintFarmer

```bash
# Open in browser
open http://localhost:8080

# Or use curl
curl -s http://localhost:8080/ | head -20

# Expected: HTML with "<title>PrintFarmer</title>"
```

---

## Deployment Verification Checklist

### Container Health
```bash
docker compose ps
# ✅ All containers showing "Up"
# ✅ No containers showing "Exited" or "Unhealthy"
```

### Database Connectivity
```bash
sqlcmd -S localhost,1433 -U sa -P 'L0rWItvZR9KLaoYl!' -Q "SELECT COUNT(*) FROM sys.tables"
# ✅ Should return a number (number of system tables)
```

### API Health
```bash
curl -s http://localhost:5245/healthz | jq .
# ✅ Should return: {"status":"ok"}
```

### Network Discovery
```bash
curl -s http://localhost:5245/api/printers | jq .
# ✅ Should return: [] (empty array - no printers connected yet)
```

### Port Verification
```bash
# All ports should be listening
netstat -an | grep LISTEN | grep -E ":(1433|5245|8080|8081)"

# Expected:
# 1433 - SQL Server
# 5245 - API
# 8080 - HTTP/Nginx
# 8081 - OrcaSlicer Worker
```

---

## Troubleshooting

### Problem: YAML Parsing Error (Duplicate "volumes" keys)

**Symptoms:**
```
yaml: unmarshal errors:
  line 148: mapping key "volumes" already defined at line 25
```

**Solution:**
1. Regenerate compose file:
```bash
rm docker-compose.yml
./scripts/deploy-docker.sh --non-interactive
```

2. Verify no special characters in password:
```bash
# Check password contains no unescaped special chars
cat .deploy-config | grep SQLSERVER_PASSWORD
# Should be in single quotes: 'password'
```

3. Validate generated YAML:
```bash
docker compose config --quiet < docker-compose.yml
# Should output nothing if valid
```

### Problem: Database Won't Start

**Symptoms:**
```
docker compose logs database | tail
# Shows: "Failed to initialize database" or "SA password does not meet complexity requirements"
```

**Solution:**
1. Check SQL Server password complexity:
   - At least 8 characters
   - Contains uppercase, lowercase, numbers, and special characters
   - Current password: `L0rWItvZR9KLaoYl!` ✅ Valid

2. Check port availability:
```bash
lsof -i :1433
# If port is in use, kill the process:
kill -9 <PID>
```

3. Regenerate and restart:
```bash
docker compose down -v  # Remove volumes (database data)
rm docker-compose.yml
./scripts/deploy-docker.sh --non-interactive
```

### Problem: API Can't Connect to Database

**Symptoms:**
```
curl http://localhost:5245/healthz
# Returns: Connection refused or timeout
```

**Solution:**
1. Check connection string:
```bash
docker compose exec api env | grep ConnectionStrings__Default
# Should show: Server=localhost,1433;Database=printfarmer;...
```

2. Test database manually:
```bash
sqlcmd -S localhost,1433 -U sa -P 'L0rWItvZR9KLaoYl!'
# Should connect successfully
```

3. Check API logs:
```bash
docker compose logs api --tail=100 | grep -i "connection\|error"
```

### Problem: Port Already in Use

**Symptoms:**
```
docker compose up -d
# Error: bind: address already in use
```

**Solution:**
```bash
# Find process using port
lsof -i :5245   # or :1433, :8080, :8081

# Stop the process
kill -9 <PID>

# Or use a different port (edit .deploy-config):
export API_PORT=5246
./scripts/deploy-docker.sh --non-interactive
```

### Problem: OrcaSlicer Worker Not Starting

**Symptoms:**
```
docker compose ps | grep orcaslicer
# Shows: Exited (1)
```

**Solution:**
```bash
# Check worker logs
docker compose logs orcaslicer-worker --tail=50

# Rebuild worker image
docker compose build orcaslicer-worker

# Restart worker
docker compose up -d orcaslicer-worker
```

---

## Maintenance & Operations

### Viewing Logs

```bash
# All services
docker compose logs -f

# Specific service
docker compose logs -f api
docker compose logs -f database
docker compose logs -f orcaslicer-worker

# Last N lines
docker compose logs --tail=50 api
```

### Stopping Services

```bash
# Stop but keep containers and data
docker compose stop

# Resume
docker compose start
```

### Full Cleanup & Restart

```bash
# Stop containers and remove volumes (DELETES DATABASE!)
docker compose down -v

# Remove generated compose file
rm docker-compose.yml

# Regenerate and restart
./scripts/deploy-docker.sh --non-interactive
```

### Database Backup

```bash
# Backup SQL Server database
docker exec printfarmer-database-sqlserver \
  /opt/mssql-tools/bin/sqlcmd \
  -S localhost \
  -U sa \
  -P 'L0rWItvZR9KLaoYl!' \
  -Q "BACKUP DATABASE [printfarmer] TO DISK='/var/opt/mssql/backup/printfarmer.bak'"

# Copy backup from container
docker cp printfarmer-database-sqlserver:/var/opt/mssql/backup/printfarmer.bak ~/printfarmer-backup.bak
```

### Database Restore

```bash
# Copy backup to container
docker cp ~/printfarmer-backup.bak printfarmer-database-sqlserver:/var/opt/mssql/backup/

# Restore database
docker exec printfarmer-database-sqlserver \
  /opt/mssql-tools/bin/sqlcmd \
  -S localhost \
  -U sa \
  -P 'L0rWItvZR9KLaoYl!' \
  -Q "RESTORE DATABASE [printfarmer] FROM DISK='/var/opt/mssql/backup/printfarmer.bak'"
```

---

## Performance Tuning

### For Multiple OrcaSlicer Workers

If you have multiple printers:
```bash
# Edit .deploy-config
export ORCA_WORKER_COUNT=4    # Create 4 worker instances
./scripts/deploy-docker.sh --non-interactive
```

### For Large Networks

If network discovery is slow:
```bash
# Reduce discovery subnet range in .deploy-config
NETWORK_RANGES="10.0.0.0/25"  # Smaller range = faster discovery

# Or disable discovery and add printers manually
PFARM__NetworkDiscovery__EnableDiscovery=no
```

### Memory Management

```bash
# Check memory usage
docker compose stats

# Limit memory per service (edit compose file)
services:
  database:
    mem_limit: 2g
  api:
    mem_limit: 1g
```

---

## Security Notes

### Password Management
- Current password: `L0rWItvZR9KLaoYl!` (for development only)
- For production, use strong password:
  ```bash
  # Generate secure password
  openssl rand -base64 12
  # Update both .deploy-config and CONNECTION_STRING
  ```

### Network Access
- API accessible at `localhost:5245` (host network mode)
- Firewall should restrict external access
- In production, use reverse proxy with authentication

### Credentials
- Never commit passwords to git
- Use `.deploy-config` with restricted file permissions (600)
- Consider using Docker secrets for production

---

## Support & Debugging

### Detailed Logs

```bash
# Enable debug logging
export ASPNETCORE_ENVIRONMENT=Development
docker compose up -d api

# Verbose compose output
docker compose --verbose up
```

### Health Check Status

```bash
# SQL Server health
docker compose exec database \
  /opt/mssql-tools/bin/sqlcmd \
  -S localhost \
  -U sa \
  -P 'L0rWItvZR9KLaoYl!' \
  -Q "SELECT @@VERSION"

# API health
curl -v http://localhost:5245/health

# OrcaSlicer worker health
curl -v http://localhost:8081/health
```

### Collecting Debug Information

```bash
# Create debug bundle
mkdir -p /tmp/printfarmer-debug
docker compose ps > /tmp/printfarmer-debug/containers.txt
docker compose logs > /tmp/printfarmer-debug/logs.txt
docker compose exec api env > /tmp/printfarmer-debug/api-env.txt
docker exec printfarmer-database-sqlserver sqlcmd -U sa -P 'L0rWItvZR9KLaoYl!' -Q "SELECT * FROM sys.databases" > /tmp/printfarmer-debug/databases.txt

# Collect into tarball
tar -czf printfarmer-debug-$(date +%Y%m%d-%H%M%S).tar.gz -C /tmp printfarmer-debug
```

---

## Next Steps After Deployment

1. **Initial Setup Wizard**: Access http://localhost:8080 to configure administrator account
2. **Network Discovery**: Verify printers are auto-discovered (check Settings → Network)
3. **Add Printers Manually**: Add printers by IP if auto-discovery doesn't find them
4. **Configure Spoolman**: If using Spoolman, connect in Settings → Integrations
5. **Test Print**: Send a test print job to verify end-to-end functionality

---

## Configuration Reference

See `.deploy-config` for all available options and detailed documentation.

For more information, see:
- `QUICK_REFERENCE.md` - Quick deployment reference
- `docs/` - Full documentation
- `scripts/` - Deployment and utility scripts

---

**Configuration Last Updated**: 2025-11-02  
**For Support**: See troubleshooting section above or check logs with `docker compose logs -f`
