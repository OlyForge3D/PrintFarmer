# Deploy Script Configuration Persistence & Host Network Fix

## Date: October 6, 2025

## Problems Addressed

### 1. Configuration Not Persisted
**Issue**: Script didn't remember deployment architecture or database provider between runs, forcing users to re-enter the same choices every time.

**Impact**: Poor user experience for iterative deployments

### 2. Host Network Mode Conflicts
**Issue**: Docker Compose error: "service api declares mutually exclusive `network_mode` and `networks`"

**Root Cause**: When using host network mode, Docker Compose was merging multiple files and seeing both `network_mode: host` and `networks: printfarmer-network`, which are mutually exclusive in Docker.

## Solutions Implemented

### 1. Smart Configuration Defaults (scripts/deploy-docker.sh)

**Enhanced `choose_architecture()` function:**
```bash
# Use previous architecture as default, or "1" for new deployments
local default_choice="1"
if [ "${ARCHITECTURE:-}" = "microservices" ]; then
    default_choice="2"
fi

prompt_with_default "Choose architecture [1=Monolithic, 2=Microservices]:" "$default_choice" "ARCH_CHOICE"
```

**Enhanced `configure_database()` function:**
```bash
# Use previous DB provider as default, or "sqlite"/"postgres" for new deployments  
local default_provider="${DB_PROVIDER:-sqlite}"  # monolithic
local default_provider="${DB_PROVIDER:-postgres}"  # microservices

prompt_with_default "Database provider [...]:" "$default_provider" "DB_PROVIDER"
```

**Enhanced `load_previous_config()` to show loaded settings:**
```bash
# Display key settings that will be used as defaults
if [ -n "${ARCHITECTURE:-}" ]; then
    echo -e "  ${BLUE}Architecture:${NC} $ARCHITECTURE"
fi
if [ -n "${DB_PROVIDER:-}" ]; then
    echo -e "  ${BLUE}Database:${NC} $DB_PROVIDER"
fi
if [ -n "${NETWORK_MODE:-}" ]; then
    echo -e "  ${BLUE}Network Mode:${NC} $NETWORK_MODE"
fi
```

### 2. Standalone Host Network Compose File

**Problem**: Cannot override/remove keys in Docker Compose merges

**Solution**: Generate a complete standalone `docker-compose.host-network.yml` that includes ALL services, with API configured for host mode.

**File Structure**:
```yaml
# docker-compose.host-network.yml (generated)
services:
  redis:
    # Normal bridge networking
    networks: [printfarmer-network]
    ports: ["6379:6379"]
  
  database:
    # Normal bridge networking
    networks: [printfarmer-network]
    ports: ["5432:5432"]
  
  api:
    # HOST NETWORK MODE - no ports/networks keys
    network_mode: "host"
    # connects to localhost:6379 and localhost:5432
  
  frontend:
    # Normal bridge networking
    networks: [printfarmer-network]
    
  orcaslicer-worker:
    # Normal bridge networking
    networks: [printfarmer-network]
  
  prusaslicer-worker:
    # Normal bridge networking
    networks: [printfarmer-network]
```

**Deployment Command**:
```bash
# Host mode: Uses standalone file (replaces microservices.yml)
docker compose --env-file .env.microservices \
  -f docker-compose.host-network.yml \
  -f docker-compose.override.yml \
  up -d --build

# Bridge mode: Uses base file
docker compose --env-file .env.microservices \
  -f docker-compose.microservices.yml \
  -f docker-compose.override.yml \
  up -d --build
```

## Testing & Validation

### Configuration Persistence Test
```bash
# First run - user selects microservices + postgres
./scripts/deploy-docker.sh --dry-run

# Second run - defaults show microservices (2) and postgres
./scripts/deploy-docker.sh --dry-run
# Output: "Architecture: microservices", "Database: postgres"
```

### Host Network Mode Test
```bash
# Generate host mode config
printf "2\npostgres\npostgres\nyes\n2\n192.168.0.0/16\n8080\n5245\nProduction\nno\nno\n0\nno\n0\nno\n\n" | \
  ./scripts/deploy-docker.sh --dry-run

# Validate Docker Compose configuration (no conflicts)
docker compose --env-file .env.microservices \
  -f docker-compose.host-network.yml \
  -f docker-compose.override.yml \
  config 2>&1 | grep -i "mutually\|error"

# Result: No errors! ✅
```

## User Experience Improvements

**Before:**
```
Choose architecture [1=Monolithic, 2=Microservices]: 
Default: 1  <-- Always defaulted to 1, even if user previously chose 2

Database provider [postgres/sqlserver/mysql/external]:
Default: postgres  <-- Always defaulted to postgres, even if user previously chose sqlserver
```

**After:**
```
Found previous deployment configuration
✅ Loaded configuration from .deploy-config
  Architecture: microservices
  Database: sqlserver
  Network Mode: host
ℹ️  Previous settings will be used as defaults (press Enter to accept)

Choose architecture [1=Monolithic, 2=Microservices]:
Default: 2  <-- Smart default from saved config!

Database provider [postgres/sqlserver/mysql/external]:
Default: sqlserver  <-- Smart default from saved config!
```

## Key Configuration Files

- `.deploy-config` - Saved deployment settings (persisted)
- `.env.microservices` - Environment variables for microservices
- `docker-compose.host-network.yml` - Standalone compose file for host mode (generated)
- `docker-compose.override.yml` - Database service overrides (generated)

## Benefits

1. **Faster iteration**: Users can press Enter to accept previous choices
2. **Fewer errors**: Eliminates network_mode/networks conflict
3. **Better UX**: Clear display of loaded configuration  
4. **Consistency**: Same deployment configuration across multiple runs
5. **Flexibility**: Users can still override defaults by typing new values

## Related Documentation

- Main deployment docs: `DOCKER_DEPLOYMENT.md`
- Host network mode fix: `MICROSERVICES_HOST_NETWORK_FIX.md`
- Deploy script fixes: `DEPLOY_SCRIPT_FIXES.md`
