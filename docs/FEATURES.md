# Features

## Location System

Organize your printers by physical location for easier management.

### Overview

The location system allows you to group printers by workspace, room, or any logical division. Each location can contain multiple printers and tracks basic information about the space.

### Data Model

Each location stores:
- **Name** - Location identifier (required, unique, e.g., "Workshop", "RACK1-01")
- **Description** - Context about the location (optional, e.g., "Main workspace area")
- **Printer Count** - Auto-updated count of assigned printers
- **Timestamps** - Created and last modified dates

Printers can optionally be assigned to a location. Unassigned printers have no location.

### Features

**Create Locations** - Define new locations with name and description  
**Assign Printers** - Link printers to locations via drag-and-drop or API  
**View Locations** - See all locations with printer counts  
**Edit Locations** - Update location name and description  
**Delete Locations** - Soft delete (printers remain unaffected)  
**Drag-and-Drop** - Visual interface for managing assignments

### Architecture

**Backend:**
- `Location` entity with Name (unique), Description, PrinterCount (denormalized)
- `Printer` entity enhanced with `LocationId` foreign key
- `ILocationService` with 15+ methods for CRUD and queries
- `ILocationRepository` for data access
- DTOs for API: `LocationDto`, `CreateLocationDto`, `UpdateLocationDto`, `LocationDetailsDto`

**Frontend:**
- `LocationService` - TypeScript API client
- `LocationManagement` - React component for admin CRUD
- `LocationSelector` - Dropdown for selecting location in forms
- `PrinterLocationDragDrop` - Drag-and-drop UI for assignments

### Quick Start

#### Create a Location

**UI:**
1. Go to **Admin > Manage Locations**
2. Click **Create Location**
3. Enter name (e.g., "Workshop") and description (e.g., "Main workspace")
4. Click **Create**

**API:**
```http
POST /api/locations
Content-Type: application/json

{
  "name": "Workshop",
  "description": "Main workspace area"
}
```

#### Assign Printer to Location

**UI (Drag-and-Drop):**
1. Go to **Admin > Assign Printers**
2. Drag unassigned printer to a location card
3. Drop to assign (automatic API update)

**UI (Dropdown):**
1. Go to printer edit/create form
2. Use LocationSelector dropdown to pick location
3. Save printer

**API:**
```http
POST /api/printers/{printerId}/location
Content-Type: application/json

{
  "locationId": "location-uuid"
}
```

#### Remove Printer from Location

**UI (Drag-and-Drop):**
1. Go to **Admin > Assign Printers**
2. Drag printer from location back to "Unassigned" area
3. Drop to unassign

**API:**
```http
DELETE /api/printers/{printerId}/location
```

### Drag-and-Drop Interface

**Layout:**
```
┌─────────────────────────────────────────────────┐
│ Unassigned  │ Location 1 │ Location 2 │ Loc... │
│ (4 printers)│(2 printers)│(3 printers)│        │
├─────────────┼───────────┼───────────┼────────┤
│ [Printer A] │[Printer C]│[Printer E]│        │
│ [Printer B] │[Printer D]│[Printer F]│        │
│ [Printer G] │           │           │        │
│ [Printer H] │           │           │        │
└─────────────┴───────────┴───────────┴────────┘
```

**Features:**
- Drag any printer card to assign/reassign
- Drag to "Unassigned" to remove from location
- Visual feedback: opacity changes while dragging
- Drop zone highlighting on hover
- Error messages if assignment fails
- Printer names and server URLs displayed
- Location printer count updates automatically

### Database Schema

```sql
-- Locations table
CREATE TABLE Locations (
  Id UNIQUEIDENTIFIER PRIMARY KEY,
  Name NVARCHAR(256) UNIQUE NOT NULL,
  Description NVARCHAR(1024),
  PrinterCount INT NOT NULL DEFAULT 0,
  IsActive BIT NOT NULL DEFAULT 1,
  CreatedAt DATETIME2 NOT NULL,
  ModifiedAt DATETIME2 NOT NULL
);

-- Printers table (enhanced)
CREATE TABLE Printers (
  Id UNIQUEIDENTIFIER PRIMARY KEY,
  Name NVARCHAR(256) NOT NULL,
  Url NVARCHAR(2048) NOT NULL,
  LocationId UNIQUEIDENTIFIER,  -- NEW: FK to Locations
  -- ... other fields
  FOREIGN KEY (LocationId) REFERENCES Locations(Id) ON DELETE SET NULL
);

-- Indexes for performance
CREATE UNIQUE INDEX IX_Locations_Name ON Locations(Name) WHERE IsActive = 1;
CREATE INDEX IX_Locations_IsActive ON Locations(IsActive);
CREATE INDEX IX_Printers_LocationId ON Printers(LocationId);
```

### API Endpoints

```
GET    /api/locations                    # List all locations
GET    /api/locations/{id}               # Get location details
POST   /api/locations                    # Create location
PUT    /api/locations/{id}               # Update location
DELETE /api/locations/{id}               # Delete location (soft delete)

POST   /api/printers/{id}/location       # Assign printer to location
DELETE /api/printers/{id}/location       # Unassign printer from location
```

### Response Examples

**GET /api/locations:**
```json
[
  {
    "id": "uuid",
    "name": "Workshop",
    "description": "Main workspace area",
    "printerCount": 3,
    "isActive": true,
    "createdAt": "2025-12-19T10:00:00Z",
    "modifiedAt": "2025-12-19T11:00:00Z"
  }
]
```

**POST /api/locations:**
```json
{
  "name": "Garage",
  "description": "Secondary printer area"
}
```

### Implementation Status

✅ **Complete:**
- Domain entities (Location, Printer FK)
- Service and repository layers
- DTOs and database config
- TypeScript service for API
- LocationManagement React component
- LocationSelector dropdown
- PrinterLocationDragDrop component
- All tests passing

⏳ **Pending:**
- LocationController REST API endpoints
- DI container registration
- AutoMapper profile
- Full integration tests

### Design Decisions

**Soft Deletes:** Locations are soft-deleted (IsActive flag), not hard-deleted. Printers remain unaffected.

**Denormalized Count:** PrinterCount cached on Location for query performance (updated on assignment/unassignment).

**Nullable Assignment:** LocationId is nullable - printers can exist without a location.

**Uniqueness:** Location names are unique and case-sensitive. Attempts to create duplicate raise conflict (409).

**Cascade Behavior:** Deleting a location doesn't delete printers - FK is SET NULL.

### Future Enhancements

- Hierarchy support (nested locations)
- Location-based statistics and alerts
- Role-based access control by location
- Location history and audit logs
- Bulk operations (move multiple printers)
- Location-specific printer health reports

---

## CSV Import/Export

Bulk import or export printer configurations using CSV format.

### Import Format

Create a CSV file with the following columns:

```csv
Name,URL,BackendType,ApiKey,LocationName
Printer 1,http://192.168.1.100:7125,Moonraker,xxx_api_key_xxx,Workshop
Printer 2,http://192.168.1.101:8080,PrusaLink,,Garage
Printer 3,http://192.168.1.102:7125,Moonraker,yyy_api_key_yyy,Workshop
```

### Columns

| Column | Required | Example | Notes |
|--------|----------|---------|-------|
| `Name` | Yes | Printer 1 | Display name for printer |
| `URL` | Yes | http://192.168.1.100:7125 | Backend server address |
| `BackendType` | Yes | Moonraker | Moonraker, PrusaLink, or SDCP |
| `ApiKey` | No | api_key_xxx | Required for secured APIs |
| `LocationName` | No | Workshop | Must match existing location |

### Import Steps

1. Go to **Admin > Import Printers**
2. Select CSV file
3. Preview imported printers
4. Click **Import**
5. Non-existent locations are created automatically (use default settings)

### Export Steps

1. Go to **Admin > Export Printers**
2. Select which columns to export
3. Click **Download CSV**
4. File contains all active printers

---

## Printer Discovery

Automatically discover printers on your network.

### Supported Backends

- **Moonraker** - Klipper-based printers (detection on port 7125)
- **PrusaLink** - Prusa printers (detection on port 8080)
- **SDCP** - Simple Data Communication Protocol

### Discovery Process

1. Go to **Admin > Discover Printers**
2. Select desired backends
3. Set timeout (30 seconds recommended)
4. Click **Start Scan**

### Discovery Steps

1. Network broadcast/port scan
2. Detect responding services
3. Verify service type (Moonraker/PrusaLink)
4. Add to "Suggested Printers" list
5. Quick add to system with one click

### Tips

- Run discovery during off-peak hours (it uses network bandwidth)
- Ensure printers are powered on and networked
- Discovery only works on local network (same subnet)
- Timeouts adjustable based on network size

---

## Job Queue

Monitor and control active and queued print jobs.

### Dashboard View

- Active jobs on all printers
- Progress bars with time estimates
- Current file, job status, printer name
- Quick action buttons (pause, resume, cancel)

### Job Status

- **Queued** - Waiting to print
- **Printing** - Currently printing
- **Paused** - Paused by user or printer
- **Completed** - Successfully finished
- **Failed** - Error during printing
- **Cancelled** - Cancelled by user

### Real-time Updates

Job status updates via SignalR:
- Progress percentage
- Time remaining
- Temperature changes
- State changes

### Actions

- **Pause** - Pause current print (if printer supports)
- **Resume** - Resume paused print
- **Cancel** - Cancel job and stop printer
- **View** - See job details and history

---

## Printer Profiles

Integrated OrcaSlicer profiles for slicing and printing.

### Profile Types

1. **Machine Profiles** - Printer model specifications
2. **Process Profiles** - Print settings (speed, temperature, layer height)
3. **Filament Profiles** - Material properties

### Profile Selection

When creating a job:

1. Select printer model
2. Choose process profile (speed preset)
3. Choose filament profile (material)
4. Generate G-code

### Supported Manufacturers

- Prusa (CORE One, MK3S+, MK4, etc.)
- Bamboo Lab (X1, X1E, P1, etc.)
- Creality (Ender series, CR series, etc.)
- Voron (0.1, 0.2, 2.4, etc.)
- And many more...

### Profile Management

- Auto-updated with OrcaSlicer releases
- Compatible printer detection
- Material compatibility checking
- Custom profile import/export

---

## Real-time Monitoring

Live status updates for all connected printers via SignalR.

### Status Information

- **Online Status** - Connected/disconnected
- **Printer State** - Idle, Printing, Paused, Error
- **Temperatures** - Current and target temps
- **Job Progress** - Percentage and time remaining
- **Firmware Version** - Printer firmware info

### Update Frequency

- **Heartbeat** - Every 5 seconds when connected
- **On Change** - Immediate update when status changes
- **Auto-reconnect** - Reconnect if connection lost

### Connection Status

Visual indicator shows:
- 🟢 Connected - Real-time updates active
- 🟡 Connecting - Attempting to establish connection
- 🔴 Disconnected - No real-time updates

---

## Health Checks & Diagnostics

Built-in health checks for system monitoring.

### Available Checks

- **Database** - Connection and query performance
- **API Server** - Server responsiveness
- **SignalR** - Real-time connection status
- **Printer Discovery** - Network service availability
- **External APIs** - Moonraker/PrusaLink connectivity

### Health Endpoints

```
GET /api/health        # Detailed health status
GET /api/healthz       # Quick health check
```

### Diagnostics Tools

Available in Admin panel:
- Connection test for each printer
- Network discovery test
- Database connectivity test
- SignalR connection diagnostic

---

## Multi-Database Support

PrintFarmer works with multiple database engines.

### Supported Databases

- **SQLite** (default) - File-based, no setup required
- **PostgreSQL** - Enterprise open-source
- **SQL Server** - Microsoft enterprise
- **MySQL** - Popular open-source

### Switching Databases

Set environment variables before startup:

```bash
# PostgreSQL
export DB_PROVIDER=postgres
export DB_CONNECTION_STRING="Host=localhost;Database=printfarmer;Username=postgres;Password=password"

# SQL Server
export DB_PROVIDER=sqlserver
export DB_CONNECTION_STRING="Server=localhost;Database=printfarmer;User Id=sa;Password=YourPassword123"

# MySQL
export DB_PROVIDER=mysql
export DB_CONNECTION_STRING="Server=localhost;Database=printfarmer;Uid=root;Pwd=password"

# SQLite (default)
export DB_PROVIDER=sqlite
# Uses farm.db file in current directory
```

### Migration

All databases use the same schema and data structure. Switching databases requires no schema changes.

---

## User Authentication

Secure multi-user access with role-based permissions.

### Initial Setup

On first run, you'll be prompted to create an administrator account:

1. Email address
2. Password (minimum 8 characters)
3. Confirm password

### User Roles

- **Administrator** - Full system access, user management
- **Operator** - View status, control printers, manage jobs
- **Viewer** - Read-only access to status

### Password Policy

- Minimum 8 characters
- Secure password hashing (PBKDF2)
- Session timeout after 1 hour of inactivity
- Token expiration

### Security

- HTTPS enforced in production
- Secure HttpOnly cookies for tokens
- CORS restricted to authorized origins
- Rate limiting on login attempts

---

## Troubleshooting Features

Tools to diagnose and resolve issues.

### Connection Test

1. Go to **Admin > Diagnostics**
2. Select printer
3. Click **Test Connection**
4. Results show API responses and timing

### Network Discovery Test

1. Go to **Admin > Discover Printers**
2. Run discovery
3. View detailed results for each found service

### Logs

System logs available in:
- `/logs/printfarmer.log` - Application logs
- Docker logs: `docker logs printfarmer-api`

### Common Issues

See [Troubleshooting Guide](./TROUBLESHOOTING.md) for solutions to common problems.
