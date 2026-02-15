# Deploy Script Health Check Enhancement

## Overview

Enhanced `deploy-docker.sh` with comprehensive health checking similar to `start-all-local-with-workers.sh`. The script now:
1. Waits for all containers to become healthy before declaring success
2. Runs comprehensive health checks on all services
3. Provides detailed troubleshooting info if checks fail
4. Returns appropriate exit codes based on health status

## Changes Made

### 1. Container Health Waiting Loop

**Location**: After `docker compose up -d` in `deploy_containers()`

**What it does**:
- Waits up to 2 minutes (120 seconds) for all containers to become healthy
- Checks container health status every 5 seconds
- Shows progress updates every 15 seconds
- Lists containers that are still starting/unhealthy

**Code**:
```bash
local max_wait=120  # 2 minutes total
while [ $elapsed -lt $max_wait ]; do
    local unhealthy_count=$(docker compose ps --format json | grep -c 'starting\|unhealthy')
    if [ "$unhealthy_count" -eq 0 ]; then
        print_success "All containers are healthy!"
        break
    fi
    # Show progress...
done
```

### 2. Comprehensive Health Verification

**Location**: `verify_deployment()` function

**Checks performed**:
1. **Basic Health** (`/healthz`):
   - Tests endpoint availability
   - Validates JSON response contains `"status":"ok"`

2. **Comprehensive Health** (`/health`):
   - Tests endpoint availability
   - Parses health status from JSON
   - Validates status is "Healthy"
   - Displays health check details using `jq` if available
   - Retries once if initial check fails

3. **API Endpoints** (`/api/printers`):
   - Verifies API is responding to actual requests

4. **Worker Health** (if enabled):
   - OrcaSlicer worker: `http://localhost:8081/healthz`
   - PrusaSlicer worker: `http://localhost:8082/healthz`

**Output format**:
```
✓ Basic health check: OK
✓ Comprehensive health check: Healthy
  • comprehensive: Catalog API check passed
  • signalr: SignalR fully operational
  • spoolman: Spoolman not configured
✓ API endpoints: OK
✓ OrcaSlicer worker: Healthy
✓ PrusaSlicer worker: Healthy
```

### 3. Exit Code Handling

**Behavior**:
- Returns `0` if all health checks pass ✅
- Returns `1` if any health check fails ❌
- Main script exits with `1` if verification fails
- Provides clear visual indicators (✓ vs ✗)

### 4. Enhanced Final Display

**Added features**:
- Shows health check status in deployment summary
- Different messages based on verification result:
  - Success: "✅ PrintFarmer is now running and healthy!"
  - Warning: "⚠️ PrintFarmer is deployed but some health checks failed"

**Conditional troubleshooting**:
- Shows standard troubleshooting always
- Shows **additional troubleshooting** if health checks fail:
  - How to check API logs
  - How to check for crashed containers
  - How to restart specific services
  - How to manually verify health

### 5. Verification Flow

**Before** (old behavior):
```bash
deploy_containers
  └─ Starts containers
  └─ Sleeps 15 seconds
  └─ Returns

verify_deployment
  └─ Checks endpoints (may fail if still starting)
  └─ Always shows "Deployment verification completed!"
  └─ Always returns success

display_final_info
  └─ Always shows "PrintFarmer is now running!"
```

**After** (new behavior):
```bash
deploy_containers
  └─ Starts containers
  └─ Waits for ALL containers to be healthy (up to 2 min)
  └─ Shows progress every 15 seconds
  └─ Returns

verify_deployment
  └─ Comprehensive health checks
  └─ Retries if needed
  └─ Returns 0 (success) or 1 (failure)

display_final_info
  └─ Shows status based on verification result
  └─ Shows extra troubleshooting if checks failed

Main exits with 1 if verification failed
```

## Benefits

1. **Confidence**: Know for certain that services are healthy before using them
2. **Early Detection**: Catch startup failures immediately, not after manual testing
3. **Better UX**: No more "502 Bad Gateway" surprises after deployment
4. **Debugging**: Detailed troubleshooting info when things go wrong
5. **Automation-Friendly**: Proper exit codes for CI/CD integration

## Example Output

### Successful Deployment
```
🚀 Deployment

Step 3/3: Containers starting...
Waiting for all services to be healthy...
✓ All containers are healthy!

🔍 Verifying Deployment

NAMES                        STATUS                   PORTS
pfarm-api-1                  Up 45 seconds (healthy)  0.0.0.0:5245->8080/tcp
pfarm-frontend-1             Up 45 seconds (healthy)  0.0.0.0:8080->80/tcp
pfarm-database-1             Up 46 seconds (healthy)  0.0.0.0:1434->1433/tcp

Running comprehensive health checks...
✓ Basic health check: OK
✓ Comprehensive health check: Healthy
  • comprehensive: All systems operational
  • signalr: SignalR fully operational
✓ API endpoints: OK
✓ OrcaSlicer worker: Healthy

✅ All health checks passed!

🎉 Deployment Complete
✅ PrintFarmer is now running and healthy!
```

### Failed Deployment (with new troubleshooting)
```
🔍 Verifying Deployment

✗ Basic health check: FAILED (endpoint not responding)
✗ Comprehensive health check: Status = Unhealthy
✗ API endpoints: Not responding

⚠️ Some health checks failed. Services may still be initializing.
  • Health: curl http://localhost:8080/health | jq
  • Logs:   docker compose logs -f

🎉 Deployment Complete
⚠️ PrintFarmer is deployed but some health checks failed

⚠️ Health Check Failures - Common Solutions:
  1. Check API container logs:
     docker compose logs api | tail -50
  2. Check if API crashed (exit code):
     docker ps -a | grep api
  3. Restart API container:
     docker compose restart api
  4. Check health manually (wait 30s then):
     curl http://localhost:8080/health | jq

⚠️ Setup completed with warnings - please check health status above
```

## Testing

Run the enhanced deployment:

```bash
./scripts/deploy-docker.sh --tear-down
./scripts/deploy-docker.sh
```

Expected behavior:
- Script waits for all containers to be healthy
- Runs comprehensive health checks
- Shows ✓ for passing checks, ✗ for failures
- Exits with code 0 if all healthy, 1 if any failures
- Provides helpful troubleshooting if checks fail

## Compatibility

- Works with all architectures (monolith, microservices)
- Works with all network modes (bridge, host)
- Works with all database providers
- Gracefully handles missing `jq` (falls back to raw JSON)
- Handles both local and remote deployments
