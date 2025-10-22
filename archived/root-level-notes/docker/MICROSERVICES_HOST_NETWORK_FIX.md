# Microservices Host Network Mode Fix

## Problem
The deploy-docker script was forcing bridge mode for non-Linux systems (macOS, Windows), even when users wanted to generate configuration for deployment to a Linux server.

## Root Cause
The script detected the **local OS** (macOS) and assumed the containers would run on that same machine, so it prevented host network mode selection since host networking only works on Linux.

## Solution
Added a deployment target detection:
- Script now asks: **"Are you deploying to a Linux server (not this machine)?"**
- If **yes**: Allows host network mode selection even from macOS/Windows
- If **no**: Forces bridge mode for local non-Linux deployment

## Changes Made

### 1. Improved Network Configuration Flow
**File**: `scripts/deploy-docker.sh` (lines ~665-750)

**New flow:**
1. **Ask about network mode FIRST** (host vs bridge)
2. **If host mode**: Auto-enable discovery, ask for network ranges
3. **If bridge mode**: Ask if user wants discovery, then ask for ranges

**Before:**
```
1. Ask: Enable discovery?
2. If yes → Ask: Network ranges?
3. If yes → Ask: Network mode?
```

**After:**
```
1. Ask: Network mode (host or bridge)?
2. If host → Auto-enable discovery, ask for ranges
3. If bridge → Ask: Enable discovery? (then ranges if yes)
```

**Rationale:** Host networking inherently provides full network access (broadcast/multicast), so discovery should always be enabled. Only bridge mode needs the discovery question since it has limited network access.

### 2. Added Linux Deployment Target Prompt
**File**: `scripts/deploy-docker.sh` (lines ~699-745)

```bash
if [ "$OS" != "linux" ]; then
    print_warning "Host network mode only works on Linux."
    print_warning "Current OS: $OS (detected)"
    echo
    prompt_yes_no "Are you deploying to a Linux server (not this machine)?" "no" "DEPLOYING_TO_LINUX"
    
    if [ "$DEPLOYING_TO_LINUX" = "yes" ]; then
        # Allow host mode selection for Linux target
        # ... (host mode configuration)
    else
        # Force bridge mode for local deployment
        NETWORK_MODE="bridge"
    fi
fi
```

### 2. Added Linux Deployment Target Prompt
**File**: `scripts/deploy-docker.sh` (lines ~699-745)

```bash
if [ "$OS" != "linux" ]; then
    print_warning "Host network mode only works on Linux."
    print_warning "Current OS: $OS (detected)"
    echo
    prompt_yes_no "Are you deploying to a Linux server (not this machine)?" "no" "DEPLOYING_TO_LINUX"
    
    if [ "$DEPLOYING_TO_LINUX" = "yes" ]; then
        # Allow host mode selection for Linux target
        # ... (host mode configuration)
    else
        # Force bridge mode for local deployment
        NETWORK_MODE="bridge"
    fi
fi
```

### 3. Fixed Empty Array Error
**File**: `scripts/deploy-docker.sh` (lines ~1118-1136)

Fixed "unbound variable" error when `profiles_to_enable` array is empty (no workers enabled):

```bash
# Before:
if "$compose_cmd[@]" up -d "${profiles_to_enable[@]}"; then

# After:
if [ ${#profiles_to_enable[@]} -gt 0 ]; then
    "$compose_cmd[@]" up -d "${profiles_to_enable[@]}"
else
    "$compose_cmd[@]" up -d
fi
```

## Generated Files for Host Network Mode

### 1. `.env.microservices`
```bash
NETWORK_MODE=host
DOCKER_HOST_NETWORK=true
API_PORT=5245
HTTP_PORT=8080
```

### 2. `docker-compose.host-network.yml` (Override)
```yaml
services:
  api:
    network_mode: "host"
    ports: []              # Removed - conflicts with host mode
    networks: []           # Removed - not compatible with host mode
    environment:
      - ASPNETCORE_URLS=http://0.0.0.0:5245
      - ConnectionStrings__Redis=localhost:6379  # Host mode: Redis on localhost
      - DOCKER_HOST_NETWORK=true
      - NETWORK_MODE=host
```

## Testing

### Dry-Run Test (Microservices + Host Network)
```bash
# Clean previous config
rm -f .deploy-config .env.microservices docker-compose.override.yml docker-compose.host-network.yml

# Run deployment with inputs:
# 2 = Microservices
# postgres = DB provider
# postgres = DB password
# yes = Enable discovery
# 192.168.0.0/16 = Network ranges
# yes = Deploying to Linux server
# 2 = Host network mode
# 8080 = HTTP port
# 5245 = API port
# Production = Environment
printf "2\npostgres\npostgres\nyes\n192.168.0.0/16\nyes\n2\n8080\n5245\nProduction\nno\nno\n0\nno\n0\nno\n\n" | \
  ./scripts/deploy-docker.sh --dry-run
```

**Result**: ✅ Setup completed successfully!

### Verification
```bash
# Check environment file
grep "NETWORK_MODE" .env.microservices
# Output: NETWORK_MODE=host

# Check host-network override exists
ls docker-compose.host-network.yml
# Output: docker-compose.host-network.yml (exists)

# Verify network_mode and networks don't conflict
grep -E "network_mode|networks:" docker-compose.host-network.yml
# Output shows network_mode: "host" and networks: [] (empty, no conflict)
```

## Usage Workflows

### Deploying from macOS to Linux Server

**Option 1: Generate config on macOS, deploy on Linux**
```bash
# On macOS - Generate configuration
./scripts/deploy-docker.sh --dry-run
# Answer "yes" to "deploying to Linux server"
# Choose host network mode

# Copy files to Linux server
scp .env.microservices docker-compose.*.yml user@linux-server:~/printfarmer/
scp -r . user@linux-server:~/printfarmer/  # Or git clone on server

# On Linux server - Deploy
docker compose --env-file .env.microservices \
  -f docker-compose.microservices.yml \
  -f docker-compose.override.yml \
  -f docker-compose.host-network.yml \
  up -d --build
```

**Option 2: Run deployment script directly on Linux server** (Recommended)
```bash
# SSH to Linux server
ssh user@linux-server

# Clone repo or copy deployment script
git clone <repo> && cd printfarmer

# Run deployment script on Linux
./scripts/deploy-docker.sh
# Select microservices, host mode (no prompt about target OS)
```

## Network Mode Comparison

| Mode | Works On | Discovery | Docker Networks | Use Case |
|------|----------|-----------|-----------------|----------|
| **Bridge** | All platforms | Limited (known IPs only) | ✅ Uses Docker networks | Local dev, known printer IPs |
| **Host** | Linux only | Full (broadcast/multicast) | ❌ Direct host network | Production, auto-discovery |

## Key Points

1. **Host networking = Linux only**: Docker's host mode networking feature only works on Linux hosts
2. **macOS limitation**: macOS Docker Desktop uses a VM, so host mode isn't available
3. **Cross-platform deployment**: Can generate Linux configs from macOS, but must deploy on Linux
4. **Mutual exclusivity**: Cannot use both `network_mode: host` and `networks:` in same service
5. **Override file**: Host mode settings applied via `docker-compose.host-network.yml` override

## Related Issues Fixed
- ✅ "network_mode and networks are mutually exclusive" error
- ✅ Script forcing bridge mode for remote Linux deployments
- ✅ Empty profiles array causing "unbound variable" error
- ✅ DB_PASSWORD unbound variable (previous fix)
- ✅ .NET SDK check only for monolithic (previous fix)

## Documentation
- Main deployment docs: `DOCKER_DEPLOYMENT.md`
- Deploy script fixes: `DEPLOY_SCRIPT_FIXES.md`
- Docker build fixes: `DOCKER_BUILD_FIX.md`
