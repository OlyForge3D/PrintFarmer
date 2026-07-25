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

## Printers API

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
    "url": "http://192.168.1.100:7125",
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
  "url": "http://192.168.1.100:7125",
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
  "url": "http://192.168.1.101:7125",
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

### Discover Printers on Network

```http
POST /api/discovery/scan
Content-Type: application/json

{
  "timeout": 30,
  "backendTypes": ["Moonraker", "PrusaLink"]
}
```

**Response:** 202 Accepted (returns stream of discoveries)

### Get Discovery Status

```http
GET /api/discovery/status
```

**Response:**
```json
{
  "isRunning": true,
  "progress": 45,
  "foundCount": 3,
  "totalCount": 8
}
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

**URL:** `http://localhost:5245/hubs/printers`

**Protocol:** WebSocket

### Listening for Events

```javascript
const connection = new HubConnectionBuilder()
  .withUrl('http://localhost:5245/hubs/printers', {
    withCredentials: true
  })
  .withAutomaticReconnect()
  .build();

// Listen for printer updates
connection.on('printerUpdated', (printer) => {
  console.log('Printer updated:', printer);
  // printer: { id, name, isOnline, state, hotendTemp, bedTemp, progress, ... }
});

// Listen for connection changes
connection.on('printerConnected', (printerId) => {
  console.log('Printer connected:', printerId);
});

connection.on('printerDisconnected', (printerId) => {
  console.log('Printer disconnected:', printerId);
});

// Listen for job progress
connection.on('jobProgressUpdated', (jobData) => {
  console.log('Job progress:', jobData);
  // jobData: { printerId, progress, timeRemaining, ... }
});

// Listen for location changes
connection.on('locationUpdated', (location) => {
  console.log('Location updated:', location);
});

// Start connection
connection.start().catch(err => console.error(err));
```

### Event Formats

#### printerUpdated
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

#### printerConnected
```json
{
  "id": "printer-uuid",
  "name": "Printer 1",
  "timestamp": "2025-12-19T10:30:00Z"
}
```

#### printerDisconnected
```json
{
  "id": "printer-uuid",
  "name": "Printer 1",
  "timestamp": "2025-12-19T10:30:00Z"
}
```

#### jobProgressUpdated
```json
{
  "printerId": "printer-uuid",
  "jobId": "job-uuid",
  "progress": 45.5,
  "timeRemaining": 1800,
  "timeElapsed": 1200
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
  "error": "Unauthorized",
  "details": "Invalid or expired token"
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
