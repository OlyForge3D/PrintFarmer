# API Reference

Complete REST API and SignalR WebSocket documentation for PrintFarmer.

## Base URL

```
http://localhost:5245/api
```

## Authentication

All API requests require a Bearer token in the Authorization header:

```
Authorization: Bearer <token>
```

Tokens are obtained via login and typically stored in secure HttpOnly cookies.

## Response Format

All API responses use camelCase JSON (for TypeScript compatibility):

```json
{
  "id": "uuid",
  "name": "Printer Name",
  "isOnline": true,
  "createdAt": "2025-12-19T10:30:00Z"
}
```

Connection credentials and private service addresses are write-only configuration.
Printer, slicer, worker, queue, artifact, capability, and real-time response
contracts never return API keys, usernames, passwords, worker keys, internal
paths, backend endpoints, or private service URLs.

## API Contract and Calibration Capabilities

### Contract negotiation

The minimum supported API contract is `1.0`. Capability requests can negotiate
the contract with either:

```http
X-PrintFarmer-Api-Contract-Version: 1.0
```

or:

```http
GET /api/system/capabilities?apiContractVersion=1.0
```

Clients that omit negotiation retain backward-compatible behavior. Capability
responses include:

```http
X-PrintFarmer-Api-Contract-Version: 1.0
X-PrintFarmer-Minimum-Supported-Api-Contract-Version: 1.0
```

`X-PrintFarmer-Minimum-Api-Contract-Version` is retained as a compatibility
alias. An invalid or older requested version returns `426` with the stable
problem code `client_upgrade_required`. `/api/version` remains the
OctoPrint-compatibility contract and is not used for PrintFarmer negotiation.

### Public platform capabilities

`GET /api/system/capabilities` is anonymous and publicly cacheable for 30
seconds. It reports deployment-level configuration and health without
caller-specific permissions:

```json
{
  "serverVersion": "1.0.0",
  "apiContractVersion": "1.0",
  "minimumSupportedApiContractVersion": "1.0",
  "calibrationApiVersion": "1.0",
  "calibrationSchemaVersion": "1.0",
  "slicingConfigured": true,
  "slicingOperational": false,
  "calibrationContextEnabled": true,
  "calibrationPersistenceEnabled": true,
  "calibrationSyncEnabled": true,
  "calibrationPhotosEnabled": true,
  "calibrationProfileHistoryEnabled": true,
  "calibrationGenerationEnabled": false,
  "calibrationSlicingEnabled": false,
  "calibrationArtifactPromotionEnabled": false,
  "calibrationQueueEnabled": false,
  "calibrationJobBoundBedClearEnabled": false,
  "calibrationEventsEnabled": false,
  "supportedFirmwareFamilies": ["Klipper"],
  "supportedGcodeDialects": ["Klipper"],
  "supportedSlicerEngines": [
    {
      "type": "OrcaSlicer",
      "distribution": "upstream",
      "version": "2.3.1",
      "supported": true
    }
  ],
  "calibration": {
    "contextImplemented": true,
    "operational": true
  },
  "routes": {
    "systemCapabilities": "/api/system/capabilities",
    "calibrationCapabilities": "/api/calibration/capabilities",
    "printers": "/api/printers",
    "calibrationCandidates": "/api/printers/calibration-candidates",
    "calibrationContext": "/api/printers/{id}/calibration-context?slicerType=OrcaSlicer",
    "calibrationProjects": "/api/calibration-projects",
    "calibrationSync": "/api/calibration-sync/changes",
    "calibrationImports": "/api/calibration-imports/legacy-v4",
    "sliceJobs": "/api/slice-jobs",
    "sliceJob": "/api/slice-jobs/{id}",
    "jobArtifact": "/api/artifacts/job/{jobId}",
    "printerHub": "/hubs/printers",
    "slicerRegistryHub": "/hubs/slicer-registry",
    "slicerProgressHub": "/hubs/slicers"
  },
  "healthyCompatibleWorker": {
    "available": false,
    "healthyCount": 0,
    "availableSlots": 0,
    "engine": "OrcaSlicer",
    "requiredVersion": "2.3.1"
  },
  "unavailableReasons": [
    {
      "feature": "slicing",
      "code": "compatible_worker_unavailable",
      "message": "No healthy upstream OrcaSlicer 2.3.1 worker is available."
    }
  ]
}
```

`slicingConfigured` means slicing is enabled in configuration.
`slicingOperational` additionally requires reachable slicer persistence,
artifact storage, and a fresh enabled upstream OrcaSlicer `2.3.1` worker with
available capacity. Calibration context is operational when the caller can
reach the local upstream OrcaSlicer profile resolver. The monolith advertises
and serves that context; a split API without a caller-reachable resolver
advertises it as non-operational and returns
`503 profile_service_unavailable`. Persistence, synchronization, private
photos, and immutable generated-profile history are implemented independently.
Generation, slicing, promotion, queue, and event-streaming feature flags remain
false. Routes are canonical same-origin paths and never disclose internal
service addresses.

### Effective calibration capabilities

`GET /api/calibration/capabilities` requires a PrintFarmer JWT. Unscoped
OctoPrint-compatible API keys cannot authenticate this route. The response has
the platform capability shape plus:

- `effectivePermissions`: only the caller's effective `resource:action`
  permissions;
- `effectiveCapabilities`: permission- and dependency-gated operations;
- model and photo limits, accepted MIME types, and export formats;
- non-secret compatible-worker counts and structured unavailable reasons.

The response uses `Cache-Control: private, max-age=15` and
`Vary: Authorization`. Current foundation versions are API `1.0`, calibration
API `1.0`, and calibration schema `1.0`.

### Calibration foundation permissions

| Resource | Actions |
|---|---|
| `calibration` | `create`, `read`, `update`, `delete`, `generate`, `publish` |
| `queue` | `read`, `write`, `start`, `cancel`, `acknowledge-bed-clear`, `reconcile` |
| `slicing` | `submit`, `read-artifact`, `promote` |
| `dispatch-settings` | `manage` |

Protected routes return `401 authentication_required` without authentication
and `403 permission_denied` when the action is missing. Ownership and farm
scope are checked after permission checks; identifier-based direct and binary
reads cannot bypass them. The `farm_admin` bypass is explicit and audited.
Ordinary users receive no calibration-foundation permissions implicitly.

## Printers API

### List Printer Calibration Candidates

```http
GET /api/printers/calibration-candidates
```

Requires `calibration:read`. The response includes every enabled printer with
its configuration revision, observed status freshness, explicit firmware and
slicer identities, geometry, physical toolheads, declared adapter operations,
and typed eligibility results.

Printer Calibration eligibility is strictly conjunctive:

- firmware family is explicitly `Klipper`;
- G-code dialect is explicitly `Klipper`;
- slicer engine is `OrcaSlicer`, distribution is `upstream`, and the pinned
  version and profile format are compatible;
- firmware detection source, detector version, confidence, verification, and
  observation are present and fresh;
- bed geometry, physical toolhead/nozzle limits, material, motion, thermal,
  enclosure, and required operational metadata are complete and fresh;
- live status is authoritative, fresh, and online;
- the adapter explicitly supports status, file upload, and print start, or a
  combined upload-and-print operation;
- selected machine, process, and filament profiles are compatible, safe, and
  visible to the caller, with explicit upstream distribution, compatible
  version, and `orca-json` format identity.

Eligibility never derives from manufacturer, model, aliases, transport
backend, Moonraker, OctoPrint, or a static printer catalog. Missing, stale,
unverified, inaccessible, incompatible, or operationally incomplete data
keeps the printer in the response with `eligible: false`, `missingInputs`, and
stable `{ code, field, message }` rejection reasons.

```json
[
  {
    "id": "0d7d648e-b7c2-4499-b7a8-612a2627e651",
    "name": "Voron 2.4",
    "configurationRevision": 7,
    "reachability": "online",
    "observedAtUtc": "2026-07-25T12:00:00Z",
    "isStale": false,
    "firmware": {
      "family": "Klipper",
      "gcodeDialect": "Klipper",
      "detectionSource": "printer",
      "version": "v0.12.0",
      "detectionVersion": "printer-info-v1",
      "detectionConfidence": 1.0,
      "verified": true
    },
    "slicer": {
      "engine": "OrcaSlicer",
      "distribution": "upstream",
      "version": "2.3.1",
      "profileFormat": "orca-json"
    },
    "eligible": true,
    "missingInputs": [],
    "rejectionReasons": []
  }
]
```

The contract never returns printer credentials, connection URLs, ports,
cameras, worker credentials, internal paths, or private service addresses.

### Get Printer Calibration Context

```http
GET /api/printers/{printerId}/calibration-context?slicerType=OrcaSlicer
```

Requires `calibration:read`. An optional `configurationRevision` query value
provides optimistic concurrency. If the printer changed, the API returns
`409 printer_configuration_changed` and the current revision.

The `200` response repeats the candidate state and adds an immutable
credential-free snapshot:

- canonical `snapshotSha256`, schema version, capture time, and authenticated
  subject;
- exact bed geometry, exclusions, motion and acceleration limits;
- physical toolhead offsets, nozzle/hotend limits, drive details, and material
  compatibility;
- verified firmware, backend API version, and upstream OrcaSlicer identity;
- caller-visible exact machine, process, and filament profile JSON with raw
  SHA-256 values, explicit distribution and format identity, and canonical
  effective settings;
- compatible filament product choices and physical spool choices;
- supported Printer Calibration methods and generator compatibility.

Exact profile JSON is omitted and a typed rejection reason is returned if a
profile contains credentials, authorization headers, private service URLs,
filesystem paths, file URIs, or unsafe commands. Private profiles are visible
only to their owner; the `farm_admin` bypass remains explicit and audited.
Transient printer status and capture metadata do not change the canonical
snapshot hash.

An incomplete context still returns `200` with `eligible: false`, typed
rejection reasons, and no synthesized defaults. Consumers must not generate or
dispatch calibration work from an ineligible context.

| Status | Stable code | Meaning |
|---|---|---|
| `400` | `unsupported_slicer_type` | `slicerType` is not exactly `OrcaSlicer`. |
| `401` | `authentication_required` | No authenticated PrintFarmer identity is present. |
| `403` | `permission_denied` | The caller lacks `calibration:read`. |
| `404` | `printer_not_found` | The printer is missing or disabled. |
| `409` | `printer_configuration_changed` | The requested revision is no longer current. |
| `503` | `profile_service_unavailable` | The profile resolver is not caller-reachable. |

This context supports the public **Printer Calibration** workflow for Klipper
printers based on upstream OrcaSlicer and its
[official calibration wiki](https://github.com/SoftFever/OrcaSlicer/wiki/Calibration).

### Calibration persistence and synchronization

All calibration persistence routes require a PrintFarmer JWT, the corresponding
`calibration:*` permission, and owner or farm-resource authorization. The
authoritative resources are available through these route groups:

- projects and drafts: `/api/calibration-projects`;
- attempts, append-only events, observations, and private photos:
  `/api/calibration-attempts` and `/api/calibration-photos`;
- immutable upstream OrcaSlicer profile history:
  `/api/calibration-generated-profiles`;
- cursor pull and ordered mutation apply: `/api/calibration-sync`;
- legacy v4 preview and commit: `/api/calibration-imports/legacy-v4`.

Editable projects, drafts, and photo metadata return a strong `ETag` and body
`revision`. Updates and deletes require both `If-Match` and `baseRevision`.
Missing preconditions return `428 precondition_required`; stale revisions return
`412 revision_conflict` with the current safe representation. Reusing an
operation ID with a different canonical request returns
`409 idempotency_payload_mismatch`. The service never applies a client-wins or
last-write-wins fallback.

The change feed uses an opaque owner-scoped cursor and includes soft-delete
tombstones. Attempt plans, lifecycle events, observations, printer snapshots,
and generated profile revisions are immutable. Profile create/export/publish
routes only persist externally generated exact JSON and operation history; they
do not generate G-code, submit slicing, promote artifacts, or dispatch jobs.

Photo bytes are private and authenticated. Responses never expose storage keys
or paths. Uploads are size-limited, decoded and checked against JPEG, PNG, or
WebP magic, re-encoded without EXIF/GPS metadata, and deleted through a durable
two-phase reconciliation process.

### List All Printers

```http
GET /api/printers
```

**Response:**
```json
[
  {
    "id": "uuid",
    "name": "Printer 1",
    "backendType": "Moonraker",
    "isOnline": true,
    "state": "Idle",
    "locationId": "location-uuid",
    "location": {
      "id": "location-uuid",
      "name": "Workshop"
    },
    "createdAt": "2025-12-19T10:00:00Z"
  }
]
```

### Get Printer Details

```http
GET /api/printers/{printerId}
```

**Parameters:**
- `printerId` (path, required) - UUID of printer

**Response:**
```json
{
  "id": "uuid",
  "name": "Printer 1",
  "backendType": "Moonraker",
  "isOnline": true,
  "state": "Idle",
  "hotendTemp": 25.5,
  "hotendTarget": 0,
  "bedTemp": 25.0,
  "bedTarget": 0,
  "progress": 0,
  "locationId": "location-uuid",
  "createdAt": "2025-12-19T10:00:00Z",
  "modifiedAt": "2025-12-19T11:00:00Z"
}
```

### Create Printer

```http
POST /api/printers
Content-Type: application/json

{
  "name": "Printer 2",
  "url": "http://192.168.1.101:7125",
  "backendType": "Moonraker",
  "apiKey": "api_key_here",
  "locationId": "location-uuid"
}
```

**Response:** 201 Created
```json
{
  "id": "new-printer-uuid",
  "name": "Printer 2",
  "backendType": "Moonraker",
  "isOnline": false,
  "locationId": "location-uuid",
  "createdAt": "2025-12-19T10:30:00Z"
}
```

### Update Printer

```http
PUT /api/printers/{printerId}
Content-Type: application/json

{
  "name": "Printer 1 Updated",
  "url": "http://192.168.1.100:7125",
  "locationId": "new-location-uuid"
}
```

**Response:** 200 OK

### Delete Printer

```http
DELETE /api/printers/{printerId}
```

**Response:** 204 No Content

### Assign Printer to Location

```http
POST /api/printers/{printerId}/location
Content-Type: application/json

{
  "locationId": "location-uuid"
}
```

**Response:** 200 OK

### Remove Printer from Location

```http
DELETE /api/printers/{printerId}/location
```

**Response:** 204 No Content

## Locations API

### List All Locations (Flat)

```http
GET /api/locations
```

**Authentication:** Not required  
**Response:**
```json
[
  {
    "id": "location-uuid",
    "name": "Workshop",
    "parentId": null,
    "depth": 0,
    "path": "/Workshop",
    "sortOrder": 1,
    "printerCount": 3,
    "totalPrinterCount": 3,
    "locationTypeId": "type-uuid",
    "createdAt": "2025-12-19T10:00:00Z"
  }
]
```

### Get Location Tree (Hierarchical)

```http
GET /api/locations/tree?rootId=optional-uuid
```

**Authentication:** Not required  
**Query Parameters:**
- `rootId` (optional, UUID) - Get subtree from specified node. Omit for full tree.

**Response:** 200 OK
```json
[
  {
    "id": "warehouse-uuid",
    "name": "Warehouse 1",
    "parentId": null,
    "depth": 0,
    "path": "/Warehouse 1",
    "sortOrder": 1,
    "printerCount": 0,
    "totalPrinterCount": 5,
    "locationTypeId": "type-uuid",
    "children": [
      {
        "id": "room-uuid",
        "name": "Room A",
        "parentId": "warehouse-uuid",
        "depth": 1,
        "path": "/Warehouse 1/Room A",
        "sortOrder": 1,
        "printerCount": 2,
        "totalPrinterCount": 2,
        "locationTypeId": "type-uuid",
        "children": []
      }
    ]
  }
]
```

**Status Codes:**
- `200` - Success
- `503` - System initializing

### Get Location Details

```http
GET /api/locations/{locationId}
```

**Authentication:** Not required  
**Path Parameters:**
- `locationId` (required, UUID) - Location to retrieve

**Response:** 200 OK
```json
{
  "id": "location-uuid",
  "name": "Workshop",
  "parentId": null,
  "depth": 0,
  "path": "/Workshop",
  "sortOrder": 1,
  "printerCount": 3,
  "totalPrinterCount": 3,
  "locationTypeId": "type-uuid",
  "createdAt": "2025-12-19T10:00:00Z",
  "modifiedAt": "2025-12-19T11:00:00Z"
}
```

**Status Codes:**
- `200` - Success
- `404` - Location not found
- `503` - System initializing

### Get Location Ancestors (Breadcrumbs)

```http
GET /api/locations/{locationId}/ancestors
```

**Authentication:** Not required  
**Path Parameters:**
- `locationId` (required, UUID) - Starting location

**Response:** 200 OK — Returns path from root to specified location (useful for breadcrumbs)
```json
[
  {
    "id": "warehouse-uuid",
    "name": "Warehouse 1",
    "depth": 0,
    "sortOrder": 1
  },
  {
    "id": "room-uuid",
    "name": "Room A",
    "depth": 1,
    "sortOrder": 1
  },
  {
    "id": "location-uuid",
    "name": "Rack 3",
    "depth": 2,
    "sortOrder": 1
  }
]
```

**Status Codes:**
- `200` - Success
- `503` - System initializing

### Get Location Descendants (Subtree)

```http
GET /api/locations/{locationId}/descendants
```

**Authentication:** Not required  
**Path Parameters:**
- `locationId` (required, UUID) - Root of subtree to retrieve

**Response:** 200 OK — Flat list of all descendants (does not include the root location itself)
```json
[
  {
    "id": "child1-uuid",
    "name": "Child 1",
    "parentId": "location-uuid",
    "depth": 1,
    "path": "/Parent/Child 1",
    "printerCount": 2,
    "totalPrinterCount": 2
  },
  {
    "id": "child2-uuid",
    "name": "Child 2",
    "parentId": "location-uuid",
    "depth": 1,
    "path": "/Parent/Child 2",
    "printerCount": 1,
    "totalPrinterCount": 1
  }
]
```

**Status Codes:**
- `200` - Success
- `503` - System initializing

### Create Location

```http
POST /api/locations
Content-Type: application/json
Authorization: Bearer <token>

{
  "name": "Room A",
  "parentId": "warehouse-uuid",
  "locationTypeId": "type-uuid",
  "sortOrder": 1
}
```

**Authentication:** Required (role: `farm_admin`)  
**Request Body:**
- `name` (string, required) - Location name
- `parentId` (UUID, optional) - Parent location ID (null = root level)
- `locationTypeId` (UUID, optional) - Location type (building, room, rack, etc.)
- `sortOrder` (integer, optional) - Display order (default: 999)

**Response:** 201 Created
```json
{
  "id": "new-location-uuid",
  "name": "Room A",
  "parentId": "warehouse-uuid",
  "depth": 1,
  "path": "/Warehouse 1/Room A",
  "sortOrder": 1,
  "printerCount": 0,
  "totalPrinterCount": 0,
  "locationTypeId": "type-uuid",
  "createdAt": "2025-12-19T10:30:00Z"
}
```

**Status Codes:**
- `201` - Created
- `400` - Invalid request (duplicate name at level, max depth exceeded)
- `503` - System initializing

### Update Location

```http
PUT /api/locations/{locationId}
Content-Type: application/json
Authorization: Bearer <token>

{
  "name": "Room A Updated",
  "sortOrder": 2
}
```

**Authentication:** Required (role: `farm_admin`)  
**Path Parameters:**
- `locationId` (required, UUID) - Location to update

**Request Body:**
- `name` (string, optional) - New location name
- `sortOrder` (integer, optional) - Display order

**Response:** 200 OK
```json
{
  "id": "location-uuid",
  "name": "Room A Updated",
  "parentId": "warehouse-uuid",
  "depth": 1,
  "path": "/Warehouse 1/Room A Updated",
  "sortOrder": 2,
  "printerCount": 0,
  "totalPrinterCount": 0,
  "locationTypeId": "type-uuid",
  "createdAt": "2025-12-19T10:00:00Z",
  "modifiedAt": "2025-12-19T11:00:00Z"
}
```

**Status Codes:**
- `200` - Success
- `400` - Invalid request (duplicate name)
- `404` - Location not found
- `503` - System initializing

### Move Location (Reparent)

```http
POST /api/locations/{locationId}/move
Content-Type: application/json
Authorization: Bearer <token>

{
  "newParentId": "new-parent-uuid"
}
```

**Authentication:** Required (role: `farm_admin`)  
**Path Parameters:**
- `locationId` (required, UUID) - Location to move

**Request Body:**
- `newParentId` (UUID, optional) - New parent location ID (null = move to root)

**Response:** 200 OK — Returns updated location with new path
```json
{
  "id": "location-uuid",
  "name": "Room A",
  "parentId": "new-parent-uuid",
  "depth": 2,
  "path": "/Building 2/Room A",
  "sortOrder": 1,
  "printerCount": 1,
  "totalPrinterCount": 1,
  "locationTypeId": "type-uuid",
  "createdAt": "2025-12-19T10:00:00Z",
  "modifiedAt": "2025-12-19T11:30:00Z"
}
```

**Status Codes:**
- `200` - Success
- `400` - Invalid move (circular reference, max depth exceeded, duplicate name at destination)
- `404` - Location or parent not found
- `503` - System initializing

### Delete Location

```http
DELETE /api/locations/{locationId}
Authorization: Bearer <token>
```

**Authentication:** Required (role: `farm_admin`)  
**Path Parameters:**
- `locationId` (required, UUID) - Location to delete

**Response:** 204 No Content — Location soft-deleted (marked as deleted)

**Status Codes:**
- `204` - Success (no content returned)
- `400` - Invalid delete (has child locations, cannot delete non-empty parent)
- `404` - Location not found
- `503` - System initializing

## Auto-Dispatch API

The auto-dispatch system scores all available printers against job requirements using a 9-factor algorithm (4 hard filters + 5 soft scoring factors). This enables intelligent printer selection and automated job assignment.

### Get Dispatch Candidates

```http
GET /api/job-queue/{jobId}/candidates
Authorization: Bearer <token>
```

**Authentication:** Required  
**Path Parameters:**
- `jobId` (required, UUID) - Print job to find candidates for

**Response:** 200 OK — Returns all printers ranked by compatibility score
```json
[
  {
    "printerId": "printer-1-uuid",
    "printerName": "Prusa MK4 #1",
    "serverUrl": "http://192.168.1.100:7125",
    "backendType": "Moonraker",
    "isOnline": true,
    "state": "Idle",
    "score": 95.5,
    "scoreFactors": {
      "materialMatch": { "score": 100, "reason": "PLA matches job requirement" },
      "nozzleDiameter": { "score": 100, "reason": "0.4mm matches exactly" },
      "buildVolume": { "score": 90, "reason": "Adequate for part dimensions" },
      "enclosure": { "score": 80, "reason": "Enclosure not required but available" },
      "nozzleHardness": { "score": 85, "reason": "Standard nozzle suitable" },
      "modelMatch": { "score": 70, "reason": "Compatible model variant" },
      "queueDepth": { "score": 50, "reason": "3 jobs already queued" },
      "preferred": { "score": 40, "reason": "Marked as preferred for material" },
      "availability": { "eliminated": false, "reason": "Printer is online and idle" }
    },
    "eliminationReason": null
  },
  {
    "printerId": "printer-2-uuid",
    "printerName": "Prusa MK3S+ #2",
    "serverUrl": "http://192.168.1.101:7125",
    "backendType": "Moonraker",
    "isOnline": true,
    "state": "Idle",
    "score": 0.0,
    "scoreFactors": { ... },
    "eliminationReason": "Material PCTG not available on this printer"
  }
]
```

**Status Codes:**
- `200` - Success
- `404` - Job not found
- `500` - Scoring error

**Score Factors Explained:**

| # | Factor | Weight | Type | Description |
|---|--------|--------|------|-------------|
| 1 | Material Match | 100 | HARD | Job material must be available on printer |
| 2 | Nozzle Diameter | 100 | HARD | Must match ±0.01mm tolerance |
| 3 | Build Volume | 50 | SOFT | Printer must fit part dimensions |
| 4 | Enclosure | 80 | COND. | Bonus if available; required for some materials |
| 5 | Nozzle Hardness | 80 | COND. | Bonus for hardened nozzles with abrasive materials |
| 6 | Model Match | 60 | SOFT | Bonus for printer model matching job expectation |
| 7 | Queue Depth | 30 | SOFT | Penalty for printers with many queued jobs |
| 8 | Preferred | 40 | COND. | Bonus if user marked printer as preferred for material |
| 9 | Availability | 0 | HARD | Pre-filter: printer must be online and idle |

Eliminated printers appear at end of list with `eliminationReason` explaining why they cannot accept the job.

### Dispatch Job to Printer

```http
POST /api/job-queue/{jobId}/dispatch-to
Content-Type: application/json
Authorization: Bearer <token>

{
  "printerId": "printer-uuid"
}
```

**Authentication:** Required  
**Path Parameters:**
- `jobId` (required, UUID) - Print job to dispatch

**Request Body:**
- `printerId` (required, UUID) - Target printer (must be from candidates list)

**Response:** 200 OK — Job assigned and print started
```json
{
  "id": "job-uuid",
  "printerId": "printer-uuid",
  "printerName": "Prusa MK4 #1",
  "gcodeFileId": "file-uuid",
  "filename": "calibration.gcode",
  "status": "Printing",
  "progress": 0.0,
  "timeElapsed": 0,
  "estimatedTimeRemaining": 1800,
  "dispatchScore": 95.5,
  "dispatchedBy": "user@example.com",
  "dispatchedAt": "2025-12-19T10:30:00Z",
  "createdAt": "2025-12-19T10:00:00Z"
}
```

**Status Codes:**
- `200` - Successfully dispatched
- `400` - Invalid printer or job not in queue
- `404` - Job not found
- `500` - Dispatch error

---

## Dispatch Settings API

Manage system-wide auto-dispatch configuration (singleton entity).

### Get Dispatch Settings

```http
GET /api/dispatch-settings
Authorization: Bearer <token>
```

**Authentication:** Required  

**Response:** 200 OK
```json
{
  "autoDispatchEnabled": true,
  "autoDispatchMode": "Suggest",
  "idleThresholdSeconds": 30,
  "minimumScoreThreshold": 70,
  "maxConcurrentDispatches": 3,
  "updatedAt": "2025-12-19T10:30:00Z"
}
```

**Fields:**
- `autoDispatchEnabled` (boolean) - Enable/disable auto-dispatch system
- `autoDispatchMode` (string: "Suggest" | "Auto") - Mode of operation:
  - **Suggest**: Notify operator of recommended printer, require manual confirmation
  - **Auto**: Automatically dispatch to highest-scoring printer
- `idleThresholdSeconds` (integer) - Seconds to wait after printer goes idle before triggering dispatch (prevents mid-print triggers)
- `minimumScoreThreshold` (integer, 0-100) - Only dispatch if top candidate scores >= this threshold
- `maxConcurrentDispatches` (integer) - Maximum simultaneous dispatch operations
- `updatedAt` (ISO 8601 timestamp) - Last update time

### Update Dispatch Settings

```http
PUT /api/dispatch-settings
Content-Type: application/json
Authorization: Bearer <token>

{
  "autoDispatchEnabled": true,
  "autoDispatchMode": "Auto",
  "idleThresholdSeconds": 45,
  "minimumScoreThreshold": 75,
  "maxConcurrentDispatches": 5
}
```

**Authentication:** Required  

**Request Body:** — All fields optional; omitted fields retain current values
- `autoDispatchEnabled` (boolean)
- `autoDispatchMode` (string: "Suggest" | "Auto")
- `idleThresholdSeconds` (integer, >= 0)
- `minimumScoreThreshold` (integer, 0-100)
- `maxConcurrentDispatches` (integer, >= 1)

**Response:** 200 OK — Returns updated settings
```json
{
  "autoDispatchEnabled": true,
  "autoDispatchMode": "Auto",
  "idleThresholdSeconds": 45,
  "minimumScoreThreshold": 75,
  "maxConcurrentDispatches": 5,
  "updatedAt": "2025-12-19T10:35:00Z"
}
```

**Status Codes:**
- `200` - Updated successfully
- `400` - Validation error (e.g., idleThresholdSeconds < 0, minimumScoreThreshold out of range)
- `500` - Server error

**Validation Rules:**
- `idleThresholdSeconds` ≥ 0
- `minimumScoreThreshold` 0–100
- `maxConcurrentDispatches` ≥ 1



## Discovery API

Discovery routes require a PrintFarmer JWT and the `farm_admin` role. Network
targets, camera URLs, credentials, and scanned ranges remain server-side.

### Start Discovery

```http
POST /api/printers/discover/stream
Authorization: Bearer <token>
Content-Type: application/json

{
  "backends": ["Moonraker", "PrusaLink"]
}
```

**Response:** `202 Accepted`

```json
{
  "sessionId": "discovery-session-id",
  "message": "Discovery started"
}
```

Call `JoinDiscoveryGroupAsync(sessionId)` on the authenticated `/hubs/printers`
connection. Only the session owner or an audited farm-administrator bypass may
join. The lowercase events are:

- `discoveryprogress`: counts, percentage, status, and a redacted message;
- `discoveryprinterfound`: safe metadata plus an opaque `discoveryId`;
- `discoverycompleted`: totals, duration, and cancellation state.

No discovery event includes an IP address, URL, network range, camera endpoint,
or credential.

### Register a Discovery Result

```http
POST /api/printers/discover/{sessionId}/register
Authorization: Bearer <token>
Content-Type: application/json

{
  "discoveryId": "opaque-discovery-id",
  "manufacturerId": "optional-manufacturer-id",
  "modelId": "optional-model-id"
}
```

The server resolves the owner-bound target, creates the printer, and consumes
the identifier after success. Unknown, expired, cross-session, or replayed
identifiers return `404 resource_not_found`.

### Cancel Discovery

```http
POST /api/printers/discover/{sessionId}/cancel
Authorization: Bearer <token>
```

## Catalog API

### List Manufacturers

```http
GET /api/catalog/manufacturers
```

**Response:**
```json
[
  {
    "id": "mfg-uuid",
    "name": "Prusa",
    "modelCount": 5
  }
]
```

### List Printer Models

```http
GET /api/catalog/models?manufacturerId=mfg-uuid
```

**Response:**
```json
[
  {
    "id": "model-uuid",
    "name": "Prusa i3 MK3S+",
    "manufacturerId": "mfg-uuid",
    "nozzleSize": 0.4,
    "buildArea": "250 x 210 x 210mm"
  }
]
```

## 3D Model Upload API

### Upload a Model

```http
POST /api/3d-models/upload
Content-Type: multipart/form-data
```

The `modelFile` form field is required. The optional `thumbnailFile` field accepts
a decoded PNG up to 10 MiB, 4096 pixels per dimension, and 16 million pixels.
The optional `clientUploadId` GUID makes retries idempotent within the
authenticated user account.

Successful responses include `thumbnailUrl`, `wasExisting`, `clientUploadId`,
and `etag`. The same ETag is returned in the `ETag` response header.

### Replace a Model Thumbnail

```http
PUT /api/3d-models/{id}/thumbnail
Content-Type: multipart/form-data
If-Match: "current-etag"
```

The required `thumbnailFile` form field uses the same PNG validation limits as
model upload. Only the model owner or a user with the `farm_admin` role can
replace the thumbnail. `If-Match` is optional; when supplied, a stale ETag
returns `412 Precondition Failed`. The previous thumbnail remains available if
validation, storage, cancellation, or database commit fails.

**Response:**

```json
{
  "id": "model-uuid",
  "thumbnailUrl": "/api/3d-models/thumbnail/model-uuid",
  "etag": "\"updated-etag\""
}
```

The updated ETag is also returned in the `ETag` response header.

## Settings API

The settings API is served by `UnifiedSettingsController` under `/api/settings`. It
backs the tabbed Settings Shell in the React frontend (`/settings`, `/admin/settings`,
`/admin/manage`). Each backend `[AppSetting]` class is one section, keyed by its
`SectionName`. See [SETTINGS_ARCHITECTURE.md](./SETTINGS_ARCHITECTURE.md) for the
end-to-end model.

Two sections (`HomeAssistant`, `Telegram`) manage encrypted tokens through dedicated
admin controllers and are blocklisted from the generic endpoints below. They return
`404 Not Found` if you address them via `/api/settings/{keyName}`.

### List All Sections With Current Values

```http
GET /api/settings
```

Anonymous. Returns a dictionary keyed by `SectionName`, where each value is the current
settings object for that section (with camelCase properties matching the class's
`[JsonPropertyName]` values).

**Response:**

```json
{
  "SystemLog": {
    "enabled": true,
    "minimumLevel": "Warning",
    "retentionDays": 30,
    "enableExport": true
  },
  "NetworkDiscovery": {
    "enableDiscovery": true,
    "discoverySubnets": ["192.168.1.0/24"],
    "backgroundScanEnabled": false
  }
}
```

### Get Section Values

```http
GET /api/settings/{keyName}
```

Anonymous. Returns the current values for one section. `keyName` is the `SectionName`
constant on the settings class (e.g. `"SystemLog"`, `"NetworkDiscovery"`).

**Status codes:**

- `200 OK` — section object.
- `404 Not Found` — unknown or blocklisted `keyName`.

### Get Metadata For The UI

```http
GET /api/settings/metadata
```

Requires authentication. Returns display metadata for every non-blocklisted section:
class name, section key, group, display name/description/icon, and per-property display
metadata (input type, allowed values, min/max, order, etc.). The frontend uses this to
render the dynamic settings pages without any hand-written form code.

### Get Group Metadata

```http
GET /api/settings/groups
```

Requires authentication. Returns the ordered list of group metadata (declared via
`[SettingGroup]` on settings classes). Used to build sidebar entries within a settings
sub-page.

### Save A Single Section

```http
POST /api/settings/{keyName}
Content-Type: application/json

{
  "enabled": true,
  "retentionDays": 30,
  "minimumLevel": "Warning"
}
```

Requires authentication. This is the canonical save endpoint — the settings UI fires one
call per group when the user presses Save. There is no "Save All" button; each group has
its own save button.

**Status codes:**

- `200 OK` — section saved and re-validated.
- `400 Bad Request` — validation failed. Body shape:
  `{ "message": "Validation failed for class '<keyName>'", "errors": { "<property>": "..." } }`.
- `404 Not Found` — unknown or blocklisted `keyName`.

### Save All Sections (Legacy, Do Not Use For UI)

```http
POST /api/settings
```

Kept for test scaffolding and seed scripts. **Not** invoked by the settings UI in
production, and the settings-page tests explicitly assert that `saveAllSettings` is
never called on save. Prefer `POST /api/settings/{keyName}` for anything user-driven.

### Discovery Heartbeat

```http
POST /api/settings/{keyName}/heartbeat
```

Anonymous. Only meaningful for `NetworkDiscovery`. Bumps the `LastHeartbeat` timestamp
so the health check can distinguish a running discovery worker from a stalled one.
Returns `204 No Content` on success.

## Admin Control Center

### Overview Snapshot

```http
GET /api/admin/overview
```

Requires the `farm_admin` role. Returns a single snapshot the `/admin` hub renders in
one call: subsystem health tiles plus a ranked list of items needing operator attention.

Implementation notes that shape client expectations:

- **Aggregates existing health checks.** The endpoint calls
  `HealthCheckService.CheckHealthAsync()` and splits the results into per-subsystem
  tiles. It does not run new probes and does not touch the database directly.
- **8-second timeout.** The health-check aggregation is guarded by an 8s
  `CancellationTokenSource`. If it does not complete in time, non-API subsystems are
  marked `Unknown` and an `Error`-severity attention item is added.
- **Never 500s.** Any unexpected exception is caught and reported the same way as a
  timeout. The endpoint always returns 200 with a valid `AdminOverviewDto`.
- **String enums.** `SubsystemStatus` and `AttentionSeverity` are serialized as strings
  via `JsonStringEnumConverter`. Clients receive `"Healthy" | "Degraded" | "Unhealthy" |
  "Unknown"` and `"Info" | "Warning" | "Error"`, not integers.

**Status codes:**

- `200 OK` — snapshot returned.
- `401 Unauthorized` — no session.
- `403 Forbidden` — authenticated but missing the `farm_admin` role.

**Response:**

```json
{
  "checkedAt": "2025-12-19T10:30:00Z",
  "subsystems": [
    {
      "key": "api",
      "name": "API",
      "status": "Healthy",
      "detail": "Responding"
    },
    {
      "key": "database",
      "name": "Database",
      "status": "Healthy",
      "detail": "PostgreSQL · 4 ms"
    },
    {
      "key": "signalr",
      "name": "SignalR Hub",
      "status": "Healthy",
      "detail": null
    },
    {
      "key": "backends",
      "name": "Printer Backends",
      "status": "Degraded",
      "detail": "1 of 8 unreachable"
    }
  ],
  "attention": [
    {
      "key": "backend-printer-42-offline",
      "severity": "Warning",
      "title": "Printer offline",
      "detail": "\"Voron 2.4\" has not responded to Moonraker probes for 3 minutes.",
      "actionLabel": "Open printer",
      "actionRoute": "/printers/42"
    }
  ]
}
```

Fields:

- `checkedAt` — UTC timestamp when the snapshot was generated.
- `subsystems[]` — ordered subsystem tiles. Always includes `api`, `database`, `signalr`,
  `backends`. Optional subsystems (e.g. `spoolman`) appear only when configured.
- `subsystems[].key` — stable machine key. Do not localize.
- `subsystems[].detail` — optional one-line status detail, may be `null`.
- `attention[]` — sorted `Error` first, then `Warning`, then `Info`. Empty when
  everything is healthy.
- `attention[].actionLabel` / `actionRoute` — optional call-to-action pair. `actionRoute`
  is always a client-side router path, never a raw URL.

To add a subsystem tile, add a `BuildTileFromEntry` or `BuildTileFromSubcheck` call in
`AdminOverviewService.BuildSubsystems`. To add an attention item, append to
`AppendAttentionForEntry` or `AppendExternalServicesAttention` in the same service. Do
not add probes to the overview endpoint directly — register them under the existing
`comprehensive` health check so the aggregation stays in one place.

## System Source API

### Get License and Corresponding Source

```http
GET /api/system/source
```

This endpoint is intentionally unauthenticated so every network user can
identify the license and exact corresponding source for the running version.
Release builds return the full immutable revision, source tree, source archive,
license, notices, and SPDX SBOM links. Development builds without a full commit
return `sourceAvailable: false` and omit versioned links.

See
[Licensing, Corresponding Source, and Provenance](LICENSING_AND_SOURCE.md) for
operator requirements.

## Health Check API

### System Health

```http
GET /api/health
```

**Response:**
```json
{
  "status": "Healthy",
  "timestamp": "2025-12-19T10:30:00Z",
  "services": {
    "database": "Healthy",
    "signalr": "Healthy",
    "discovery": "Running"
  }
}
```

### Quick Health Check

```http
GET /api/healthz
```

**Response:**
```json
{
  "status": "ok"
}
```

## SignalR Real-time Hub

### Connection

Protected hub routes:

- `/hubs/printers`: authenticated farm and per-user printer events;
- `/hubs/slicers`: authenticated owner-scoped slice-job progress;
- `/hubs/slicer-registry`: farm-administrator-only registry events.

SignalR clients must authenticate with the same PrintFarmer JWT used for REST:

### Listening for Events

```javascript
const connection = new HubConnectionBuilder()
  .withUrl('http://localhost:5245/hubs/printers', {
    accessTokenFactory: () => printFarmerJwt
  })
  .withAutomaticReconnect()
  .build();

// Listen for printer updates
connection.on('printerupdated', (printer) => {
  console.log('Printer updated:', printer);
  // printer: { id, name, isOnline, state, hotendTemp, bedTemp, progress, ... }
});

// Start connection
connection.start().catch(err => console.error(err));
```

The server adds authenticated connections only to authorized farm, user,
printer, slice-job, administrator, and future project/calibration/queue
resource groups. Protected publishers do not broadcast through `Clients.All`.
Job subscriptions on `/hubs/slicers` verify ownership or the audited
farm-administrator bypass before joining the group.
Discovery subscriptions on `/hubs/printers` apply the same owner-or-audited-
administrator rule. Discovery publishers are internal authenticated HTTP
ingestion routes; untrusted clients cannot invoke hub publisher methods.

SignalR messages are hints and progress notifications, not authoritative
state. After reconnect, token refresh, a sequence gap, or process restart,
clients must refetch the relevant REST resources. A successful automatic
reconnect does not imply that events sent during the gap were replayed.

### Event Formats

#### printerupdated
```json
{
  "id": "printer-uuid",
  "name": "Printer 1",
  "isOnline": true,
  "state": "Printing",
  "hotendTemp": 210.5,
  "hotendTarget": 210,
  "bedTemp": 60,
  "bedTarget": 60,
  "progress": 45.5,
  "timeRemaining": 1800,
  "currentFile": "calibration.gcode"
}
```

## Error Responses

### 400 Bad Request
```json
{
  "error": "Invalid request",
  "details": "Location name is required"
}
```

### 401 Unauthorized
```json
{
  "status": 401,
  "title": "Authentication required",
  "code": "authentication_required"
}
```

### 403 Forbidden
```json
{
  "status": 403,
  "title": "Permission denied",
  "code": "permission_denied"
}
```

### 404 Not Found
```json
{
  "error": "Not found",
  "details": "Printer with id 'xyz' not found"
}
```

### 409 Conflict
```json
{
  "error": "Conflict",
  "details": "Location name already exists"
}
```

### 426 Upgrade Required
```json
{
  "status": 426,
  "title": "Client upgrade required",
  "code": "client_upgrade_required",
  "apiContractVersion": "1.0",
  "minimumSupportedApiContractVersion": "1.0"
}
```

### 500 Server Error
```json
{
  "error": "Internal server error",
  "details": "An unexpected error occurred"
}
```

## Rate Limiting

API requests are rate limited per IP:
- **Tier 1**: 1000 requests/hour (default)
- **Tier 2**: 5000 requests/hour (authenticated users)

Rate limit headers:
```
X-RateLimit-Limit: 1000
X-RateLimit-Remaining: 999
X-RateLimit-Reset: 1639910400
```

## Pagination

List endpoints support pagination:

```
GET /api/printers?page=1&pageSize=10&sortBy=name&sortDirection=asc
```

**Response:**
```json
{
  "items": [...],
  "totalCount": 42,
  "page": 1,
  "pageSize": 10,
  "totalPages": 5
}
```

## Filtering

Endpoints support filtering:

```
GET /api/printers?backendType=Moonraker&isOnline=true&locationId=xyz
```

## Sorting

Supported sort fields (check specific endpoint documentation):

```
GET /api/printers?sortBy=name&sortDirection=asc
```
