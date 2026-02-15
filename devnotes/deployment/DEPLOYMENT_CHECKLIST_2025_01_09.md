# Deployment Checklist - Health Check & DI Fix

## Overview
This deployment includes:
1. **Critical DI lifetime fix** - Prevents API crash on startup
2. **Comprehensive health checks** - Ensures services are truly ready

## Deployment Steps

### 1. Push Changes to Remote
```bash
git push origin dev/jpapiez/logging-db-consolidation
```

### 2. SSH to Server
```bash
ssh pi@10.0.0.75
cd /home/pi/pfarm
```

### 3. Pull Latest Changes
```bash
git pull origin dev/jpapiez/logging-db-consolidation
```

### 4. Deploy with Enhanced Health Checks
```bash
./scripts/deploy-docker.sh --tear-down
./scripts/deploy-docker.sh
```

### 5. Observe New Behavior

**During deployment, you should see:**
```
🚀 Deployment
Step 3/3: Containers starting...
Waiting for all services to be healthy...
⏳ Still waiting... (15s elapsed)
✓ All containers are healthy!

🔍 Verifying Deployment
Running comprehensive health checks...
✓ Basic health check: OK
✓ Comprehensive health check: Healthy
  • comprehensive: All systems operational
  • signalr: SignalR fully operational
  • spoolman: Spoolman not configured (expected if not set up)
✓ API endpoints: OK
✓ OrcaSlicer worker: Healthy

✅ All health checks passed!

🎉 Deployment Complete
✅ PrintFarmer is now running and healthy!
```

## Verification

### 1. API Should Start Successfully (DI Fix)
```bash
# Check API container is running (not exited)
docker ps | grep api

# Should show: Up X seconds (healthy)
# NOT: Exited (139) as before
```

### 2. Health Endpoints Should Work
```bash
# Basic health
curl http://localhost:8080/healthz

# Comprehensive health
curl http://localhost:8080/health | jq

# Should show all services healthy
```

### 3. Setup Wizard Should Pre-Fill Settings
```bash
# Open in browser: http://10.0.0.75:8080

# Should show:
# - Spoolman URL already filled (if configured in deploy script)
# - Network discovery settings already filled
# - No duplicate configuration needed
```

## What Changed

### 1. DI Lifetime Fix (Critical)
**File**: `src/api/Infrastructure/ServiceCollectionExtensions.cs`

**Problem**: API crashed with exit code 139
```
Cannot consume scoped service 'ISettingsService' from singleton 'SettingsInitializationService'
```

**Fix**: Changed registration from Singleton to Scoped
```csharp
// Before: services.AddSingleton<SettingsInitializationService>();
// After:  services.AddScoped<SettingsInitializationService>();
```

### 2. Health Check Enhancements
**File**: `scripts/deploy-docker.sh`

**New behavior**:
- ✅ Waits for containers to be healthy (not just started)
- ✅ Verifies all health endpoints comprehensively
- ✅ Shows detailed health status with jq
- ✅ Tests worker endpoints if enabled
- ✅ Exits with error if health checks fail
- ✅ Shows troubleshooting commands on failure

**Old behavior**:
- ❌ Sleep 15 seconds arbitrarily
- ❌ Basic curl checks (could pass while services still starting)
- ❌ Always declared success
- ❌ No troubleshooting info

## Troubleshooting

### If API Still Crashes
```bash
# Check logs for DI errors
docker compose logs api | grep -i "cannot consume"

# If still seeing DI errors, verify the build included the fix:
docker compose exec api cat /app/appsettings.json | grep -i version
```

### If Health Checks Fail
The script will now show:
```
⚠️ Health Check Failures - Common Solutions:
  1. Check API container logs:
     docker compose logs api | tail -50
  2. Check if API crashed (exit code):
     docker ps -a | grep api
  3. Restart API container:
     docker compose restart api
  4. Check health manually (wait 30s then):
     curl http://localhost:8080/health | jq
```

### If Services Seem Slow
```bash
# The script now waits up to 2 minutes for health
# If containers are still starting, you'll see:
⏳ Still waiting... (45s elapsed)
  Containers still starting/unhealthy:
  • pfarm-api-1: starting
  • pfarm-database-1: healthy
```

## Success Criteria

- [ ] API container starts successfully (no exit 139)
- [ ] All containers show "healthy" status
- [ ] Deploy script waits for health before declaring success
- [ ] All health checks pass (✓ symbols, no ✗)
- [ ] Setup Wizard shows pre-filled settings
- [ ] No duplicate configuration needed
- [ ] Settings persist after container restart

## Rollback (If Needed)

```bash
# Rollback to previous commit
git log --oneline -5  # Find commit before 458d4a8
git checkout <previous-commit>
./scripts/deploy-docker.sh --tear-down
./scripts/deploy-docker.sh
```

## Timeline

**Estimated deployment time**: 5-10 minutes
- Pull changes: ~30s
- Tear down: ~30s
- Deploy: ~3-5 minutes (includes 2-minute health wait)
- Verification: ~2 minutes
- Browser testing: ~2 minutes
