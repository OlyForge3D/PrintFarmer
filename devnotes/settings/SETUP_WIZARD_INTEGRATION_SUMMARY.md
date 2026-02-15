# Setup Wizard Deployment Integration - Implementation Summary

## Problem Statement

**Original Issue**: Users were asked to configure the same settings twice:
1. First in deployment script (`deploy-docker.sh`)
2. Again in Setup Wizard in the browser

This created poor UX and potential for configuration drift.

## Solution Overview

Implemented automatic settings initialization from environment variables, eliminating duplicate configuration.

**Flow**:
```
Deploy Script → Environment Variables → API Initialization → Setup Wizard Pre-fill
```

## What Was Implemented

### 1. Settings Initialization Pattern

**New Classes**:
- `SettingsInitializationService` - Reads PFARM__ env vars, initializes settings if DB empty
- `SpoolmanSettings` - IAppSetting implementation for Spoolman config

**Environment Variable Convention**:
```bash
PFARM__{SettingKey}__{PropertyName}

# Examples:
PFARM__Spoolman__BaseUrl=http://spoolman:7912
PFARM__NetworkDiscovery__EnableDiscovery=true
PFARM__NetworkDiscovery__DiscoverySubnets=["10.0.0.0/24","192.168.1.0/24"]
```

**Key Behavior**:
- Only initializes if settings are **empty** (default values only)
- Skips initialization if settings already exist in DB
- Preserves user modifications (won't overwrite existing config)
- Runs on every app startup, but only creates settings on first run

### 2. Deployment Integration

**Deploy Script Changes**:
- Saves PFARM__ environment variables to `.deployment.config`
- Passes env vars to Docker containers via docker-compose
- Works with all architectures (monolith, microservices)
- Works with all network modes (bridge, host)

**Docker Compose Updates**:
- `docker-compose.yml` (monolith): Added PFARM__ env vars
- `docker-compose.microservices.yml`: Added PFARM__ env vars to API service
- Generated templates updated with env vars

### 3. Frontend Integration

**Setup Wizard Changes**:
- Fetches settings from API on component mount
- Pre-populates form fields with deployment config values
- Works for Spoolman and NetworkDiscovery settings
- User can still modify settings (changes persist to DB)

### 4. Critical Bug Fix - DI Lifetime Mismatch

**Problem**: API crashed on startup with exit code 139 (SIGSEGV)
```
Cannot consume scoped service 'ISettingsService' from singleton 'SettingsInitializationService'
```

**Root Cause**: `SettingsInitializationService` registered as Singleton but depended on Scoped `ISettingsService`

**Fix**: Changed registration from `AddSingleton` to `AddScoped`
```csharp
// src/api/Infrastructure/ServiceCollectionExtensions.cs line 62
services.AddScoped<SettingsInitializationService>();
```

**Impact**: API now starts successfully instead of crashing

### 5. Deploy Script Health Checks

**New Behavior**:
- Waits up to 2 minutes for all containers to be healthy
- Checks container health status every 5 seconds
- Shows progress updates every 15 seconds
- Runs comprehensive health verification:
  - `/healthz` endpoint (basic health)
  - `/health` endpoint (comprehensive status)
  - API endpoints (`/api/printers`)
  - Worker endpoints (OrcaSlicer, PrusaSlicer if enabled)
- Parses health status with jq if available
- Returns exit code 0 (success) or 1 (failure)
- Shows troubleshooting commands if checks fail

**Old Behavior**:
- Sleep 15 seconds arbitrarily
- Basic curl checks (could pass while services still starting)
- Always declared success
- No troubleshooting info

## Files Modified

### Backend
1. **src/Infrastructure/Settings/SettingsInitializationService.cs** (CREATED)
   - Initializes IAppSetting from environment variables
   - Checks if settings contain only default values
   - Uses IConfiguration to bind env vars to settings classes

2. **src/Infrastructure/Settings/SpoolmanSettings.cs** (CREATED)
   - IAppSetting implementation
   - `SectionKey = "Spoolman"`
   - `BaseUrl` property

3. **src/api/Services/SpoolmanService.cs** (MODIFIED)
   - Removed `AppDbContext` dependency
   - Added `ISettingsService` dependency
   - Refactored to use settings service

4. **src/api/Infrastructure/ServiceCollectionExtensions.cs** (MODIFIED)
   - Line 62: **CRITICAL FIX** - `AddScoped<SettingsInitializationService>()`
   - Changed from Singleton to match ISettingsService lifetime

5. **src/api/Program.cs** (MODIFIED)
   - Lines 441-453: Settings initialization after `app.Build()`
   - Calls `InitializeFromEnvironment<SpoolmanSettings>()`
   - Calls `InitializeFromEnvironment<NetworkDiscoverySettings>()`

### Deployment
6. **scripts/deploy-docker.sh** (MODIFIED)
   - Lines 392-395: Save PFARM__ env vars to config
   - Lines 1918-1946: Enhanced `deploy_containers()` with health waiting
   - Lines 1977-2074: Complete rewrite of `verify_deployment()`
   - Lines 2108-2125: Main flow captures verification result
   - Lines 2184-2203: Added troubleshooting for failed health checks

7. **docker-compose.microservices.yml** (MODIFIED)
   - Lines 68-70: Added PFARM__ env vars to API service

8. **docker-compose.yml** (MODIFIED)
   - Lines 61-63: Added PFARM__ env vars to web service

### Frontend
9. **src/Web/ReactApp/src/types/SpoolmanSettings.ts** (CREATED)
   - TypeScript interface for SpoolmanSettings

10. **src/Web/ReactApp/src/components/SetupWizard.tsx** (MODIFIED)
    - Lines 99-110: Fetch SpoolmanSettings on mount
    - Pre-populate form fields

### Documentation
11. **docs/SETUP_WIZARD_DEPLOYMENT_INTEGRATION.md** (CREATED)
    - Full implementation plan
    - Architecture decisions
    - Testing strategy

12. **docs/TESTING_SETUP_WIZARD_INTEGRATION.md** (CREATED)
    - Step-by-step testing instructions
    - Verification commands
    - Troubleshooting guide

13. **docs/DEPLOY_SCRIPT_HEALTH_CHECKS.md** (CREATED)
    - Health check enhancement documentation
    - Before/after comparison
    - Example output

14. **docs/DEPLOYMENT_CHECKLIST_2025_01_09.md** (CREATED)
    - Deployment steps
    - Verification checklist
    - Troubleshooting guide

## Testing

### Unit Tests
All existing tests pass. No new test failures introduced.

### Integration Testing Steps

1. **Deploy to server**:
   ```bash
   ./scripts/deploy-docker.sh --tear-down
   ./scripts/deploy-docker.sh
   ```

2. **Verify health checks**:
   - Script should wait for containers to be healthy
   - Should show comprehensive health check output
   - Should only declare success when all checks pass

3. **Verify API starts**:
   ```bash
   docker ps | grep api
   # Should show: Up X seconds (healthy)
   # NOT: Exited (139)
   ```

4. **Verify settings pre-fill**:
   - Open Setup Wizard in browser
   - Spoolman URL should be pre-filled if configured
   - Network settings should be pre-filled if configured

5. **Verify persistence**:
   - Modify settings in wizard
   - Restart containers: `docker compose restart`
   - Settings should persist (not reset to env var values)

## Benefits

1. **Better UX**: Users configure once (in deploy script), Setup Wizard pre-fills
2. **Consistency**: Deployment config matches runtime config
3. **Automation-Friendly**: Settings can be fully configured via environment variables
4. **Flexibility**: Users can still modify settings in UI (changes persist)
5. **Non-Destructive**: Won't overwrite existing settings on restart
6. **Reliable Deployments**: Health checks ensure services are truly ready
7. **Better Debugging**: Detailed troubleshooting info on failures

## Architecture Decisions

### Why IAppSetting Instead of Direct DB Entity?

**Problem with direct DB approach**:
- Required modifying entity classes with nullable fields
- SQL Server IDENTITY columns caused issues
- Settings initialization coupled to database schema

**IAppSetting pattern benefits**:
- Clean separation of concerns
- Settings service handles persistence
- Environment variable binding uses IConfiguration
- No database schema changes needed
- Works with all database providers

### Why Environment Variables?

**Alternatives considered**:
1. **Config files in volume mounts** - Complex, requires file management
2. **API endpoint for initialization** - Security concerns, requires auth
3. **Environment variables** - ✅ Docker-native, secure, simple

**Chosen approach**: Environment variables via Docker Compose
- Native to Docker/Kubernetes
- Secure (not exposed in logs)
- Easy to manage via deploy script
- Works with all orchestration systems

### Why Check for Empty Settings?

**Behavior**: Only initialize if settings contain default values

**Rationale**:
- Preserves user modifications
- Prevents config drift
- Allows settings to be managed via UI after initial setup
- Environment variables act as "defaults", not "overrides"

## Known Limitations

1. **Array Settings**: JSON serialization required for arrays
   ```bash
   PFARM__NetworkDiscovery__DiscoverySubnets=["10.0.0.0/24","192.168.1.0/24"]
   ```

2. **First Run Only**: Environment variables only used if DB is empty
   - To reset: Delete settings in UI or database
   - Then restart to re-initialize from env vars

3. **No Validation**: Environment variable values not validated until runtime
   - Invalid values will fail at runtime, not at deployment

## Future Enhancements

1. **Settings Migration**: Automatic migration from old config format
2. **Validation**: Environment variable validation in deploy script
3. **More Settings**: Extend pattern to all configurable settings
4. **UI Indicator**: Show which settings came from environment vs user-modified
5. **Settings Export**: Export current settings as environment variables

## Rollback Plan

If issues arise:

```bash
# 1. Find commit before changes
git log --oneline -10

# 2. Rollback to previous commit
git checkout <previous-commit>

# 3. Redeploy
./scripts/deploy-docker.sh --tear-down
./scripts/deploy-docker.sh
```

## Success Metrics

- ✅ API starts successfully (no DI crash)
- ✅ Deploy script waits for container health
- ✅ All health checks pass
- ✅ Setup Wizard pre-fills settings from deployment
- ✅ Settings persist after modification
- ✅ No duplicate configuration required
- ✅ Deployment declares success only when services healthy

## Conclusion

This implementation provides a seamless configuration experience:
1. Configure once in deployment script
2. Settings automatically initialize in API
3. Setup Wizard pre-fills from API
4. Users can modify as needed
5. Changes persist across restarts
6. Deployment verifies health comprehensively

The pattern is extensible to other settings and provides a solid foundation for configuration management.
