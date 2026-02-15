# Setup Wizard & Deployment Script Integration Plan

## Problem Statement

Currently, users are asked for the same configuration twice:
1. During `deploy-docker.sh` execution (Spoolman URL, network subnets, ports)
2. During Setup Wizard in the browser (same settings again)

This creates a poor UX and potential inconsistency between deployment config and application settings.

## Solution Overview

**Single Source of Truth**: Configuration entered during `deploy-docker.sh` should be used to pre-populate application settings, which the Setup Wizard then displays (pre-filled) for user review and optional modification.

## Implementation Strategy

### Phase 1: Environment Variable → Settings Initialization

**Goal**: Allow IAppSetting implementations to initialize from environment variables on first load.

#### 1.1 Update Settings Service

Modify `SettingsService` (or create initialization middleware) to:
- Check if settings exist in database
- If not, check for corresponding environment variables
- Initialize settings with env var values as defaults
- Save to database

**Environment Variable Naming Convention**:
```bash
# Pattern: PFARM__{SettingKey}__{PropertyName}
PFARM__Spoolman__BaseUrl=http://spoolman.local:7912
PFARM__NetworkDiscovery__DiscoverySubnets=["10.0.0.0/24","192.168.1.0/24"]
PFARM__NetworkDiscovery__Ports=[80,7125,8080]
```

#### 1.2 Deploy Script Updates

**File**: `scripts/deploy-docker.sh`

Add to deployment config saved settings:
```bash
# Application Settings - Pre-populate Setup Wizard
PFARM__Spoolman__BaseUrl=${SPOOLMAN_BASE_URL:-}
PFARM__NetworkDiscovery__DiscoverySubnets=$(printf '%q' "${NETWORK_RANGES:-}")
PFARM__NetworkDiscovery__Ports=$(printf '%q' "${DISCOVERY_PORTS:-[80,7125,8080]}")
```

These environment variables will be:
1. Saved to `.deployment.config` for persistence
2. Passed to Docker containers via docker-compose environment section
3. Used by SettingsService to initialize database settings on first run

#### 1.3 Docker Compose Integration

**For Microservices** (`docker-compose.microservices.yml`):
```yaml
services:
  api:
    environment:
      - PFARM__Spoolman__BaseUrl=${PFARM__Spoolman__BaseUrl:-}
      - PFARM__NetworkDiscovery__DiscoverySubnets=${PFARM__NetworkDiscovery__DiscoverySubnets:-}
      - PFARM__NetworkDiscovery__Ports=${PFARM__NetworkDiscovery__Ports:-}
```

**For Monolith** (`docker-compose.yml`):
```yaml
services:
  web:
    environment:
      - PFARM__Spoolman__BaseUrl=${PFARM__Spoolman__BaseUrl:-}
      - PFARM__NetworkDiscovery__DiscoverySubnets=${PFARM__NetworkDiscovery__DiscoverySubnets:-}
      - PFARM__NetworkDiscovery__Ports=${PFARM__NetworkDiscovery__Ports:-}
```

### Phase 2: Setup Wizard Pre-population

**Goal**: Setup Wizard reads existing settings from database (which were initialized from env vars) and displays them pre-filled.

#### 2.1 Settings API Enhancement

**Endpoint**: `GET /api/settings/{keyName}`

Already exists! The UnifiedSettingsController already supports:
- `GET /api/settings/Spoolman` - Returns SpoolmanSettings with BaseUrl
- `GET /api/settings/NetworkDiscovery` - Returns NetworkDiscoverySettings with subnets/ports

#### 2.2 Setup Wizard Component Updates

**File**: `src/Web/ReactApp/src/components/SetupWizard.tsx`

Current flow:
```tsx
// Step 2: Network - Already fetches settings!
useEffect(() => {
  apiClient.getSettings<NetworkDiscoverySettings>('NetworkDiscovery')
    .then(settings => setNetworkDiscoverySettings(settings))
    .catch(() => { /* fallback */ });
}, []);

// Step 3: Spoolman - MISSING: Should fetch settings
const [spoolmanUrl, setSpoolmanUrl] = useState('');
```

**Enhancement needed**:
```tsx
// Step 3: Spoolman - Fetch settings on mount
useEffect(() => {
  apiClient.getSettings<SpoolmanSettings>('Spoolman')
    .then(settings => {
      if (settings?.baseUrl) {
        setSpoolmanUrl(settings.baseUrl);
        setSpoolmanEnabled(true);
      }
    })
    .catch(() => { /* use empty defaults */ });
}, []);
```

#### 2.3 User Experience Flow

1. **Deployment** (via script):
   ```bash
   ./scripts/deploy-docker.sh
   # User enters: Spoolman URL, network subnets, discovery ports
   # These are saved to .deployment.config and passed as env vars
   ```

2. **First Container Start**:
   - API starts, SettingsService initializes
   - Checks database for SpoolmanSettings → not found
   - Reads `PFARM__Spoolman__BaseUrl` environment variable
   - Creates SpoolmanSettings with BaseUrl from env var
   - Saves to database
   - Same for NetworkDiscoverySettings

3. **Setup Wizard** (browser):
   - User navigates to http://server:8080
   - Setup Wizard loads
   - Step 2 (Network): Fetches NetworkDiscoverySettings → pre-filled with subnets/ports from deploy script ✅
   - Step 3 (Spoolman): Fetches SpoolmanSettings → pre-filled with URL from deploy script ✅
   - User can review and modify if needed
   - On submit: Saves any modifications back to settings

### Phase 3: Advanced - Read-Only vs Editable

**Optional Enhancement**: Some settings might be deployment-locked (read-only in wizard).

**Approach**:
- Add `DeploymentConfigured` flag to settings
- If setting was initialized from env var, mark as `DeploymentConfigured = true`
- Setup Wizard shows these as "Configured during deployment" with ability to override
- Show a toggle: "Use deployment configuration" vs "Customize"

**UI Example**:
```tsx
<div className="setting-field">
  <label>Spoolman Base URL</label>
  {deploymentConfigured ? (
    <div className="deployment-notice">
      ✓ Configured during deployment: {spoolmanUrl}
      <button onClick={() => setCustomize(true)}>Customize</button>
    </div>
  ) : (
    <input value={spoolmanUrl} onChange={...} />
  )}
</div>
```

## Implementation Checklist

### Backend

- [ ] Create `SettingsInitializationMiddleware` or enhance `SettingsService`
  - [ ] Read environment variables matching `PFARM__{Key}__{Property}` pattern
  - [ ] Initialize settings if not in database
  - [ ] Support JSON arrays for collections (subnets, ports)
  
- [ ] Update `SpoolmanSettings.cs`
  - [ ] Add `DeploymentConfigured` property (optional)
  
- [ ] Update `NetworkDiscoverySettings.cs`
  - [ ] Already exists, just ensure env var initialization works

### Deployment Scripts

- [ ] Update `scripts/deploy-docker.sh`
  - [ ] Add Spoolman URL prompt section (if not already exists)
  - [ ] Add network discovery ports prompt
  - [ ] Format arrays as JSON for env vars
  - [ ] Add to `.deployment.config` save section
  - [ ] Pass to Docker Compose as environment variables

- [ ] Update `docker-compose.yml` (monolith)
  - [ ] Add `PFARM__Spoolman__BaseUrl` environment variable
  - [ ] Add `PFARM__NetworkDiscovery__DiscoverySubnets`
  - [ ] Add `PFARM__NetworkDiscovery__Ports`

- [ ] Update `docker-compose.microservices.yml`
  - [ ] Add same environment variables to `api` service

- [ ] Update `docker-compose.host-network.yml` generation
  - [ ] Include environment variables in generated config

### Frontend

- [ ] Update `SetupWizard.tsx`
  - [ ] Step 3: Fetch SpoolmanSettings on mount
  - [ ] Pre-populate spoolmanUrl and spoolmanEnabled from fetched settings
  - [ ] Step 2: Already fetches NetworkDiscoverySettings ✅
  - [ ] Add UI indicators for "Deployment Configured" settings (optional)

- [ ] Create TypeScript type for `SpoolmanSettings`
  - [ ] File: `src/Web/ReactApp/src/types/SpoolmanSettings.ts`
  - [ ] Match C# SpoolmanSettings structure

## Benefits

1. **Single Configuration Point**: Users configure once during deployment
2. **Reviewable Defaults**: Setup Wizard shows what was configured, allows changes
3. **Consistent State**: Deployment config and application settings stay in sync
4. **Better UX**: No duplicate questions, clear source of truth
5. **Flexibility**: Users can still customize during wizard if needed
6. **Documentation**: Deployment config file serves as record of initial configuration

## Migration Path

**Existing Deployments**: Will continue to work
- Settings service falls back to empty defaults if env vars not present
- Setup Wizard still allows manual configuration
- No breaking changes

**New Deployments**: Enhanced workflow
- Deploy script asks for all config upfront
- Settings auto-initialized from env vars
- Setup Wizard shows pre-populated values

## Alternative Approaches Considered

### ❌ Skip Setup Wizard Entirely
**Rejected**: Setup Wizard still needed for:
- Admin account creation
- Reviewing auto-detected settings
- Advanced configuration options
- Filament preset selection

### ❌ Settings Only from Env Vars (No Database)
**Rejected**: 
- Settings need to be runtime-modifiable via UI
- Database persistence allows settings changes without redeployment
- Hybrid approach (env var initialization + database storage) provides best of both

### ✅ Hybrid Approach (Chosen)
- Env vars initialize database settings on first run
- Database stores current state (can be modified via UI)
- Setup Wizard reads from database (pre-populated from env vars)
- Best balance of deployment automation and runtime flexibility

## Testing Strategy

1. **Fresh Deployment Test**:
   ```bash
   ./scripts/deploy-docker.sh --tear-down
   # Enter: Spoolman URL = http://spoolman.local:7912
   # Enter: Network subnets = 10.0.0.0/24,192.168.1.0/24
   ./scripts/deploy-docker.sh
   # Verify: Container starts with env vars
   # Verify: Database settings initialized
   # Verify: Setup Wizard shows pre-filled values
   ```

2. **Modification Test**:
   - Change Spoolman URL in Setup Wizard
   - Save changes
   - Verify: Database updated
   - Verify: API uses new URL
   - Restart container
   - Verify: Modified settings persist (not overwritten by env vars)

3. **Upgrade Test**:
   - Deploy without env vars (old method)
   - Verify: Setup Wizard still works with empty defaults
   - Manually configure settings
   - Restart container
   - Verify: Settings persist

## Timeline

- **Phase 1** (Backend): ~4 hours
  - Settings initialization from env vars
  - Docker Compose updates
  
- **Phase 2** (Frontend): ~2 hours
  - Setup Wizard pre-population
  - Type definitions
  
- **Phase 3** (Script): ~3 hours
  - Deploy script enhancements
  - Configuration persistence
  
- **Testing**: ~2 hours

**Total**: ~11 hours

## Success Criteria

- [ ] User configures Spoolman URL once (in deploy script)
- [ ] Setup Wizard shows pre-filled Spoolman URL
- [ ] User configures network subnets once (in deploy script)
- [ ] Setup Wizard shows pre-filled network subnets
- [ ] Settings persist across container restarts
- [ ] User can modify settings in Setup Wizard (overrides deployment config)
- [ ] Documentation updated with new workflow
