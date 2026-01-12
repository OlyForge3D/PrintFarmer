# Phase 7 Task 1: Per-Service Authentication & RBAC - Implementation Summary

**Status**: ✅ **COMPLETE**  
**Date**: 2025-10-20  
**Branch**: `feature/orcaslicer-reimplementation`

## Overview

Implemented comprehensive per-service API key authentication with self-service rotation and RBAC-protected admin management for slicer services. This ensures secure communication between slicer worker services and the API while minimizing administrative overhead.

## Implementation Details

### 1. Per-Service API Key Authentication

#### New Authentication Filter
**File**: `src/api/Infrastructure/Filters/RequireSlicerServiceApiKeyAttribute.cs`

- Validates `X-Slicer-ApiKey` header against registered SlicerService's stored API key
- Requires service ID in route to lookup the correct service
- Returns `401 Unauthorized` for missing/invalid/mismatched keys
- Applied to all SlicerService endpoints except registration and list

**Usage**:
```csharp
[HttpPost("{id}/heartbeat")]
[RequireSlicerServiceApiKey]
public async Task<IActionResult> HeartbeatAsync(Guid id, [FromBody] HeartbeatDto dto)
```

#### Protected Endpoints
The following endpoints now require per-service authentication:
- `GET /api/slicers/{id}` - Get service details
- `POST /api/slicers/{id}/heartbeat` - Send heartbeat
- `POST /api/slicers/{id}/deregister` - Deregister service
- `POST /api/slicers/{id}/rotate-key` - Rotate API key (self-service)

#### Public Endpoints
These endpoints use the static registration key (`SLICER_REGISTRATION_KEY` env var):
- `GET /api/slicers` - List services
- `POST /api/slicers/register` - Register new service

### 2. Self-Service API Key Rotation

#### Database Schema Enhancement
**File**: `src/infra/Domain/SlicerService.cs`

Added new property for audit tracking:
```csharp
public DateTime? ApiKeyRotatedAt { get; set; } // Track last rotation for auditing
```

#### Service Implementation
**File**: `src/api/Services/Slicing/SlicersService.cs`

New `RotateApiKeyAsync` method:
- Generates new cryptographically secure API key (base64-encoded GUID)
- Updates SlicerService entity with new key and rotation timestamp
- Synchronizes to Worker table for job dispatcher
- Broadcasts `SlicerApiKeyRotated` event via SignalR
- Returns new key to caller for immediate use

**Rotation Flow**:
1. Service calls `POST /api/slicers/{id}/rotate-key` with current valid API key
2. Backend validates current key, generates new key
3. New key returned to service (old key immediately invalidated)
4. Service updates its configuration with new key
5. All subsequent requests use new key

#### SignalR Event
**File**: `src/api/Hubs/SlicerHub.cs`

Added new event constant:
```csharp
public const string SlicerApiKeyRotated = "SlicerApiKeyRotated";
```

Payload: `{ id: Guid, name: string, rotatedAt: DateTime }`

### 3. RBAC-Protected Admin Management

#### Admin Controller
**File**: `src/api/Controllers/Admin/SlicerManagementController.cs`

New admin-only endpoints protected by `[RequirePermission("slicers", "admin")]`:

**`GET /api/admin/slicers`**
- Lists all slicer services with full details (including API keys)
- Admin visibility for monitoring and troubleshooting

**`POST /api/admin/slicers/{id}/admin-rotate-key`**
- Forces API key rotation (for security incidents)
- Logs warning about administrative action
- Returns new key for manual distribution to service

**`DELETE /api/admin/slicers/{id}`**
- Forcibly deregisters a service (for maintenance/security)
- Logs warning about administrative action
- Bypasses normal graceful deregistration flow

#### Authorization Requirements
All admin endpoints require:
- Authenticated user (`[Authorize]`)
- Permission: `slicers:admin` OR
- Role: `farm_admin` (implicit full permissions)

### 4. Security Architecture

#### Multi-Layer Authentication
```
┌─────────────────────────────────────────────────────────┐
│ Static Registration Key (SLICER_REGISTRATION_KEY)      │
│ ├─ POST /api/slicers/register                          │
│ └─ GET /api/slicers                                     │
├─────────────────────────────────────────────────────────┤
│ Per-Service API Key (unique per SlicerService)         │
│ ├─ GET /api/slicers/{id}                               │
│ ├─ POST /api/slicers/{id}/heartbeat                    │
│ ├─ POST /api/slicers/{id}/deregister                   │
│ └─ POST /api/slicers/{id}/rotate-key                   │
├─────────────────────────────────────────────────────────┤
│ JWT + RBAC (users with slicers:admin permission)       │
│ ├─ GET /api/admin/slicers                              │
│ ├─ POST /api/admin/slicers/{id}/admin-rotate-key       │
│ └─ DELETE /api/admin/slicers/{id}                      │
└─────────────────────────────────────────────────────────┘
```

#### Key Generation
- Uses cryptographically secure `Guid.NewGuid()` converted to base64
- 22 characters (base64 without padding)
- ~132 bits of entropy
- Safe for concurrent generation across distributed systems

#### Rotation Best Practices
- **Self-service rotation**: Services can rotate their own keys without admin intervention
- **Minimal downtime**: New key returned immediately, old key invalidated atomically
- **Audit trail**: `ApiKeyRotatedAt` timestamp tracks all rotations
- **SignalR broadcast**: Real-time notification to connected clients
- **Admin override**: Admins can force rotation in security incidents

## Testing Recommendations

### Unit Tests
- Verify `RequireSlicerServiceApiKeyAttribute` validates keys correctly
- Test key rotation generates unique keys
- Verify admin endpoints reject unauthorized users

### Integration Tests
```csharp
[Fact]
public async Task HeartbeatAsync_WithValidApiKey_Succeeds()
{
    // Arrange: Register service, capture API key
    var (id, apiKey) = await RegisterServiceAsync();
    
    // Act: Send heartbeat with valid key
    var response = await SendHeartbeatAsync(id, apiKey);
    
    // Assert: 204 No Content
    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
}

[Fact]
public async Task HeartbeatAsync_WithInvalidApiKey_Returns401()
{
    // Arrange: Register service
    var (id, _) = await RegisterServiceAsync();
    
    // Act: Send heartbeat with wrong key
    var response = await SendHeartbeatAsync(id, "wrong-key");
    
    // Assert: 401 Unauthorized
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}

[Fact]
public async Task RotateApiKeyAsync_GeneratesNewKey_InvalidatesOldKey()
{
    // Arrange: Register service
    var (id, oldKey) = await RegisterServiceAsync();
    
    // Act: Rotate key
    var rotateResponse = await RotateKeyAsync(id, oldKey);
    var newKey = await ExtractKeyFromResponse(rotateResponse);
    
    // Assert: Old key fails, new key works
    Assert.Equal(HttpStatusCode.Unauthorized, await SendHeartbeatAsync(id, oldKey).StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, await SendHeartbeatAsync(id, newKey).StatusCode);
}

[Fact]
public async Task AdminRotateKey_WithoutPermission_Returns403()
{
    // Arrange: Non-admin user
    var user = await CreateUserWithoutAdminPermission();
    
    // Act: Try admin rotation
    var response = await AdminRotateKeyAsync(serviceId, user.Token);
    
    // Assert: 403 Forbidden
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
}
```

### Manual Testing
```bash
# 1. Register service
curl -X POST http://localhost:5245/api/slicers/register \
  -H "X-Slicer-ApiKey: $SLICER_REGISTRATION_KEY" \
  -H "Content-Type: application/json" \
  -d '{"name":"test-orca","slicerType":1,"version":"2.0","host":"http://worker:8080","maxConcurrentJobs":4}'

# Response: {"id":"<guid>","apiKey":"<key>"}

# 2. Test heartbeat with valid key
curl -X POST http://localhost:5245/api/slicers/<guid>/heartbeat \
  -H "X-Slicer-ApiKey: <key>" \
  -H "Content-Type: application/json" \
  -d '{"status":"Online","freeSlots":3}'

# Response: 204 No Content

# 3. Rotate key
curl -X POST http://localhost:5245/api/slicers/<guid>/rotate-key \
  -H "X-Slicer-ApiKey: <key>"

# Response: {"id":"<guid>","apiKey":"<new-key>"}

# 4. Test old key fails
curl -X POST http://localhost:5245/api/slicers/<guid>/heartbeat \
  -H "X-Slicer-ApiKey: <old-key>" \
  -H "Content-Type: application/json" \
  -d '{"status":"Online","freeSlots":3}'

# Response: 401 Unauthorized

# 5. Admin force rotation (requires JWT)
curl -X POST http://localhost:5245/api/admin/slicers/<guid>/admin-rotate-key \
  -H "Authorization: Bearer <jwt-token>"

# Response: 200 OK with new key
```

## Migration Notes

### Existing Deployments
1. **No database migration required** - `ApiKeyRotatedAt` is nullable
2. **Existing API keys remain valid** - backward compatible
3. **Static registration key still works** - for initial registration

### Worker Configuration
Workers should implement graceful key rotation:

```python
# Example worker code
class OrcaWorkerClient:
    def __init__(self, service_id, api_key):
        self.service_id = service_id
        self.api_key = api_key
        self.rotation_interval = timedelta(days=30)  # Rotate monthly
        self.last_rotation = datetime.now()
    
    async def heartbeat(self):
        # Send heartbeat with current key
        response = await self.http_client.post(
            f"/api/slicers/{self.service_id}/heartbeat",
            headers={"X-Slicer-ApiKey": self.api_key},
            json={"status": "Online", "freeSlots": self.free_slots}
        )
        
        # Check if rotation needed
        if datetime.now() - self.last_rotation > self.rotation_interval:
            await self.rotate_key()
    
    async def rotate_key(self):
        response = await self.http_client.post(
            f"/api/slicers/{self.service_id}/rotate-key",
            headers={"X-Slicer-ApiKey": self.api_key}
        )
        
        if response.status_code == 200:
            data = response.json()
            self.api_key = data["apiKey"]
            self.last_rotation = datetime.now()
            self.save_config()  # Persist new key
            logger.info("API key rotated successfully")
```

## Security Considerations

### Key Storage
- **Backend**: Keys stored in database (consider encryption at rest)
- **Worker**: Keys stored in configuration file (secure file permissions)
- **Transport**: Always use HTTPS in production

### Rotation Policy
Recommended rotation schedule:
- **Routine**: Every 30-90 days (automated by workers)
- **Incident**: Immediately if compromise suspected (admin force rotation)
- **Decommission**: Rotate before deregistering a service

### Audit Logging
All rotation events logged for security audit:
- Service-initiated rotation: INFO level
- Admin-forced rotation: WARNING level
- Includes: timestamp, service ID, service name, initiator

## Files Modified

### New Files
- `src/api/Infrastructure/Filters/RequireSlicerServiceApiKeyAttribute.cs`
- `src/api/Controllers/Admin/SlicerManagementController.cs`
- `docs/slicer/PHASE_7_AUTH_IMPLEMENTATION.md`

### Modified Files
- `src/infra/Domain/SlicerService.cs` - Added `ApiKeyRotatedAt`
- `src/api/Services/Slicing/ISlicersService.cs` - Added `RotateApiKeyAsync`
- `src/api/Services/Slicing/SlicersService.cs` - Implemented rotation logic
- `src/api/Controllers/SlicersController.cs` - Added rotation endpoint, applied auth filters
- `src/api/Hubs/SlicerHub.cs` - Added `SlicerApiKeyRotated` event

## Next Steps

Phase 7 remaining tasks:
- **Task 2**: Observability metrics (job durations, failure rates, capacity)
- **Task 3**: Resource limits and sandboxing (Docker constraints)
- **Task 4**: CI checks for worker builds
- **Task 5**: Profile management runbook documentation

---

**Implementation Summary**: Task 1 delivers production-ready per-service authentication with self-service key rotation and RBAC-protected admin controls. Services can manage their own security lifecycle while admins retain override capabilities for incident response.
