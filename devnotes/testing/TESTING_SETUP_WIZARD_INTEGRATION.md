# Testing Setup Wizard & Deployment Integration

## What Changed

The Setup Wizard now pre-populates settings from the deployment script, eliminating duplicate configuration.

## Testing Steps

### 1. Fresh Deployment Test

```bash
# Clean slate
./scripts/deploy-docker.sh --tear-down

# Deploy with configuration
./scripts/deploy-docker.sh

# When prompted:
# - Network Discovery: Enable = yes
# - Network Subnets: 10.0.0.0/24,192.168.1.0/24
# - Spoolman Integration: Enable = yes
# - Spoolman URL: http://spoolman.local:7912
```

### 2. Verify Environment Variables

The deployment script should save these to `.deployment.config`:

```bash
cat .deployment.config | grep "PFARM__"
```

Expected output:
```
PFARM__Spoolman__BaseUrl=http://spoolman.local:7912
PFARM__NetworkDiscovery__EnableDiscovery=true
PFARM__NetworkDiscovery__DiscoverySubnets=10.0.0.0/24,192.168.1.0/24
```

### 3. Verify Container Environment

Check that the API container receives the environment variables:

```bash
# For microservices
docker exec printfarmer-api printenv | grep "PFARM__"

# For monolith
docker exec printfarmer-web printenv | grep "PFARM__"
```

### 4. Verify Database Initialization

Check API logs for settings initialization:

```bash
docker logs printfarmer-api | grep "Settings initialization"
```

Expected output:
```
[Startup] Settings initialization from environment variables completed
```

### 5. Verify Setup Wizard Pre-population

1. Open browser: `http://your-server:8080`
2. Navigate through Setup Wizard:
   - Step 1: Create admin account
   - **Step 2: Network Settings** → Should show pre-filled subnets ✓
   - **Step 3: Spoolman** → Should show pre-filled URL ✓
3. Values should match what you entered in deploy script

### 6. Verify Settings Persistence

Change a setting in the Setup Wizard:
1. Modify Spoolman URL to `http://different:7912`
2. Complete wizard
3. Restart container:
   ```bash
   docker restart printfarmer-api
   ```
4. Check logs - should NOT re-initialize from env vars
5. Settings should persist with your modified value

## Expected Behavior

### ✅ First Run (Fresh Database)
- API reads `PFARM__Spoolman__BaseUrl` from environment
- Creates SpoolmanSettings in database with that value
- Setup Wizard fetches settings → shows pre-filled URL
- User can review and modify if needed

### ✅ Subsequent Runs (Existing Database)
- API checks database → settings exist
- Skips environment variable initialization
- Setup Wizard fetches from database → shows current values
- Modified settings persist across restarts

## Troubleshooting

### Settings Not Pre-filled

**Check**: Environment variables set correctly
```bash
docker exec printfarmer-api printenv | grep PFARM__
```

**Check**: API logs for initialization errors
```bash
docker logs printfarmer-api | grep -i "settings"
```

### Settings Reverting to Env Vars

**Symptom**: Modified settings reset to deployment values on restart

**Cause**: Database not persisting (volume issue)

**Fix**: Verify docker volume:
```bash
docker volume inspect printfarmer_app_data
```

### Setup Wizard Shows Empty Fields

**Check 1**: API endpoint returns settings
```bash
curl http://localhost:5245/api/settings/Spoolman
```

**Check 2**: Browser console for fetch errors
```
F12 → Console → Look for API errors
```

## Architecture Flow

```
1. deploy-docker.sh
   ├─ Prompts user for Spoolman URL
   ├─ Saves to .deployment.config
   └─ Exports PFARM__Spoolman__BaseUrl=...

2. docker-compose.yml
   ├─ Passes PFARM__* env vars to container
   └─ Container receives: PFARM__Spoolman__BaseUrl=http://spoolman:7912

3. API Startup (Program.cs)
   ├─ SettingsInitializationService.InitializeFromEnvironment<SpoolmanSettings>()
   ├─ Checks database → empty
   ├─ Reads PFARM__Spoolman__BaseUrl from IConfiguration
   ├─ Creates SpoolmanSettings { BaseUrl = "http://spoolman:7912" }
   └─ Saves to database via ISettingsService

4. Setup Wizard (Browser)
   ├─ Fetches: GET /api/settings/Spoolman
   ├─ Receives: { "baseUrl": "http://spoolman:7912" }
   └─ Pre-fills form field with value
```

## Configuration Override Hierarchy

1. **Deployment Config** (lowest priority)
   - Used only if database is empty
   - Source: `PFARM__*` environment variables

2. **Database Settings** (highest priority)
   - Used if settings exist in database
   - Can be modified via Setup Wizard or Settings UI
   - Persists across container restarts

This ensures:
- Fresh deployments get sensible defaults from deploy script
- User modifications persist and aren't overwritten
- Single source of truth (database) after initialization
