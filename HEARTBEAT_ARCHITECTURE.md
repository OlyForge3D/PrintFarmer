# Printer Discovery Service Heartbeat Architecture

## Overview

The printer discovery service now implements an active health monitoring system to confirm it's running and alive. This prevents the UI from showing the "Discover Printers" button when the service is disabled or not responding.

## Architecture

### 1. Discovery Service (Sender)
**Location**: `src/printer-discovery/`

#### HeartbeatBackgroundService
- **File**: `BackgroundServices/HeartbeatBackgroundService.cs`
- **Function**: Continuously sends heartbeat signals to the API
- **Interval**: Configurable via `Discovery:HeartbeatIntervalSeconds` (default: 30 seconds)
- **Behavior**:
  - Waits 5 seconds after startup before first heartbeat
  - Sends HTTP POST to `{API_URL}/api/settings/NetworkDiscovery/heartbeat`
  - Includes current UTC timestamp in payload
  - Handles timeouts gracefully (10-second timeout per request)
  - Logs failures but continues retrying on next interval
  - Runs indefinitely as a hosted service

#### Configuration
**File**: `appsettings.json`
```json
{
  "Discovery": {
    "ApiBaseUrl": "http://api:5245",
    "HeartbeatIntervalSeconds": 30,
    ...
  }
}
```

### 2. API Server (Receiver)
**Location**: `src/api/`

#### NetworkDiscoverySettings
**File**: `infra/Settings/NetworkDiscoverySettings.cs`
- Added property: `DateTime? LastHeartbeat { get; set; }`
- Stored in database as part of app settings
- Updated on each heartbeat received
- Initially null (not set until first heartbeat)

#### UnifiedSettingsController Heartbeat Endpoint
**File**: `Controllers/UnifiedSettingsController.cs`
- **Endpoint**: `POST /api/settings/NetworkDiscovery/heartbeat`
- **Function**: Receives heartbeat from discovery service
- **Behavior**:
  - Updates `LastHeartbeat` timestamp to current UTC time
  - Saves updated settings to database
  - Returns 204 NoContent on success
  - Validates only works for NetworkDiscovery settings
  - Logs debug message for audit trail

### 3. React Frontend (Consumer)
**Location**: `src/Web/ReactApp/src/pages/admin/PrintersAdminPage.tsx`

#### Discovery Availability Detection
```typescript
const checkDiscoveryAvailability = async () => {
  const settings = await apiClient.getSettings('NetworkDiscovery');
  
  // Discovery is available if BOTH conditions are met:
  // 1. EnableDiscovery is true (configuration flag)
  // 2. LastHeartbeat is recent (within 2 minutes)
  
  const isEnabled = settings?.enableDiscovery === true;
  const hasRecentHeartbeat = settings?.lastHeartbeat 
    ? new Date().getTime() - new Date(settings.lastHeartbeat).getTime() < 120000
    : false;
  
  setDiscoveryAvailable(isEnabled && hasRecentHeartbeat);
};
```

**Logic**:
- Shows "Discover Printers" button only if both conditions are true:
  1. `EnableDiscovery` is true (configuration enabled)
  2. `LastHeartbeat` is within 2 minutes (service is responding)
- If service crashes or stops, heartbeat will expire after 2 minutes
- Logs warning to console if enabled but not responding

## Data Flow

```
┌─────────────────────────────────────────────────────────────────┐
│ DISCOVERY SERVICE                                               │
│ ┌──────────────────────────────────────────────────────────┐   │
│ │ HeartbeatBackgroundService                               │   │
│ │ ├─ Every 30 seconds (configurable)                       │   │
│ │ ├─ POST /api/settings/NetworkDiscovery/heartbeat         │   │
│ │ └─ Payload: { timestamp: DateTime.UtcNow }              │   │
│ └──────────────────────────────────────────────────────────┘   │
└─────────────────────────┬──────────────────────────────────────┘
                          │ HTTP POST
                          ▼
┌─────────────────────────────────────────────────────────────────┐
│ API SERVER                                                      │
│ ┌──────────────────────────────────────────────────────────┐   │
│ │ UnifiedSettingsController.SendHeartbeat()                │   │
│ │ ├─ Receives heartbeat request                            │   │
│ │ ├─ Gets NetworkDiscoverySettings from database           │   │
│ │ ├─ Updates LastHeartbeat = DateTime.UtcNow              │   │
│ │ ├─ Saves to database                                     │   │
│ │ └─ Returns 204 NoContent                                 │   │
│ └──────────────────────────────────────────────────────────┘   │
│ ┌──────────────────────────────────────────────────────────┐   │
│ │ Database (Settings)                                      │   │
│ │ └─ NetworkDiscoverySettings.LastHeartbeat ◄──────────────┤   │
│ └──────────────────────────────────────────────────────────┘   │
└─────────────────────────┬──────────────────────────────────────┘
                          │ GET /api/settings/NetworkDiscovery
                          ▼
┌─────────────────────────────────────────────────────────────────┐
│ REACT FRONTEND (Admin Panel)                                    │
│ ┌──────────────────────────────────────────────────────────┐   │
│ │ PrintersAdminPage.checkDiscoveryAvailability()          │   │
│ │ ├─ Fetches NetworkDiscoverySettings                      │   │
│ │ ├─ Checks: enableDiscovery === true                      │   │
│ │ ├─ Checks: LastHeartbeat within 2 minutes               │   │
│ │ ├─ Shows/hides "Discover Printers" button                │   │
│ │ └─ Conditionally renders PrinterDiscoveryModal           │   │
│ └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

## Timing Details

### Heartbeat Intervals
- **Discovery Service**: Sends heartbeat every 30 seconds (default)
- **React Frontend**: Considers service alive if heartbeat ≤ 2 minutes old
- **Grace Period**: Up to 2 minutes after service stops to show UI change

### Startup Sequence
1. Discovery service starts
2. Waits 5 seconds for system stabilization
3. First heartbeat sent (~5 seconds after startup)
4. LastHeartbeat set in API database
5. React detects recent heartbeat on next check

### Failure Scenarios

**Service Crashes**:
- Heartbeat stops being sent
- LastHeartbeat becomes stale (> 2 minutes)
- React hides "Discover Printers" button after 2 minutes
- No error to user, just missing button

**Service Disabled** (`EnableDiscovery: false`):
- Heartbeat background service may still run
- React check fails on `enableDiscovery === true`
- Button hidden immediately via configuration

**Network Disconnection**:
- Heartbeat request times out (10 seconds)
- Service logs warning
- Retries on next interval
- After 2 minutes of failures, React hides button

**API Unreachable**:
- Heartbeat service logs warning
- Continues sending heartbeats
- LastHeartbeat may become stale if persistent

## Configuration

### Environment Variables (Override in Docker/Local)
```bash
# Discovery service heartbeat interval (seconds)
Discovery__HeartbeatIntervalSeconds=30

# API base URL for discovery service
Discovery__ApiBaseUrl=http://api:5245

# Enable/disable network discovery
PFARM__NetworkDiscovery__EnableDiscovery=true
```

### Default Values
- Heartbeat Interval: 30 seconds
- API Timeout: 10 seconds per heartbeat
- Frontend Staleness Threshold: 120 seconds (2 minutes)
- Startup Delay: 5 seconds

## Testing

### Local Development
```bash
# Start all services with discovery enabled
./scripts/start-all-local.sh --fresh

# Verify heartbeat working
curl -s http://localhost:5245/api/settings/NetworkDiscovery | jq '.lastHeartbeat'

# Should show a recent timestamp (current UTC time)
# If null or old, service may not be responding
```

### Disabling Discovery
```bash
# Manually disable in deploy script prompt or via environment
export PFARM__NetworkDiscovery__EnableDiscovery=false

# React button disappears immediately (config-based)
```

### Service Failure Simulation
```bash
# Kill discovery service manually
# Button disappears after 2 minutes

# Or restart discovery service
# Button reappears after ~35 seconds (5s startup + 30s interval)
```

## Benefits

1. **Active Health Monitoring**: API knows service state in real-time
2. **UI Reliability**: "Discover Printers" button only shows when service is actually running
3. **Graceful Degradation**: No errors, just missing button if service unavailable
4. **Network-Aware**: Handles network timeouts and disconnections
5. **Configurable**: All intervals adjustable without code changes
6. **Audit Trail**: Heartbeat timestamps recorded in database for diagnostics

## Future Enhancements

- Dashboard indicator showing discovery service status
- Admin panel showing last heartbeat time and service health
- Alert/notification if discovery service is enabled but not responding
- Automatic recovery/restart of discovery service if stopped
- Metrics collection for heartbeat response times
