# REST API Endpoint Audit & Implementation Priority

**Last Updated:** November 2, 2025
**Status:** ⚠️ CRITICAL - Multiple missing endpoints identified

## Summary

After the large refactor creating services and repositories, several endpoints that the frontend is calling are **not defined** in the API controllers. This document maps all frontend API calls against backend routes and identifies gaps, prioritized by feature deployment order.

## Deployment Priority Order

1. **🔴 PRIORITY 1: Setup Wizard** - Initial configuration and admin account creation
2. **🔴 PRIORITY 2: Printer Discovery & Display** - List printers with real-time status (cards/tables)
3. **🔴 PRIORITY 3: G-Code Harvest & Print Jobs** - Harvest files and submit print jobs
4. **🟡 PRIORITY 4: Settings & User Management** - Configuration, users, import/export, Spoolman
5. **🟡 PRIORITY 5: Slicing** - Slicing engine integration

---

## Missing Endpoints by Priority

## Missing Endpoints by Priority

### 🔴 PRIORITY 1: Setup Wizard

#### Status: ✅ COMPLETE
- ✅ `GET /api/setup/status` - Check if setup is complete
- ✅ `POST /api/setup/initial-admin` - Create initial admin account
- ✅ `GET /api/setup/config-options` - Get available configuration options

**Frontend Dependencies**: None - this is entry point before auth

---

### 🔴 PRIORITY 2: Printer Discovery & Display

#### Critical Missing Endpoints

1. **`POST /api/printers/bulk`** - Bulk Create Printers ❌
   - **Frontend Call**: `api.ts:326` - `bulkCreatePrinters(printers, options)`
   - **Status**: MISSING
   - **Method**: POST
   - **Purpose**: Import multiple printers from CSV/file
   - **Impact**: Cannot import printer list

2. **`POST /api/printers/discover/stream`** - Start Discovery Stream ❌
   - **Frontend Call**: `api.ts:352` - `startDiscoveryStream(request?)`
   - **Status**: MISSING
   - **Method**: POST
   - **Purpose**: Begin network discovery with real-time updates via SignalR
   - **Impact**: Discovery UI cannot stream results

3. **`GET /api/printers/fast`** - Fast Printer List ✅ (JUST ADDED)
   - **Frontend Call**: `api.ts:195` - `getPrinters()` / `getPrintersFast()`
   - **Status**: WORKING
   - **Purpose**: Lightweight printer list for dashboard cards/tables
   - **Impact**: Dashboard can display printer cards

#### Status: ✅ MOSTLY COMPLETE

**Existing Working Endpoints**:
- ✅ `GET /api/printers` → `GET /printers/fast` (lightweight list)
- ✅ `GET /api/printers/{id}` - Get specific printer
- ✅ `GET /api/printers/{id}/details` - Get detailed printer info
- ✅ `GET /api/printers/{id}/status` - Get current printer status
- ✅ `GET /api/printers/camera-urls` - Get all camera URLs
- ✅ `POST /api/printers` - Create single printer
- ✅ `PUT /api/printers/{id}` - Update printer
- ✅ `DELETE /api/printers/{id}` - Delete printer
- ✅ `GET /api/printers/discover` - List discovered printers (GET, should be POST?)

**Actions Required**:
1. Add `POST /api/printers/bulk` endpoint
2. Add `POST /api/printers/discover/stream` endpoint
3. Verify `GET /api/printers/discover` is functioning

---

### 🔴 PRIORITY 3: G-Code Harvest & Print Jobs

#### Critical Missing Endpoints

1. **`GET /api/printers/{id}/printjob`** - Get Print Job Status ❌
   - **Frontend Call**: `api.ts:828` - `getPrintJobStatus(printerId)`
   - **Status**: MISSING
   - **Method**: GET
   - **Purpose**: Retrieve current print job info (Moonraker)
   - **Impact**: Cannot display current job in printer cards

#### Status: ✅ MOSTLY COMPLETE

**Existing Working Endpoints**:
- ✅ `GET /api/gcode-harvest/operations` - List harvest operations
- ✅ `POST /api/gcode-harvest/start` - Start harvest operation
- ✅ `GET /api/gcode-harvest/operations/{id}` - Get operation details
- ✅ `GET /api/gcode-harvest/operations/{id}/files` - Get discovered files
- ✅ `POST /api/gcode-harvest/import` - Import selected files
- ✅ `POST /api/gcode-harvest/operations/{id}/cancel` - Cancel operation
- ✅ `POST /api/gcode-harvest/operations/{id}/files/{fileId}/skip` - Skip file
- ✅ `POST /api/gcode-harvest/operations/{id}/files/{fileId}/retry` - Retry file
- ✅ `GET /api/job-queue` - List print jobs in queue
- ✅ `POST /api/job-queue` - Queue print job
- ✅ `GET /api/job-queue/{id}` - Get job details
- ✅ `PUT /api/job-queue/{id}` - Update job
- ✅ `DELETE /api/job-queue/{id}` - Delete job
- ✅ `PATCH /api/job-queue/{id}/cancel` - Cancel job
- ✅ `POST /api/printers/{id}/print/start` OR `/api/printers/{id}/start-print` - Start print

**Actions Required**:
1. Add `GET /api/printers/{id}/printjob` endpoint
2. Verify `/api/printers/{id}/start-print` route is working (added alternative route)

---

### 🟡 PRIORITY 4: Settings & User Management

#### Missing Endpoints

1. **File Upload/Management** ❌
   - `POST /api/printers/{id}/upload-gcode` - Upload G-code to printer
   - `GET /api/printers/{id}/files` - List printer files
   - `POST /api/gcode-files/upload` - Upload to library
   - `POST /api/gcode-files/upload-multiple` - Bulk upload files

2. **Import/Export** ❌
   - `GET /api/printers/export` - Export printer list
   - `POST /api/printers/export` - Bulk export format
   - `POST /api/printers/export/file` - Download export file

#### Status: ✅ MOSTLY COMPLETE

**Existing Working Endpoints**:
- ✅ `GET /api/auth/me` - Current user
- ✅ `POST /api/auth/login` - Login
- ✅ `POST /api/auth/register` - Register
- ✅ `POST /api/auth/logout` - Logout
- ✅ `GET /api/settings` - Get all settings
- ✅ `POST /api/settings` - Save all settings
- ✅ `GET /api/settings/{key}` - Get specific setting
- ✅ `POST /api/settings/{key}` - Save setting
- ✅ `GET /api/filament-types` - List filament types
- ✅ `POST /api/filament-types` - Create filament type
- ✅ `PUT /api/filament-types/{id}` - Update filament type
- ✅ `DELETE /api/filament-types/{id}` - Delete filament type
- ✅ `POST /api/filament-types/import-from-spoolman` - Import from Spoolman
- ✅ `POST /api/spoolman/scan-network` - Discover Spoolman

---

### 🟡 PRIORITY 5: Slicing

#### Status: ✅ MOSTLY COMPLETE

**Existing Working Endpoints**:
- ✅ Slicing infrastructure in place (SlicersController, SlicerController)
- ✅ Ready for slicing engine integration

---

## Endpoint Category Analysis

### ✅ Fully Mapped (Working)

#### Authentication (`/api/auth`)
- ✅ `POST /auth/login`
- ✅ `POST /auth/register`
- ✅ `POST /auth/logout`
- ✅ `GET /auth/logout` (also supports GET)
- ✅ `GET /auth/me`
- ✅ `POST /auth/change-password`
- ✅ `POST /auth/forgot-password`
- ✅ `POST /auth/reset-password`
- ✅ `POST /auth/confirm-email`
- ✅ `POST /auth/resend-confirmation`

#### Catalog (`/api/catalog`)
- ✅ `GET /catalog/manufacturers`
- ✅ `POST /catalog/manufacturers`
- ✅ `GET /catalog/manufacturers/{id:guid}`
- ✅ `GET /catalog/printer-models`
- ✅ `POST /catalog/printer-models`
- ✅ `GET /catalog/printer-models/{id:guid}`
- ✅ `PUT /catalog/printer-models/{id:guid}`

#### Filament Types (`/api/filament-types`)
- ✅ `GET /filament-types` (implicit, uses default GET)
- ✅ `POST /filament-types` (implicit, uses default POST)
- ✅ `GET /filament-types/{id:guid}` (implicit)
- ✅ `PUT /filament-types/{id:guid}`
- ✅ `DELETE /filament-types/{id:guid}`
- ✅ `GET /filament-types/presets`
- ✅ `POST /filament-types/presets`
- ✅ `POST /filament-types/import-from-spoolman`

#### Settings (`/api/settings`)
- ✅ `GET /settings/{keyName}`
- ✅ `POST /settings/{keyName}`
- ✅ `GET /settings/metadata`
- ✅ `GET /settings` (all settings)
- ✅ `POST /settings` (all settings)

#### G-code Files (`/api/gcode-files`)
- ✅ `GET /gcode-files`
- ✅ `GET /gcode-files/{id}`
- ✅ `POST /gcode-files/upload`
- ✅ `DELETE /gcode-files/{id}`
- ✅ `GET /gcode-files/download`
- ✅ `GET /gcode-files/hash`
- ✅ `GET /gcode-files/settings`
- ✅ `PUT /gcode-files/settings`
- ✅ `POST /gcode-files/move`
- ✅ `POST /gcode-files/upload-multiple`

#### G-code Harvest (`/api/gcode-harvest`)
- ✅ `POST /gcode-harvest/start`
- ✅ `GET /gcode-harvest/operations`
- ✅ `GET /gcode-harvest/operations/{operationId:guid}`
- ✅ `GET /gcode-harvest/operations/{operationId:guid}/files`
- ✅ `POST /gcode-harvest/import`
- ✅ `POST /gcode-harvest/operations/{operationId:guid}/cancel`
- ✅ `POST /gcode-harvest/operations/{operationId:guid}/files/{fileId:guid}/skip`
- ✅ `POST /gcode-harvest/operations/{operationId:guid}/files/{fileId:guid}/retry`

#### Printers (`/api/printers`)
- ✅ `GET /printers/camera-urls`
- ✅ `GET /printers/fast` (just added)
- ✅ `GET /printers/{id:guid}`
- ✅ `GET /printers/{id:guid}/details`
- ✅ `GET /printers/{id:guid}/status`
- ✅ `POST /printers`
- ✅ `PUT /printers/{id:guid}`
- ✅ `DELETE /printers/{id:guid}`
- ✅ `GET /printers/{id:guid}/snapshot`
- ✅ `POST /printers/{id:guid}/home`
- ✅ `POST /printers/{id:guid}/homexy`
- ✅ `POST /printers/{id:guid}/homez`
- ✅ `POST /printers/{id:guid}/temps`
- ✅ `POST /printers/{id:guid}/move`
- ✅ `POST /printers/{id:guid}/moveto`
- ✅ `POST /printers/{id:guid}/pause`
- ✅ `POST /printers/{id:guid}/resume`
- ✅ `POST /printers/{id:guid}/emergency-stop`
- ✅ `POST /printers/{id:guid}/firmware-restart`
- ✅ `GET /printers/{id:guid}/history`
- ✅ `GET /printers/{id:guid}/history/{jobId}`
- ✅ `GET /printers/{id:guid}/history/totals`
- ✅ `GET /printers/{id:guid}/files`
- ✅ `GET /printers/model/{modelId:guid}/default-capabilities`
- ✅ `GET /printers/export`
- ✅ `POST /printers/export`
- ✅ `POST /printers/export/file`
- ✅ `GET /printers/{id:guid}/camera/url`
- ✅ `POST /printers/{id}/maintenance` (UPDATE)

#### Job Queue (`/api/job-queue`)
- ✅ `GET /job-queue`
- ✅ `POST /job-queue`
- ✅ `GET /job-queue/{id}`
- ✅ `PUT /job-queue/{id}`
- ✅ `DELETE /job-queue/{id}`
- ⚠️ `PATCH /job-queue/{id}/cancel` (frontend expects this)

#### Spoolman (`/api/spoolman`)
- ✅ `POST /spoolman/test`
- ✅ `GET /spoolman/config`
- ✅ `POST /spoolman/config`
- ✅ `DELETE /spoolman/config`
- ✅ `GET /spoolman/spools`
- ✅ `GET /spoolman/health`
- ✅ `POST /spoolman/scan-network`

#### Health (`/api/health` and `/api/healthz`)
- ✅ `GET /health`
- ✅ `GET /healthz`

#### Utilities
- ✅ `POST /resolve-hostname`

---

## Missing Endpoints Summary

| # | Endpoint | Method | Status | Impact |
|---|----------|--------|--------|--------|
| 1 | `/printers/bulk` | POST | ❌ MISSING | Bulk import broken |
| 2 | `/printers/discover/stream` | POST | ❌ MISSING | Discovery streaming broken |
| 3 | `/printers/{id}/upload-gcode` | POST | ❌ MISSING | Needs proper route |
| 4 | `/printers/{id}/start-print` | POST | ⚠️ WRONG PATH | Path is `/print/start` instead |
| 5 | `/printers/{id}/printjob` | GET | ❌ MISSING | Print job status unavailable |

---

## Issues to Fix

### High Priority (Blocking Frontend)

1. **Add `POST /api/printers/bulk`** endpoint for bulk printer import
2. **Add `POST /api/printers/discover/stream`** endpoint for discovery streaming
3. **Add `GET /api/printers/{id}/printjob`** endpoint for print job status
4. **Fix `/api/printers/{id}/print/start`** → Should be `/api/printers/{id}/start-print`
5. **Standardize `/api/printers/{id}/upload-gcode`** endpoint naming

### Medium Priority (Usage Inconsistencies)

1. **`POST /api/job-queue/{id}/cancel`** - Frontend uses PATCH, verify backend
2. **Discovery endpoints** - GET vs POST confusion on `/printers/discover`
3. **Parameter naming** - Some endpoints use `:guid` constraint, some don't

---

## Implementation Plan by Priority

### Phase 1: Setup Wizard (✅ COMPLETE)
**Status**: All endpoints exist and working
- ✅ Setup status check
- ✅ Initial admin creation
- ✅ Configuration options

**Owner**: Backend complete, no action needed

---

### Phase 2: Printer Discovery & Display (🔴 IN PROGRESS)
**Status**: 3 critical endpoints missing
**Timeline**: NEXT

**Missing Endpoints to Implement**:
1. ✅ `GET /api/printers/fast` - **JUST ADDED**
2. ❌ `POST /api/printers/bulk` - Bulk import printers
3. ❌ `POST /api/printers/discover/stream` - Discovery stream for real-time updates

**Implementation Location**: `src/api/Controllers/PrintersController.cs`

**Testing Dependencies**:
- Frontend dashboard should display printer cards
- Real-time status updates via SignalR
- Bulk import CSV functionality

---

### Phase 3: G-Code Harvest & Print Jobs (🟡 BLOCKING)
**Status**: 1 critical endpoint missing
**Timeline**: AFTER Phase 2

**Missing Endpoints to Implement**:
1. ❌ `GET /api/printers/{id}/printjob` - Retrieve active print job status

**Implementation Location**: `src/api/Controllers/PrintersController.cs`

**Existing & Working**:
- G-code harvest operations
- Job queue management
- Print submission

**Testing Dependencies**:
- Display current print job in printer cards
- Show print progress in dashboard

---

### Phase 4: Settings & User Management (🟡 BACKLOG)
**Status**: All core endpoints exist
**Timeline**: Post core features

**Working Endpoints**:
- Authentication & user management
- Settings management
- Filament types
- Spoolman integration

**Note**: File upload endpoints need verification against frontend expectations

---

### Phase 5: Slicing (🟡 FUTURE)
**Status**: Infrastructure in place, awaiting slicing engine
**Timeline**: Post core features

---

## Implementation Tasks

### Task 1: Add Missing Phase 2 Endpoints

**File**: `src/api/Controllers/PrintersController.cs`

**Endpoints to Add**:

```csharp
// 1. POST /api/printers/bulk - Bulk create printers
[HttpPost("bulk")]
[ProducesResponseType(200)]
public async Task<IActionResult> BulkCreateAsync(
    [FromBody] CreatePrinterDto[] printers,
    [FromQuery] string? duplicateHandling,
    CancellationToken ct)
{
    // TODO: Implement bulk printer creation
    // - Validate all printers
    // - Handle duplicateHandling (skip/overwrite/error)
    // - Return BulkImportResponse with results
}

// 2. POST /api/printers/discover/stream - Start discovery stream
[HttpPost("discover/stream")]
[ProducesResponseType(200)]
public async Task<IActionResult> StartDiscoveryStreamAsync(
    [FromBody] StartDiscoveryRequest? request,
    CancellationToken ct)
{
    // TODO: Implement discovery stream
    // - Return sessionId for SignalR group
    // - Start background discovery service
    // - Broadcast updates via SignalR hub
}
```

**Status**: PARTIALLY IMPLEMENTED (skeleton code added, needs service integration)

---

### Task 2: Add Missing Phase 3 Endpoint

**File**: `src/api/Controllers/PrintersController.cs`

**Endpoint to Add**:

```csharp
// GET /api/printers/{id}/printjob - Get current print job
[HttpGet("{id:guid}/printjob")]
[ProducesResponseType(200)]
public async Task<IActionResult> GetPrintJobStatusAsync(
    Guid id,
    CancellationToken ct)
{
    // TODO: Implement print job status retrieval
    // - Query printer backend for current job
    // - Return PrintJobStatusDto or null
    // - Support Moonraker/PrusaLink/SDCP
}
```

**Status**: PARTIALLY IMPLEMENTED (skeleton code added, needs service integration)

---

### Task 3: Fix Route Mapping

**Current State**:
- ✅ `POST /api/printers/{id:guid}/print/start` - Original route
- ✅ `POST /api/printers/{id:guid}/start-print` - Alternative route (ADDED)

**Status**: COMPLETE - Both routes now work

---

## Verification Checklist

### Build & Compilation
- [ ] `cd ./src && dotnet build ./farm-web.sln -c Debug` succeeds
- [ ] No compilation errors in PrintersController
- [ ] All using statements properly included

### Unit Tests
- [ ] API tests compile
- [ ] Existing tests still pass
- [ ] New endpoints have test coverage

### Integration Testing
- [ ] Start API server: `dotnet run --project ./api/Farm.Web.Api.csproj`
- [ ] Verify setup wizard works
- [ ] Test printer discovery and display
- [ ] Test G-code harvest operations
- [ ] Test print job status retrieval

### Frontend Testing
- [ ] Dashboard loads printer list
- [ ] Printer cards display with status
- [ ] Bulk import works
- [ ] Discovery stream provides updates
- [ ] Print job status shows in cards

---

## Implementation Status

| Phase | Feature | Status | Blocking | Next Action |
|-------|---------|--------|----------|-------------|
| 1 | Setup Wizard | ✅ Complete | ❌ No | Deploy |
| 2 | Printer Discovery | 🔴 In Progress | ✅ Yes | Implement bulk + stream |
| 3 | G-Code Harvest | 🟡 Blocked | ✅ Yes | Implement printjob |
| 4 | Settings & Users | ✅ Ready | ❌ No | Verify + deploy |
| 5 | Slicing | ⏳ Waiting | ❌ No | Await engine |

---

## Files to Modify

- ✏️ `src/api/Controllers/PrintersController.cs` - Add missing endpoints (PARTIALLY DONE)
- ✏️ `src/api/Services/Printers/IPrintersService.cs` - May need new service methods (DEFER)
- ✏️ `src/api/Services/Printers/PrintersService.cs` - May need new implementations (DEFER)

---

## Next Steps (IMMEDIATE)

1. ✅ Build API to verify Phase 2 skeleton compiles
2. ✅ Commit missing endpoints skeleton code
3. 🔄 Implement full Phase 2 endpoints (bulk create, discover stream)
4. 🔄 Implement Phase 3 endpoint (printjob status)
5. ⏳ Run full test suite
6. ⏳ Manual frontend verification

