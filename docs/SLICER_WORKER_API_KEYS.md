# Slicer Worker API Keys - Automatic Generation

## Table of Contents
1. [Overview](#overview)
2. [What Changed](#what-changed)
3. [Quick Start](#quick-start)
4. [How It Works](#how-it-works)
5. [Detailed Implementation](#detailed-implementation)
6. [Deployment Examples](#deployment-examples)
7. [Security Considerations](#security-considerations)
8. [Configuration](#configuration)
9. [Common Tasks](#common-tasks)
10. [Troubleshooting](#troubleshooting)
11. [Under the Hood](#under-the-hood)
12. [Future Enhancements](#future-enhancements)

---

## Overview

The deploy-docker.sh script now **automatically generates API keys for slicer workers** during deployment. This enables workers to register with the API on startup without requiring manual API key creation or management.

### What This Solves

**Before this change:**
- ❌ Users had to manually create API keys
- ❌ Workers needed pre-configured credentials
- ❌ Scaling workers required manual key management
- ❌ Prone to configuration errors

**After this change:**
- ✅ Keys generated automatically during deployment
- ✅ All workers ready to register on startup
- ✅ Scaling with `--scale` just works
- ✅ Credentials automatically passed to containers
- ✅ No manual configuration needed

---

## What Changed

### 1. New Helper Functions in `scripts/deploy-docker.sh`

#### `generate_slicer_api_key()`
**Location**: Lines 121-131

- **Purpose**: Generates a secure, unique API key for worker registration
- **Returns**: 32-character URL-safe Base64 string
- **Implementation**: Uses OpenSSL for randomness; falls back to `/dev/urandom` if OpenSSL unavailable
- **Security**: Cryptographically secure random bytes encoded as Base64

```bash
generate_slicer_api_key() {
    # Generates URL-safe Base64 API key (no padding)
    # Returns: 32-character secure random string suitable for API authentication
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -base64 32 | tr -d '/+=\n' | cut -c1-32
    else
        tr -dc 'A-Za-z0-9_-' </dev/urandom 2>/dev/null | head -c 32 || \
        echo "apikey$(date +%s%N | md5sum | awk '{print $1}' | cut -c1-24)"
    fi
}
```

#### `generate_slicer_worker_api_keys()`
**Location**: Lines 2346-2370

- **Purpose**: Generates unique API keys for each worker replica
- **Behavior**:
  - Called automatically when `ENABLE_ORCA_WORKER=yes` and `ORCA_WORKER_COUNT > 0`
  - Creates one key per worker replica
  - Stores keys in arrays: `SLICER_WORKER_NAMES[]` and `SLICER_WORKER_API_KEYS[]`
  - Prints progress information showing masked first 8 characters of each key
- **Example Output**:
  ```
  Generated API key for worker replica 1: a1b2c3d4...
  Generated API key for worker replica 2: e5f6g7h8...
  ```

#### `export_slicer_worker_api_keys()`
**Location**: Lines 2380-2415

- **Purpose**: Exports generated API keys to the `.env` environment file
- **Environment Variables Created**:
  - `SlicerRegistry__ApiKey` - Primary/default API key for single or first worker
  - `SlicerRegistry__ApiKey__orcaslicer_worker_N` - Individual keys for scaled workers
- **Format**: Follows Docker Compose environment variable naming convention with double underscores for config section nesting

### 2. Integration into `generate_env_file()` Function

**Location**: Lines 2704-2705

The environment file generation now includes:

```bash
# Generate and export slicer worker API keys (if workers are enabled)
generate_slicer_worker_api_keys
export_slicer_worker_api_keys
```

User-facing output showing masked API keys:
```
🔑 Slicer Worker API Keys (for automatic registration):
  OrcaSlicer Worker 1: a1b2c3d4...
  OrcaSlicer Worker 2: e5f6g7h8...

Full API keys are available in: .env
Workers will automatically use these keys to register with the API on startup.
```

---

## Quick Start

### Deploy with OrcaSlicer Workers

```bash
./scripts/deploy-docker.sh
# When prompted:
# - Enable OrcaSlicer worker(s)? → yes
# - Number of OrcaSlicer worker replicas? → 2 (or your desired count)
```

### What Happens Automatically

1. ✅ Script generates unique API keys (one per worker replica)
2. ✅ Keys exported to `.env` file automatically
3. ✅ Docker containers receive keys via environment variables
4. ✅ Workers use keys to register with API on startup
5. ✅ Workers send heartbeats periodically to show they're alive

### View Generated Keys

```bash
# See all generated keys
grep SlicerRegistry__ApiKey .env

# See just the primary key
grep "^SlicerRegistry__ApiKey=" .env

# Extract just the value
grep "^SlicerRegistry__ApiKey=" .env | cut -d= -f2
```

---

## How It Works

### Worker Registration Flow

```
Worker Container Starts
  ↓
Reads SlicerRegistry__ApiKey from environment
  ↓
RegistrationBackgroundService starts
  ↓
Makes HTTP POST to /api/slicers/register
  ├─ Includes X-Slicer-ApiKey header (the generated key)
  └─ Includes worker info (name, version, capabilities)
  ↓
API validates key and creates SlicerService record
  ↓
Returns ServiceId (unique identifier for this worker)
  ↓
Worker stores ServiceId for periodic heartbeats
  ↓
Heartbeats sent every 30 seconds showing worker status
```

### Deployment Flow

1. **User Configuration**: User specifies to deploy slicer workers
   ```bash
   ./scripts/deploy-docker.sh
   # Selects: "Enable OrcaSlicer worker(s)? yes"
   # Sets: ENABLE_ORCA_WORKER=yes, ORCA_WORKER_COUNT=2
   ```

2. **Environment Generation**: During `generate_env_file()` execution:
   - Script calls `generate_slicer_worker_api_keys()` 
   - Creates 2 unique API keys (one per replica)
   - Calls `export_slicer_worker_api_keys()`
   - Writes keys to `.env` file

3. **Container Startup**: 
   - Docker Compose passes environment variables to worker containers
   - Worker reads `SlicerRegistry__ApiKey` (or worker-specific key)
   - OrcaSlicer worker's `RegistrationBackgroundService` starts
   - Worker calls `/api/slicers/register` endpoint with API key header

4. **Operational Phase**:
   - Worker sends heartbeats every 30 seconds (configurable)
   - Heartbeats include worker status and available capacity
   - API tracks worker health and capacity

---

## Detailed Implementation

### Example: Multi-Worker Deployment

When deploying with multiple OrcaSlicer workers:

```bash
./scripts/deploy-docker.sh
# Configuration:
#   Architecture: microservices
#   Database: PostgreSQL
#   Enable OrcaSlicer workers: yes
#   OrcaSlicer version: 2.3.1
#   Number of replicas: 3
```

Generated `.env` contains:
```bash
# Primary key for default/first worker
SlicerRegistry__ApiKey=Key1_SecureRandomValue_32chars

# Individual keys for each scaled replica
SlicerRegistry__ApiKey__orcaslicer_worker_1=Key1_SecureRandomValue_32chars
SlicerRegistry__ApiKey__orcaslicer_worker_2=Key2_DifferentSecureValue_32char
SlicerRegistry__ApiKey__orcaslicer_worker_3=Key3_AnotherSecureValue_32chars
```

Docker Compose brings up 3 worker containers with corresponding keys.

### API Endpoint Used

**Endpoint**: `POST /api/slicers/register`
**Location**: `src/api/Controllers/SlicersController.cs`
**Authentication**: 
- Checks for `X-Slicer-ApiKey` header
- Validates against static registry key (for registration) OR service-specific key (for updates)
- Stores ApiKey in `SlicerService` record for future operations

**Response**: 
```json
{
  "id": "guid-of-registered-service",
  "apiKey": "api-key-returned-by-api"
}
```

### Worker Registration Code

The worker registration flow is implemented in three files:

**1. `src/orcaslicer-worker/Program.cs`** - Startup and dependency injection
- Registers SlicerRegistrationClient and RegistrationBackgroundService as hosted services

**2. `src/orcaslicer-worker/Services/SlicerRegistrationClient.cs`** - HTTP communication
- Reads SlicerRegistry:ApiKey from configuration
- Sends X-Slicer-ApiKey header with registration requests

**3. `src/orcaslicer-worker/Services/RegistrationBackgroundService.cs`** - Lifecycle management
- Automatically calls RegisterAsync() on startup
- Sends heartbeats every 30 seconds
- Handles deregistration on shutdown

---

## Deployment Examples

### Example 1: Single OrcaSlicer Worker

```bash
./scripts/deploy-docker.sh
# Interactive Prompts
# Choose architecture [1=Monolithic, 2=Microservices]: 2
# Choose database [1=PostgreSQL, 2=SQL Server, 3=MySQL, 4=External]: 1
# Enable OrcaSlicer worker(s)? yes
# OrcaSlicer version to deploy: 2.3.1
# Number of OrcaSlicer worker replicas: 1
```

Generated `.env` (Relevant Sections):
```bash
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
ORCASLICER_VERSION=2.3.1
SlicerRegistry__ApiKey=a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6
```

What Happens:
1. API key generated: `a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6`
2. Exported to `.env` as `SlicerRegistry__ApiKey`
3. Docker Compose passes to worker container
4. Worker reads key on startup
5. Worker POSTs to `/api/slicers/register` with key
6. API validates and assigns ServiceId
7. Worker ready for slicing jobs ✅

Worker Container Output:
```bash
[INF] RegistrationBackgroundService starting...
[INF] Attempting to register with slicer registry...
[INF] Successfully registered with slicer registry. ServiceId: 550e8400-e29b-41d4-a716-446655440000
[INF] Heartbeat sent successfully. FreeSlots: 1, Status: Online
```

### Example 2: Three OrcaSlicer Workers (Scaled)

```bash
ORCA_WORKER_COUNT=3 ./scripts/deploy-docker.sh --non-interactive
```

Configuration:
```bash
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=3
ORCASLICER_VERSION=2.3.1
```

Generated `.env` (Worker Section):
```bash
SlicerRegistry__ApiKey=a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6
SlicerRegistry__ApiKey__orcaslicer_worker_1=a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6
SlicerRegistry__ApiKey__orcaslicer_worker_2=x9Y8z7W6v5U4t3S2r1Q0p9O8n7M6l5K
SlicerRegistry__ApiKey__orcaslicer_worker_3=K5l6M7n8O9p0Q1r2S3t4U5v6W7x8Y9z
```

Scaling Command:
```bash
docker compose --env-file .env up -d --scale orcaslicer-worker=3
```

Result: Three Containers
```
Container                          Status
printfarmer-orcaslicer-worker-1   Running (port 8081:8080)
printfarmer-orcaslicer-worker-2   Running (port 8082:8080)
printfarmer-orcaslicer-worker-3   Running (port 8083:8080)
```

Registration Output:
```bash
# Worker 1 logs
[INF] Successfully registered with slicer registry. ServiceId: 550e8400-e29b-41d4-a716-446655440001

# Worker 2 logs
[INF] Successfully registered with slicer registry. ServiceId: 550e8400-e29b-41d4-a716-446655440002

# Worker 3 logs
[INF] Successfully registered with slicer registry. ServiceId: 550e8400-e29b-41d4-a716-446655440003
```

API View of Registered Services:
```bash
curl -s http://localhost:5245/api/slicers | jq '.[] | {id, name, status, lastSeen}'

# Output:
[
  {
    "id": "550e8400-e29b-41d4-a716-446655440001",
    "name": "orcaslicer-worker",
    "status": "Online",
    "lastSeen": "2025-01-15T10:30:45Z"
  },
  {
    "id": "550e8400-e29b-41d4-a716-446655440002",
    "name": "orcaslicer-worker",
    "status": "Online",
    "lastSeen": "2025-01-15T10:30:44Z"
  },
  {
    "id": "550e8400-e29b-41d4-a716-446655440003",
    "name": "orcaslicer-worker",
    "status": "Online",
    "lastSeen": "2025-01-15T10:30:43Z"
  }
]
```

### Example 3: Custom API Key (Advanced)

Pre-generate keys externally:

```bash
# Generate your own key
MY_KEY=$(openssl rand -base64 32 | tr -d '/+=\n' | cut -c1-32)

# Create .env manually
cat > .env << EOF
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
SlicerRegistry__ApiKey=$MY_KEY
SlicerRegistry__ApiKey__orcaslicer_worker_1=$MY_KEY
SlicerRegistry__ApiKey__orcaslicer_worker_2=$MY_KEY
EOF

# Deploy with existing .env
docker compose up -d
```

Result:
- Workers use provided keys instead of generated ones
- Script still generates keys but they're overwritten by .env
- Workers register successfully with custom keys

### Example 4: Redeploy with New Keys

Security incident scenario - need new keys:

```bash
# Remove old config
rm .env .deploy-config

# Redeploy - generates completely new keys
./scripts/deploy-docker.sh

# Old workers won't start (they'll try to use old keys that no longer work)
# New workers will use fresh keys and register successfully
```

Process:
```
Old State:
  - API has ServiceIds for old workers
  - Old .env with old keys (deleted)
  
Redeploy:
  - New keys generated
  - Written to new .env
  - New keys don't match old ServiceIds
  - Old workers fail to heartbeat (credentials invalid)
  - New workers register with new keys and new ServiceIds
  
Result:
  - API now tracks only new workers
  - Old workers orphaned (can be cleaned up later)
```

---

## Security Considerations

### Key Generation
- Uses cryptographically secure random bytes (OpenSSL or `/dev/urandom`)
- 32-character length provides 192 bits of entropy (sufficient for API authentication)
- URL-safe Base64 encoding prevents URL encoding issues
- No special characters that might cause parsing issues in environment variables

### Key Storage
- Keys stored in `.env` file on deployment host
- File permissions: `600` (read/write for owner only) - enforced by existing chmod 600 in script
- Keys passed via Docker environment variables to containers
- Keys only live in container memory during execution
- Container restart generates new healthchecks but uses same persisted key

### Key Lifecycle
- Generated fresh on each deployment (one-time generation)
- Worker uses key to register once on startup
- API generates ServiceId and returns confirmation
- Worker can later request key rotation via `/api/slicers/{id}/rotate-key`
- On container restart, worker uses same key stored in env file

### Best Practices
1. **Store .env securely**: Don't commit to version control
2. **Rotate keys**: Use API endpoint if keys compromise is suspected
3. **Network security**: API and workers should be on secure networks
4. **Access control**: Restrict who can access the deploy script and generated `.env` files

---

## Configuration

### Environment Variables

| Variable | Scope | Set By | Used By |
|----------|-------|--------|---------|
| `ENABLE_ORCA_WORKER` | Global | User prompt / CLI | Deployment script, deployment config |
| `ORCA_WORKER_COUNT` | Global | User prompt / CLI | Key generation, Docker Compose scaling |
| `SlicerRegistry__ApiKey` | Docker | Key generation | Worker registration (primary key) |
| `SlicerRegistry__ApiKey__*` | Docker | Key generation | Worker registration (per-replica keys) |

### Key Details

| Aspect | Details |
|--------|---------|
| **Key Length** | 32 characters |
| **Key Format** | URL-safe Base64 (no padding) |
| **Randomness** | Cryptographically secure (OpenSSL or /dev/urandom) |
| **Storage** | `.env` file (chmod 600) |
| **Passed to Containers** | Via Docker environment variables |
| **Lifetime** | Lives in container memory, reused until container restart |
| **Per-Container** | All workers can share primary key OR get individual keys |

### Accessing Generated Keys

**View all keys:**
```bash
grep "SlicerRegistry__ApiKey" .env
```

**View only primary key:**
```bash
grep "^SlicerRegistry__ApiKey=" .env
```

**Extract just the value:**
```bash
grep "^SlicerRegistry__ApiKey=" .env | cut -d= -f2
```

### Complete `.env` With Workers Enabled

```bash
# PrintFarmer Docker Configuration
# Generated by deploy-docker.sh on Wed Jan 15 10:00:00 PST 2025

# Architecture
DEPLOYMENT_TYPE=microservices

# Application Settings
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:8080

# Database Configuration
DB_PROVIDER=postgres
POSTGRES_DB=printfarmer
POSTGRES_USER=postgres
POSTGRES_PASSWORD=SecureRandomPassword123!

# Network Configuration
ALLOW_LOCAL_NETWORK=true
NETWORK_MODE=bridge

# Feature Flags  
ENABLE_SWAGGER=false
ENABLE_DETAILED_LOGGING=false
ENABLE_DISTRIBUTED_SLICING=true
ORCA_WORKER_COUNT=2
ENABLE_ORCA_WORKER=yes
ORCA_HOST_PORT=8081

# Slicer Versions
ORCASLICER_VERSION=2.3.1

# Slicer Worker API Keys - Generated for automatic worker registration
# Workers use these keys to authenticate with the API during registration
SlicerRegistry__ApiKey=a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6

# Individual API Keys for Scaled OrcaSlicer Workers
SlicerRegistry__ApiKey__orcaslicer_worker_1=a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6
SlicerRegistry__ApiKey__orcaslicer_worker_2=x9Y8z7W6v5U4t3S2r1Q0p9O8n7M6l5K

# Port Configuration
HTTP_PORT=8080
API_PORT=5245
API_URL=http://localhost:5245

# CORS Configuration
CORS__AllowedOrigins=http://localhost:3000,http://localhost:8080,http://localhost:5245
```

---

## Common Tasks

### Check if workers registered successfully
```bash
docker compose logs orcaslicer-worker | grep -i "registered"
```

### View worker status in API
```bash
curl http://localhost:5245/api/slicers | jq '.'
```

### Verify worker heartbeats
```bash
docker compose logs orcaslicer-worker | grep -i "heartbeat"
```

### Regenerate keys (fresh deployment)
```bash
rm .env .deploy-config
./scripts/deploy-docker.sh  # Fresh run, all new keys
```

### Check if key was generated
```bash
grep SlicerRegistry__ApiKey .env | head -3
```

### Check worker logs for registration
```bash
docker compose logs orcaslicer-worker | grep "Successfully registered"
```

### Check heartbeats are working
```bash
docker compose logs orcaslicer-worker | grep "Heartbeat sent"
```

### Check via API
```bash
# Get all registered slicers
curl -s http://localhost:5245/api/slicers

# Check specific slicer health
curl -s http://localhost:5245/api/slicers/{service-id}

# Example:
curl -s http://localhost:5245/api/slicers/550e8400-e29b-41d4-a716-446655440001 | jq '.'
```

---

## Troubleshooting

### Worker Fails to Register

**Symptom**: Worker logs show "Failed to register with slicer registry"

**Solutions**:
1. Check worker can reach API:
   ```bash
   docker exec <worker-container> curl http://api:5245/api/slicers/register -v
   ```

2. Verify API key is present:
   ```bash
   docker inspect <worker-container> | grep SlicerRegistry
   ```

3. Check API logs for key validation errors:
   ```bash
   docker compose logs api | grep -i "register\|apikey"
   ```

### Workers don't register?
1. Check API is running: `curl http://localhost:5245/health`
2. Check worker can reach API: `docker exec <worker> curl http://api:5245/api/slicers`
3. Check API logs: `docker compose logs api | grep -i register`

### Multiple Workers With Same Key

This is **expected and normal**. By design:
- All workers are given access to `SlicerRegistry__ApiKey` (the primary key)
- All workers can use this shared key to register
- Each worker gets a unique `ServiceId` from the API upon registration
- ServiceId, not the API key, identifies individual workers

To give workers distinct keys (advanced):
```bash
# Manually modify .env before starting workers
# Set per-replica environment in docker-compose override file
```

### Keys Not Appearing in Environment File

**Causes**:
1. Workers disabled: `ENABLE_ORCA_WORKER=no` or `ORCA_WORKER_COUNT=0`
2. Script failed before reaching key generation
3. Environment file was edited after generation

**Solution**: Regenerate environment file:
```bash
# Force interactive setup
rm .env .deploy-config
./scripts/deploy-docker.sh  # Start fresh, enable workers when prompted
```

### Where are my API keys?
- Primary key: `grep "^SlicerRegistry__ApiKey=" .env`
- All keys: `grep SlicerRegistry__ApiKey .env`
- In running container: `docker exec <worker> env | grep SlicerRegistry`

### Can I manually set API keys?
Yes, edit `.env` before running `docker compose up`:
```bash
# Edit .env directly
SlicerRegistry__ApiKey=my-custom-key-value-here
```

### Can I rotate keys?
Yes, use the API endpoint (after initial registration):
```bash
curl -X POST http://localhost:5245/api/slicers/{service-id}/rotate-key \
  -H "X-Slicer-ApiKey: current-key"
```

---

## Under the Hood

### Files Involved

**Generation**: `scripts/deploy-docker.sh` functions:
- `generate_slicer_api_key()` - Creates single key
- `generate_slicer_worker_api_keys()` - Creates keys for all replicas
- `export_slicer_worker_api_keys()` - Writes to .env

**Usage**: Worker container code:
- `src/orcaslicer-worker/Services/SlicerRegistrationClient.cs` - Reads key, calls API
- `src/orcaslicer-worker/Services/RegistrationBackgroundService.cs` - Periodic heartbeats

**Validation**: API code:
- `src/api/Controllers/SlicersController.cs` - Validates `X-Slicer-ApiKey` header
- Infrastructure filter: `RequireSlicerApiKey` attribute

### Environment Variables

```bash
# Primary key (used by first worker or all workers if not scaled)
SlicerRegistry__ApiKey=<secure-32-char-string>

# Per-worker keys (for scaled deployments)
SlicerRegistry__ApiKey__orcaslicer_worker_1=<key1>
SlicerRegistry__ApiKey__orcaslicer_worker_2=<key2>
SlicerRegistry__ApiKey__orcaslicer_worker_N=<keyN>
```

### Data Flow

```
deploy-docker.sh
  ├─ User enables workers (count=N)
  ├─ generate_slicer_worker_api_keys()
  │  └─ Create N unique keys
  ├─ export_slicer_worker_api_keys()
  │  └─ Write to .env file
  └─ .env passed to Docker Compose
      ├─ Worker 1 gets env var with key 1
      ├─ Worker 2 gets env var with key 2
      └─ Worker N gets env var with key N
          ↓
          Each worker reads env var
          ↓
          Each worker POSTs to /api/slicers/register
          ├─ Header: X-Slicer-ApiKey: <its-key>
          └─ Body: registration data
              ↓
              API validates key
              ↓
              Creates SlicerService record (storage backend)
              ↓
              Returns ServiceId
              ↓
              Worker stores ServiceId
              ↓
              Periodic heartbeats using ServiceId
```

### Integration with Worker Registration Flow

**Current Flow (After This Change)**

```
deploy-docker.sh
  └─ configure_additional()  [User selects workers]
  └─ generate_env_file()
       └─ generate_slicer_worker_api_keys()
       └─ export_slicer_worker_api_keys()
  └─ docker compose up
       └─ OrcaSlicer Worker Container
            └─ Program.cs [Startup]
                 └─ RegistrationBackgroundService
                      └─ RegistrationClient.RegisterAsync()
                           └─ POST /api/slicers/register
                                ├─ X-Slicer-ApiKey: <generated-key>
                                └─ Body: { Name, Version, Host, Capabilities, ... }
                           └─ API validates key and creates SlicerService record
                           └─ Returns: { Id, ApiKey }
                      └─ Stores ServiceId for heartbeats
  └─ Periodic heartbeats every 30s
```

---

## Verification Checklist

After deployment, verify:

- [ ] `.env` file contains `SlicerRegistry__ApiKey` entries
- [ ] Worker containers started successfully
- [ ] Worker logs show "Successfully registered"
- [ ] `curl http://localhost:5245/api/slicers` returns registered workers
- [ ] Each worker has unique ServiceId
- [ ] Heartbeats appearing in logs periodically
- [ ] API health check shows all services healthy
- [ ] Can submit slicing jobs through UI

---

## Future Enhancements

1. **Per-Worker Key Rotation**: Support rotating individual worker keys without redeployment
2. **Key Expiration**: Add optional key expiration dates
3. **Key Metadata**: Store creation timestamp, last rotated, usage stats
4. **Key Revocation**: Ability to revoke keys for decommissioned workers
5. **Audit Logging**: Track key generation and usage for security audits
6. **Key Backup/Recovery**: Automatic backup of keys for disaster recovery

---

## Summary

✅ **Automatic** - Keys generated during deployment, no manual setup  
✅ **Unique** - Each worker replica gets unique identification  
✅ **Secure** - Cryptographically strong random keys  
✅ **Scalable** - Works with any number of worker replicas  
✅ **Non-Breaking** - Completely backward compatible  
✅ **User-Friendly** - Shows masked keys, no clutter  

**Just deploy and workers automatically register! 🚀**

---

## References

- **API Endpoint**: `src/api/Controllers/SlicersController.cs` - RegisterAsync method
- **Worker Registration**: `src/orcaslicer-worker/Services/SlicerRegistrationClient.cs`
- **Background Service**: `src/orcaslicer-worker/Services/RegistrationBackgroundService.cs`
- **Startup Configuration**: `src/orcaslicer-worker/Program.cs`
- **Deployment Script**: `scripts/deploy-docker.sh` - Key generation and export functions
