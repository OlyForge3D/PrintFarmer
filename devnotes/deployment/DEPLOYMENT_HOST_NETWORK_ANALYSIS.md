# Host Network & CORS Configuration Analysis

**Date:** October 6, 2025  
**Issue:** Deployment script doesn't configure host networking for API server  
**Impact:** Network discovery may be limited, requires manual configuration

---

## Executive Summary

❌ **Current Gap:** The `deploy-docker.sh` script does NOT configure `network_mode: host` for the API server  
❌ **CORS Configuration:** The script does NOT dynamically adjust CORS origins based on ports  
⚠️ **Workaround Available:** Manual docker-compose modification works  
✅ **Default Works:** Bridge networking with `extra_hosts` works for basic scenarios

---

## Current Network Configuration

### Monolithic Deployment (`docker-compose.yml`)

**API Container:**
```yaml
api:
  ports:
    - "5001:5001"  # Port mapping (bridge network)
  environment:
    - CORS__AllowedOrigins=http://localhost:3000,http://localhost:8080,http://localhost:5001
    - ALLOW_LOCAL_NETWORK=true
    - ALLOWED_NETWORK_RANGES=10.0.0.0/24,172.16.0.0/12
    - DOCKER_HOST_NETWORK=false  # ← Indicates bridge mode
    - ENABLE_LOCAL_NETWORK_ACCESS=true
```

**Network Mode:** Bridge (default)  
**Local Network Access:** Via `ALLOWED_NETWORK_RANGES` filtering

### Microservices Deployment (`docker-compose.microservices.yml`)

**API Container:**
```yaml
api:
  ports:
    - "5001:8080"  # Port mapping (bridge network)
  networks:
    - printfarmer-network
  environment:
    - CORS__AllowedOrigins=http://localhost:3000,http://localhost:8080
    - DEPLOYMENT_MODE=microservices
```

**Network Mode:** Bridge (user-defined network)  
**No Local Network Configuration!**

---

## What's Missing

### 1. Host Network Mode Support ❌

**For optimal network discovery, the API should support:**

```yaml
# Option A: Full host networking
api:
  network_mode: "host"
  environment:
    - ASPNETCORE_URLS=http://0.0.0.0:5245  # Must use configured port
    - CORS__AllowedOrigins=http://localhost:3000,http://localhost:8080
    - DOCKER_HOST_NETWORK=true
```

**Benefits:**
- ✅ Direct access to local network interfaces
- ✅ Better network discovery (can see broadcast/multicast traffic)
- ✅ No NAT overhead
- ✅ Simplified printer IP detection

**Limitations:**
- ⚠️ Only works on Linux hosts (not Docker Desktop for Mac/Windows)
- ⚠️ Port conflicts with host services
- ⚠️ Less container isolation
- ⚠️ Cannot use `ports:` mapping (conflicts with `network_mode: host`)

### 2. Dynamic CORS Configuration ❌

**Current hardcoded CORS:**
```yaml
CORS__AllowedOrigins=http://localhost:3000,http://localhost:8080,http://localhost:5001
```

**Problem:** If user changes `HTTP_PORT` to 9000, CORS still only allows 8080!

**Should be:**
```yaml
CORS__AllowedOrigins=http://localhost:3000,http://localhost:${HTTP_PORT},http://localhost:${API_PORT}
```

### 3. Network Mode Selection in Script ❌

**Script should ask:**
```
Network Configuration:
1. Bridge mode (default) - Works cross-platform, safer
2. Host mode (advanced) - Best for network discovery, Linux only

Choice [1]:
```

---

## Impact Analysis

### Network Discovery Performance

| Mode | Broadcast | Multicast | Direct TCP | Platform Support |
|------|-----------|-----------|------------|------------------|
| **Bridge** | ❌ Limited | ❌ Limited | ✅ Yes | ✅ All platforms |
| **Bridge + extra_hosts** | ❌ Limited | ❌ Limited | ✅ Yes | ✅ All platforms |
| **Host** | ✅ Full | ✅ Full | ✅ Yes | ⚠️ Linux only |

**Current Status:** Bridge mode works for **known IP addresses** but may miss:
- mDNS/Bonjour printer announcements
- SSDP/UPnP discovery
- Broadcast-based protocols

### CORS Impact

**Scenario:** User configures `HTTP_PORT=9000`

**What happens:**
1. ✅ Deployment script generates `.env.microservices` with `HTTP_PORT=9000`
2. ✅ Docker Compose exposes port 9000
3. ❌ CORS still configured for `http://localhost:8080`
4. ❌ Browser requests from `http://localhost:9000` → **BLOCKED by CORS!**

**Result:** User sees CORS errors in browser console, API calls fail.

---

## Recommended Solutions

### Solution 1: Add Host Network Mode Option (Recommended)

**Update `deploy-docker.sh`:**

```bash
# In configure_networking() function
configure_networking() {
    print_header "🌐 Network Configuration"
    
    # ... existing discovery prompts ...
    
    echo
    echo -e "${BLUE}Network Mode for API Container:${NC}"
    echo "1. Bridge (default) - Works on all platforms, requires port mapping"
    echo "2. Host (advanced) - Direct host network access, Linux only"
    echo
    
    if [ "$OS" != "linux" ]; then
        print_warning "Host network mode only works on Linux. Forcing bridge mode."
        NETWORK_MODE="bridge"
    else
        prompt_with_default "Network mode [1=Bridge, 2=Host]:" "1" "NETWORK_CHOICE"
        
        case "$NETWORK_CHOICE" in
            2|host)
                NETWORK_MODE="host"
                print_warning "Host mode: API will use host network directly"
                print_warning "Make sure port ${API_PORT:-5245} is available on host"
                ;;
            *)
                NETWORK_MODE="bridge"
                print_success "Using bridge mode (cross-platform compatible)"
                ;;
        esac
    fi
}
```

**Update environment file generation:**

```bash
generate_env_file() {
    # ... existing code ...
    
    cat >> "$ENV_FILE" << EOF

# Network Configuration
NETWORK_MODE=$NETWORK_MODE
DOCKER_HOST_NETWORK=$([ "$NETWORK_MODE" = "host" ] && echo "true" || echo "false")
API_URL=$([ "$NETWORK_MODE" = "host" ] && echo "http://localhost:${API_PORT}" || echo "http://localhost:${HTTP_PORT}")
EOF
}
```

**Update docker-compose files to support variable:**

```yaml
# docker-compose.microservices.yml
api:
  network_mode: "${NETWORK_MODE:-bridge}"
  # Conditional ports (only if bridge mode)
  ports: !reset []
  # Use conditional logic in entrypoint or startup script
```

**Note:** Docker Compose doesn't support conditional `ports:` well. Better approach is to generate separate override files.

### Solution 2: Fix CORS Dynamic Origins (Quick Win) ✅

**Update `generate_env_file()` in deploy-docker.sh:**

```bash
generate_env_file() {
    # ... existing code ...
    
    # Dynamic CORS origins based on configured ports
    CORS_ORIGINS="http://localhost:3000"
    
    if [ "$ARCHITECTURE" = "microservices" ]; then
        CORS_ORIGINS="${CORS_ORIGINS},http://localhost:${HTTP_PORT},http://localhost:${API_PORT}"
    else
        CORS_ORIGINS="${CORS_ORIGINS},http://localhost:${HTTP_PORT}"
    fi
    
    cat >> "$ENV_FILE" << EOF

# CORS Configuration
CORS__AllowedOrigins=$CORS_ORIGINS
EOF
}
```

**Update docker-compose files:**

```yaml
# docker-compose.yml
api:
  environment:
    - CORS__AllowedOrigins=${CORS__AllowedOrigins}
```

```yaml
# docker-compose.microservices.yml
api:
  environment:
    - CORS__AllowedOrigins=${CORS__AllowedOrigins}
```

### Solution 3: Document Manual Host Network Configuration (Immediate)

**Create `/docs/MANUAL_HOST_NETWORK_SETUP.md`:**

```markdown
# Manual Host Network Setup

For advanced users who need optimal network discovery on Linux.

## Prerequisites
- Linux host (not Docker Desktop for Mac/Windows)
- Port 5245 available on host (or your chosen API_PORT)

## Steps

1. Run deployment script normally
2. After deployment, edit the generated compose file:

### For Microservices:
\`\`\`bash
# Edit docker-compose.microservices.yml
nano docker-compose.microservices.yml

# Change:
api:
  ports:
    - "5001:8080"
  networks:
    - printfarmer-network

# To:
api:
  network_mode: "host"
  # Remove ports: and networks: sections
  environment:
    - ASPNETCORE_URLS=http://0.0.0.0:5245  # Your API_PORT
    - DOCKER_HOST_NETWORK=true
\`\`\`

3. Restart containers:
\`\`\`bash
docker compose --env-file .env.microservices down
docker compose --env-file .env.microservices up -d
\`\`\`

4. Verify:
\`\`\`bash
curl http://localhost:5245/healthz
\`\`\`
```

---

## Recommended Implementation Priority

### Phase 1: Immediate (Can Deploy Now) ✅
1. ✅ **Document manual host network setup** - Workaround for advanced users
2. ✅ **Fix CORS dynamic origins** - Prevents CORS errors with custom ports
3. ✅ **Add warning in script** - Inform users about network mode limitations

### Phase 2: Enhancement (Next Sprint) 🔧
1. 🔧 **Add network mode selection** - Let users choose bridge vs host
2. 🔧 **Generate conditional overrides** - Auto-create host network config
3. 🔧 **Validate Linux requirement** - Prevent host mode on non-Linux

### Phase 3: Advanced (Future) 🚀
1. 🚀 **Auto-detect network mode need** - Based on OS and discovery requirements
2. 🚀 **Hybrid network configuration** - Host for API, bridge for workers
3. 🚀 **Network performance testing** - Recommend optimal mode

---

## Current Workaround

**For users who need host networking NOW:**

```bash
# 1. Run deployment script
./scripts/deploy-docker.sh

# 2. Stop containers
docker compose --env-file .env.microservices down

# 3. Create override file
cat > docker-compose.host-network.yml << 'EOF'
version: '3.8'

services:
  api:
    network_mode: "host"
    ports: !reset []
    environment:
      - ASPNETCORE_URLS=http://0.0.0.0:5245
      - DOCKER_HOST_NETWORK=true
      - CORS__AllowedOrigins=http://localhost:3000,http://localhost:8080,http://localhost:5245
EOF

# 4. Start with override
docker compose --env-file .env.microservices \\
  -f docker-compose.microservices.yml \\
  -f docker-compose.host-network.yml \\
  up -d
```

---

## Testing Scenarios

### Test 1: Bridge Mode (Current Default)
```bash
# Deploy with default settings
./scripts/deploy-docker.sh
# Select: Microservices, PostgreSQL, Enable Discovery

# Verify network discovery works for known IPs
curl -X POST http://localhost:8080/api/printers/discover-streaming \\
  -H "Content-Type: application/json" \\
  -d '{"ipRanges": ["192.168.1.100/32"]}'

# Expected: Should discover printers at known addresses
```

### Test 2: Host Mode (Manual Configuration)
```bash
# Apply host network override (Linux only)
docker compose down
docker compose -f docker-compose.microservices.yml \\
  -f docker-compose.host-network.yml up -d

# Verify direct host network access
curl http://localhost:5245/healthz

# Test network discovery with broader scan
curl -X POST http://localhost:5245/api/printers/discover-streaming \\
  -H "Content-Type: application/json" \\
  -d '{"ipRanges": ["192.168.1.0/24"]}'

# Expected: Should discover ALL printers on subnet
```

### Test 3: CORS with Custom Port
```bash
# Deploy with custom HTTP port
export HTTP_PORT=9000
./scripts/deploy-docker.sh --non-interactive

# Verify CORS allows custom port
curl -v -H "Origin: http://localhost:9000" \\
  http://localhost:9000/api/printers

# Expected: Should see Access-Control-Allow-Origin header
```

---

## Documentation Updates Needed

### 1. Update DOCKER_DEPLOYMENT.md
Add section:
```markdown
### Network Mode Selection

**Bridge Mode (Default):**
- Works on all platforms
- Safer container isolation
- Suitable for most deployments
- Network discovery via known IP addresses

**Host Mode (Advanced - Linux Only):**
- Direct access to host network
- Better network discovery (broadcast/multicast)
- Requires manual configuration
- See: /docs/MANUAL_HOST_NETWORK_SETUP.md
```

### 2. Update DEPLOYMENT_READINESS_CHECK.md
Add warning:
```markdown
### Network Discovery Limitations
- Default bridge mode works for known printer IPs
- For automatic network scanning, consider host mode (Linux only)
- CORS configuration is dynamic based on configured ports
```

### 3. Create MANUAL_HOST_NETWORK_SETUP.md
Complete guide for advanced users needing host networking.

---

## Conclusion

### Current State
- ✅ **Deployment script works** for basic network discovery
- ❌ **No host network option** - requires manual configuration
- ❌ **CORS not dynamic** - can cause issues with custom ports
- ⚠️ **Documentation incomplete** - advanced users need guidance

### Action Items

**Critical (Before Ubuntu Deployment):**
1. ✅ Fix CORS dynamic origins in `generate_env_file()`
2. ✅ Add network mode documentation
3. ✅ Create manual host network setup guide
4. ✅ Test bridge mode works for known IPs

**Nice-to-Have (Future Enhancement):**
1. 🔧 Add network mode selection to deployment script
2. 🔧 Auto-generate host network override files
3. 🔧 Platform-specific recommendations

### Recommended Deployment for Ubuntu Server

**Use bridge mode (default) with these settings:**
```bash
export ENABLE_DISCOVERY=yes
export NETWORK_RANGES=192.168.0.0/16  # Adjust to your network
export HTTP_PORT=8080
export API_PORT=5245

./scripts/deploy-docker.sh --non-interactive
```

**If you need better discovery later, manually enable host mode using the workaround above.**

---

**Status:** Ready to deploy with documented workarounds  
**Priority:** Fix CORS dynamic origins before deployment  
**Timeline:** Can deploy now, enhance network options in next sprint
