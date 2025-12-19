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

### List All Locations

```http
GET /api/locations
```

**Response:**
```json
[
  {
    "id": "location-uuid",
    "name": "Workshop",
    "description": "Main workshop area",
    "printerCount": 3,
    "createdAt": "2025-12-19T10:00:00Z"
  }
]
```

### Get Location Details

```http
GET /api/locations/{locationId}
```

**Response:**
```json
{
  "id": "location-uuid",
  "name": "Workshop",
  "description": "Main workshop area",
  "printerCount": 3,
  "printers": [
    {
      "id": "printer-uuid",
      "name": "Printer 1",
      "url": "http://192.168.1.100:7125"
    }
  ],
  "createdAt": "2025-12-19T10:00:00Z",
  "modifiedAt": "2025-12-19T11:00:00Z"
}
```

### Create Location

```http
POST /api/locations
Content-Type: application/json

{
  "name": "Workshop",
  "description": "Main workshop area"
}
```

**Response:** 201 Created
```json
{
  "id": "new-location-uuid",
  "name": "Workshop",
  "description": "Main workshop area",
  "printerCount": 0,
  "createdAt": "2025-12-19T10:30:00Z"
}
```

### Update Location

```http
PUT /api/locations/{locationId}
Content-Type: application/json

{
  "name": "Workshop Updated",
  "description": "Updated description"
}
```

**Response:** 200 OK

### Delete Location

```http
DELETE /api/locations/{locationId}
```

**Response:** 204 No Content

## Jobs API

### List Active Jobs

```http
GET /api/jobs
```

**Response:**
```json
[
  {
    "id": "job-uuid",
    "printerId": "printer-uuid",
    "filename": "calibration.gcode",
    "status": "Printing",
    "progress": 45.5,
    "startedAt": "2025-12-19T10:00:00Z",
    "estimatedCompletionTime": 1800
  }
]
```

### Get Job Details

```http
GET /api/jobs/{jobId}
```

### Pause Job

```http
POST /api/jobs/{jobId}/pause
```

### Resume Job

```http
POST /api/jobs/{jobId}/resume
```

### Cancel Job

```http
POST /api/jobs/{jobId}/cancel
```

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
