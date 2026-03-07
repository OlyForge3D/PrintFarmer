# PrintFarmer Architecture

## System Overview

PrintFarmer uses a **two-tier client-server architecture** with real-time communication via SignalR.

```
┌────────────────────────────────────────┐
│     React TypeScript Frontend          │
│   (Vite @ localhost:3000)              │
│  - Dashboard UI                        │
│  - Printer management                  │
│  - Location organization               │
│  - CSV import/export                   │
├────────────────────────────────────────┤
│  HTTP/REST + WebSocket (SignalR)       │
├────────────────────────────────────────┤
│   ASP.NET Core 10 API Backend          │
│ (Kestrel @ localhost:5245)             │
│  - REST API endpoints                  │
│  - Backend Plugin Architecture:        │
│    • Moonraker (Klipper)               │
│    • PrusaLink (Prusa)                 │
│    • OctoPrint                         │
│    • SDCP (Generic printers)           │
│    • FlashForge (FlashForge printers)  │
│    • Core (base interfaces)            │
│  - Background services                 │
│  - Job queue management                │
│  - Profile/catalog management          │
│  - Real-time hub (SignalR)             │
├────────────────────────────────────────┤
│    Entity Framework Core ORM           │
├────────────────────────────────────────┤
│      Multi-Database Support            │
│  SQLite • PostgreSQL                   │
│  SQL Server • MySQL                    │
└────────────────────────────────────────┘
```

## Backend Architecture

### Layered Design

```
Controllers (REST API entry points)
     ↓
Services (Business logic, orchestration)
     ↓
Repositories (Data access)
     ↓
Entity Framework Core (ORM)
     ↓
Database (SQLite/PostgreSQL/SQL Server/MySQL)
```

### Key Components

#### Controllers
REST API endpoints organized by feature:
- `PrintersController` - Printer CRUD and management
- `LocationsController` - Location organization
- `CatalogController` - Printer models and manufacturers
- `DiscoveryController` - Network printer discovery
- `JobQueueController` - Job queue management
- `TagsController` - Job tagging and categorization
- `MaintenanceController` - Maintenance tracking
- `JobSchedulingController` - Job scheduling and automation
- `RetriesController` - Job retry management

#### Services
Business logic layer:
- `PrinterService` - Printer management, state tracking
- `LocationService` - Location CRUD and organization
- `DiscoveryService` - Moonraker/PrusaLink discovery
- `JobQueueService` - Job management and monitoring
- `BackendClientService` - Abstracts different printer APIs
- `CatalogService` - Model and manufacturer management

#### Clients
External API integration (typed HTTP clients via Refit):
- `IMoonrakerClient` - Moonraker API (Klipper firmware)
- `IPrusaLinkClient` - PrusaLink API (Prusa 3D printers)
- `IOctoPrintClient` - OctoPrint API
- `ISdcpClient` - SDCP protocol (generic printers)
- `IFlashForgeClient` - FlashForge printer API
- `ISpoolmanClient` - Spoolman integration (filament tracking)

#### Repositories
Data access abstraction:
- `PrinterRepository` - Printer persistence
- `LocationRepository` - Location persistence
- `JobRepository` - Job tracking
- `CatalogRepository` - Model/manufacturer data

#### Hubs (SignalR)
Real-time communication:
- `PrinterHub` - Live printer status updates
  - `printerUpdated` - Status change event
  - `printerConnected` - Printer comes online
  - `printerDisconnected` - Printer goes offline
  - `jobProgressUpdated` - Job progress events
  - `jobAutoDispatched` - Auto-dispatch event (when job auto-assigned)
  - `dispatchSuggestion` - Dispatch suggestion event (Suggest mode)
  - `dispatchFailed` - Dispatch failure event

### Database Schema

#### Core Entities

```
Printers
├── Id (GUID)
├── Name (string)
├── Url (string)
├── ApiKey (encrypted)
├── BackendType (enum: Moonraker, PrusaLink, SDCP)
├── LocationId (FK → Locations)
├── IsActive (soft delete)
├── CreatedAt, ModifiedAt
└── Location (navigation)

Locations
├── Id (GUID)
├── Name (string, unique)
├── Description (string, optional)
├── PrinterCount (int, denormalized)
├── IsActive (soft delete)
├── CreatedAt, ModifiedAt
└── Printers (navigation collection)

Jobs
├── Id (GUID)
├── PrinterId (FK)
├── Filename (string)
├── Status (enum: Queued, Printing, Completed, Error)
├── Progress (decimal)
├── StartedAt, CompletedAt
└── Printer (navigation)

Catalog (Manufacturers & Models)
├── Manufacturer
│  ├── Id
│  ├── Name (unique)
│  └── Models (navigation)
└── PrinterModel
   ├── Id
   ├── Name
   ├── ManufacturerId (FK)
   └── Specifications (JSON)
```

#### Features

**Soft Deletes**: All major entities have `IsActive` boolean flag for soft deletion  
**Timestamps**: All entities track `CreatedAt` and `ModifiedAt` in UTC  
**Normalization**: Printer counts denormalized on Location for performance  
**Encryption**: API keys encrypted at rest in database  

### Data Flow

#### Printer Status Update
```
External Printer API (Moonraker/PrusaLink)
    ↓
MoonrakerSubscriptionService (background service)
    ↓
PrinterHub.SendAsync("printerUpdated", status)
    ↓
React Client receives real-time update
    ↓
UI re-renders with new state
```

#### Job Assignment
```
React UI: User assigns printer to location
    ↓
POST /api/printers/{id}/location { locationId }
    ↓
PrintersController.AssignLocation()
    ↓
PrinterService.AssignToLocation()
    ↓
LocationService.UpdatePrinterCount()
    ↓
Database updated
    ↓
SignalR broadcast: printerUpdated
    ↓
All connected clients update UI
```

## Location Hierarchy Architecture

The location system organizes printers into a tree structure using the **Adjacency List + Cached Path** approach, enabling hierarchical organization (Warehouse > Floor > Room > Rack) while maintaining query efficiency.

### Schema Design

```
Locations Table
├── Id (GUID, Primary Key)
├── ParentId (FK → Locations, nullable)  — Enables tree structure
├── Name (string)
├── Path (string, indexed)               — Cached materialized path (e.g., "/Warehouse 1/Room A")
├── Depth (int, indexed)                 — Tree depth (0 = root)
├── SortOrder (int)                      — Display order among siblings
├── LocationTypeId (FK → LocationTypes)  — Building, Floor, Room, Rack, etc.
├── PrinterCount (int)                   — Printers directly assigned to this location
├── TotalPrinterCount (int)              — All printers in subtree (denormalized)
├── IsActive (bool)                      — Soft delete flag
├── CreatedAt, ModifiedAt (DateTime)
└── UNIQUE(ParentId, Name)              — No duplicate names at same level
```

### Key Design Decisions

1. **Arbitrary Depth** — No fixed level limit. Customers define their own hierarchy.
2. **User-Defined Types** — LocationType entity allows custom organizational vocabulary (7 seeded types: Building, Floor, Room, Zone, Rack, Shelf, Workstation).
3. **Cached Path** — Single `Path` column (e.g., "/Warehouse 1/Room A/Rack 3") enables:
   - Fast breadcrumb generation without recursion
   - Efficient descendant queries using `Path LIKE '/Warehouse%'`
   - Automatic propagation when moving locations
4. **Printer Assignment** — Printers can attach to ANY level (leaf or intermediate nodes).
5. **Denormalized Counts** — `TotalPrinterCount` avoids expensive recursive counting.

### Tree Operations

**GetTree** (nested hierarchy):
```
Query: SELECT * FROM Locations WHERE IsActive = true
Execution: Build tree in-memory using ParentId as join key
Result: Hierarchical LocationTreeDto with children
```

**GetAncestors** (breadcrumbs):
```
Query: SELECT * FROM Locations WHERE Path LIKE (target Path + '%')
Execution: Path provides direct ancestry chain
Result: Ordered list from root to target node
```

**GetDescendants** (subtree):
```
Query: SELECT * FROM Locations WHERE Path LIKE (parent Path + '%') AND IsActive = true
Execution: Indexed Path lookup eliminates recursion
Result: Flat list of all children/descendants
```

**Move** (reparent with circular reference detection):
```
Checks:
  1. Validate new parent exists
  2. Prevent circular reference (new parent cannot be descendant of node being moved)
  3. Check max depth (prevent trees > 50 levels)
  4. Verify no name duplication at destination level
Operations:
  1. Update Location.ParentId
  2. Recalculate Path and Depth
  3. Cascade Path updates to all descendants
  4. Update TotalPrinterCount for affected nodes
```

### Frontend Components

- **LocationTreePicker** — Tree selector for assignment UI
- **LocationBreadcrumb** — Navigation breadcrumbs using ancestors
- **LocationManagement** — CRUD and drag-drop reorganization
- **LocationSelector** — Dropdown for printer assignment

### API Endpoints

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/locations` | GET | Flat list of all locations |
| `/api/locations/tree` | GET | Full hierarchy (nested) |
| `/api/locations/{id}` | GET | Single location details |
| `/api/locations/{id}/ancestors` | GET | Path to root (breadcrumbs) |
| `/api/locations/{id}/descendants` | GET | All children (subtree) |
| `/api/locations` | POST | Create new location |
| `/api/locations/{id}` | PUT | Update location |
| `/api/locations/{id}/move` | POST | Reparent location |
| `/api/locations/{id}` | DELETE | Soft delete location |

---

## Auto-Dispatch Architecture

The auto-dispatch system scores all available printers against job requirements using a **9-factor multi-criteria algorithm**, enabling intelligent printer selection with full transparency into scoring decisions.

### Scoring Model

**9 Scoring Factors** (4 hard filters + 5 soft scoring):

| # | Factor | Weight | Type | Description |
|---|--------|--------|------|-------------|
| 1 | Material Match | 100 | HARD | Job material must exist on printer |
| 2 | Nozzle Diameter | 100 | HARD | Must match ±0.01mm tolerance |
| 3 | Build Volume | 50 | SOFT | Printer must fit part dimensions |
| 4 | Enclosure | 80 | COND. | Bonus if available; required for some materials |
| 5 | Nozzle Hardness | 80 | COND. | Bonus for hardened nozzles with abrasive materials |
| 6 | Model Match | 60 | SOFT | Bonus for printer model matching job profile |
| 7 | Queue Depth | 30 | SOFT | Penalty: printers with many queued jobs score lower |
| 8 | Preferred | 40 | COND. | Bonus if user marked printer as preferred for material |
| 9 | Availability | 0 | HARD | Pre-filter: must be online, idle, and accepting jobs |

**Hard Filters** eliminate candidates immediately (score = 0, elimination reason logged).  
**Soft Factors** contribute to final score (max 360 points possible).  
**Conditional Factors** apply only in specific scenarios (e.g., enclosure bonus, preferred printer).

### Dispatch Service Architecture

```
JobDispatchService (orchestration)
├── DispatchScorer (algorithm)
│   ├── EvaluateMaterialMatch()
│   ├── EvaluateNozzleDiameter()
│   ├── EvaluateBuildVolume()
│   ├── EvaluateEnclosure()
│   ├── EvaluateNozzleHardness()
│   ├── EvaluateModelMatch()
│   ├── EvaluateQueueDepth()
│   ├── EvaluatePreferred()
│   └── EvaluateAvailability()
├── DispatchLog (audit trail)
│   └── Records every dispatch decision + scores
└── DispatchSettings (configuration)
    └── AutoDispatchEnabled, AutoDispatchMode, IdleThresholdSeconds, MinimumScoreThreshold
```

### Background Service Pattern

The **AutoDispatchBackgroundService** monitors idle printers and queued jobs using an event-driven model:

```
Architecture:
  - Channel<Guid> for fire-and-forget idle notifications
  - SemaphoreSlim(1, 1) to serialize dispatch decisions
  - Per-printer CancellationTokenSource for cancellation
  - DispatchSettings singleton for configuration

Trigger:
  1. Printer goes idle → MoonrakerSubscriptionService publishes idle event
  2. Event queued to Channel<Guid>
  3. Background service dequeues → FindCandidatesAsync(jobId)
  4. Top candidate scored
  5. Based on DispatchSettings.AutoDispatchMode:
     - "Suggest": SignalR notify operator (UI shows recommendation)
     - "Auto": Directly dispatch to top candidate

Safety:
  - Idle threshold (default 30s) prevents mid-print triggers
  - Minimum score threshold prevents low-quality dispatch
  - Max concurrent dispatches limits system load
```

### Dispatch Modes

**Suggest Mode** (Default):
- Operator receives notification: "Recommend Printer X for Job Y (score: 95/100)"
- Operator confirms or selects different printer
- Useful for safety-critical or high-value jobs

**Auto Mode**:
- System automatically dispatches to highest-scoring printer if score ≥ minimumScoreThreshold
- No operator intervention required
- Dispatcher monitors and can manually intervene if needed

### SignalR Events

| Event | Payload | Trigger |
|-------|---------|---------|
| `jobAutoDispatched` | `{ jobId, printerId, score, dispatchedBy, timestamp }` | Job auto-assigned (Auto mode) |
| `dispatchSuggestion` | `{ jobId, topCandidates[], dispatchedBy, timestamp }` | Suggestion sent (Suggest mode) |
| `dispatchFailed` | `{ jobId, reason, timestamp }` | Dispatch error or no eligible printers |

### API Endpoints

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/job-queue/{id}/candidates` | GET | Score all printers for job |
| `/api/job-queue/{id}/dispatch-to` | POST | Assign job to specific printer |
| `/api/dispatch-settings` | GET | Read current settings |
| `/api/dispatch-settings` | PUT | Update settings |

### Dispatch Log

All dispatch decisions recorded for audit, analysis, and machine learning:

```
DispatchLog Table
├── Id (GUID)
├── JobId (FK)
├── PrinterId (FK)
├── DispatchedBy (user email)
├── DispatchScore (decimal)
├── ScoreFactors (JSON: all 9 factors + individual scores)
├── DispatchMode ("Suggest" or "Auto")
├── IsSuccessful (bool)
├── ErrorReason (nullable)
├── CreatedAt (DateTime)
└── Candidates (nullable JSON: all scored printers)
```

---



### Directory Structure

```
src/Web/ReactApp/
├── src/
│   ├── components/        # React components
│   │   ├── Dashboard.tsx
│   │   ├── PrinterCard.tsx
│   │   ├── LocationManagement.tsx
│   │   ├── LocationSelector.tsx
│   │   ├── PrinterLocationDragDrop.tsx
│   │   └── ...
│   ├── pages/            # Page-level components
│   │   ├── PrintersPage.tsx
│   │   ├── AdminPage.tsx
│   │   └── ...
│   ├── contexts/         # React Context for state
│   │   ├── AuthContext.tsx
│   │   ├── PrinterContext.tsx
│   │   └── ...
│   ├── services/         # API communication
│   │   ├── apiClient.ts
│   │   ├── printerService.ts
│   │   ├── locationService.ts
│   │   ├── jobService.ts
│   │   ├── printerSignalR.ts
│   │   └── ...
│   ├── types/            # TypeScript interfaces
│   │   ├── api.ts
│   │   ├── models.ts
│   │   └── ...
│   ├── utils/            # Utility functions
│   └── App.tsx           # Root component
├── public/               # Static assets
├── vite.config.ts        # Vite configuration
└── tsconfig.json         # TypeScript configuration
```

### Component Architecture

```
App
├── Router (React Router)
│  ├── Dashboard Page
│  │  ├── PrinterGrid
│  │  │  └── PrinterCard[] (real-time via Context)
│  │  ├── JobQueue
│  │  └── LocationFilter
│  ├── Admin Page
│  │  ├── LocationManagement (CRUD)
│  │  ├── PrinterLocationDragDrop (assignment)
│  │  ├── CatalogManagement
│  │  └── DiscoveryTool
│  └── Settings Page
├── AuthContext (user, token)
├── PrinterContext (live status from SignalR)
└── SignalR Connection (auto-reconnect)
```

### State Management

**React Query** for server state:
- Cached API responses
- Automatic refetching
- Optimistic updates

**React Context** for app state:
- Authentication (user, token, roles)
- Real-time printer status
- UI state (modals, notifications)

**SignalR Hubs** for real-time updates:
- Printer status changes
- Job progress
- Discovery events
- Location changes

### Styling

- **Tailwind CSS** - Utility-first CSS framework
- **Color System** - Consistent palette (primary, success, warning, error)
- **Responsive Design** - Mobile-first approach
- **Dark Mode Support** - Theme context for light/dark

## Integration Points

### External Printer APIs

1. **Moonraker** (Klipper-based)
   - WebSocket for real-time updates
   - REST API for commands
   - Temperature, job status, system info

2. **PrusaLink** (Prusa printers)
   - REST API only
   - Polling for status (no WebSocket)
   - Print job management

3. **SDCP** (Some printers)
    - WebSocket + JSON request/response protocol (typically at `/websocket`)
    - Polled for status (no real-time subscription)
    - Connections are short-lived per operation; an idle keepalive/ping is not required in the current implementation
    - File operations are not fully implemented yet (e.g., file listing may be empty)

### Catalog Integration

- OrcaSlicer profiles bundled with system
- Profile matching via printer model
- Automatic profile recommendation for job slicing

## Real-time Communication (SignalR)

### Connection Flow

```
React App Startup
    ↓
Create SignalR Connection to /hubs/printers
    ↓
On Connected: Request initial printer state
    ↓
Listen for events:
  - printerUpdated (periodic heartbeat or change)
  - printerConnected (new printer online)
  - printerDisconnected (printer offline)
  - jobProgressUpdated (slicing/printing progress)
    ↓
Update Context state on event
    ↓
Components re-render (using Context)
```

### Event Format

All events use **camelCase JSON** for TypeScript compatibility:

```json
{
  "id": "printer-uuid",
  "name": "Printer Name",
  "isOnline": true,
  "state": "Printing",
  "hotendTemp": 210.5,
  "hotendTarget": 210,
  "bedTemp": 60,
  "bedTarget": 60,
  "progress": 45.5,
  "eta": 1800
}
```

## Security Architecture

### Authentication
- JWT token-based
- Secure HttpOnly cookies
- Token refresh mechanism
- Role-based access control (RBAC)

### Data Protection
- API keys encrypted at rest
- HTTPS in production
- CORS validation
- Input validation and sanitization

### Network Security
- Printer discovery limited to local network
- API key masking in logs
- Secrets not hardcoded
- Environment-based configuration

## Scalability Considerations

### Database
- Multi-provider support enables vendor choice
- Connection pooling optimized
- Printer count denormalization for large fleets

### Real-time Updates
- SignalR automatic reconnection
- Heartbeat mechanism prevents client stale state
- Broadcast optimization for large printer counts

### Backend Services
- Background job processing
- Async/await throughout
- Rate limiting on external APIs
- Circuit breaker pattern for resilience

## Development Best Practices

### Backend (.NET)
- Dependency injection for testability
- Async/await for I/O operations
- Structured logging (Serilog)
- Unit and integration tests

### Frontend (React)
- **Feature-based architecture** - Components organized by domain (gcode, printers, models3d, slicer, etc.)
- Component composition and reusability
- Custom hooks for logic sharing
- TypeScript for type safety
- React Testing Library for tests
- Error boundaries for resilience

#### Frontend Directory Structure

```
src/Web/ReactApp/src/
├── features/                    # Feature-based organization
│   ├── gcode/                   # G-code library and management
│   │   ├── pages/               # GcodeLibrary, FilesPage
│   │   ├── components/          # FileBrowser, harvest components
│   │   └── hooks/               # Feature-specific hooks
│   ├── models3d/                # 3D model viewing
│   │   ├── pages/               # ModelsPage, Models3DViewerPage
│   │   ├── components/3d/       # ModelViewer3D, GCodeViewer3D
│   │   └── hooks/               # useStlUpload, etc.
│   ├── printers/                # Printer management
│   │   ├── pages/               # PrintersPage
│   │   ├── components/          # Printer cards, discovery, modals
│   │   └── hooks/               # Printer-specific hooks
│   ├── slicer/                  # Slicing operations
│   │   ├── pages/               # NewSliceJobPage, SliceJobsPage
│   │   ├── components/          # Job components, profile selector
│   │   └── hooks/               # Slicer-specific hooks
│   ├── queue/                   # Print job queue
│   │   ├── pages/               # QueuePage
│   │   └── components/          # Queue components
│   ├── catalog/                 # Manufacturer and materials
│   │   ├── pages/               # CatalogPage, SpoolsPage
│   │   └── components/          # Filament, location, tag components
│   ├── auth/                    # Authentication
│   │   ├── pages/               # LoginPage, ProfilePage
│   │   ├── components/          # Auth forms, setup wizard
│   │   └── hooks/               # useAuth
│   └── admin/                   # Admin tools
│       ├── pages/               # AdminPage, LogsPage, SettingsPage
│       └── components/          # Admin components
├── common/                      # Shared across features
│   ├── components/              # Reusable UI components
│   │   ├── ui/                  # Button, Input, Select, etc.
│   │   ├── nav/                 # Navigation components
│   │   ├── modals/              # Modal dialogs
│   │   ├── skeletons/           # Loading skeletons
│   │   └── icons/               # Icon components
│   ├── hooks/                   # Shared hooks (useApi, useSignalR)
│   ├── contexts/                # React contexts (AuthContext)
│   └── utils/                   # Utility functions
├── services/                    # API clients and services
├── types/                       # TypeScript type definitions
└── App.tsx                      # Main application entry
```

**Design Principles:**
- Features are self-contained with clear boundaries
- Common components are shared via `common/` directory
- Import paths use `@/features/*` and `@/common/*` aliases
- Each feature can have its own pages, components, and hooks

## Deployment Architecture

### Single Machine
```
Docker Container (All services)
├── API (port 5245)
├── React (port 3000, served by Nginx)
├── Database (SQLite file or container)
└── Health checks
```

### Microservices
```
Docker Network
├── API Container (port 5245)
├── Frontend Container (Nginx, port 80)
├── Database Container (PostgreSQL/SQL Server)
├── OrcaSlicer Worker Container
└── Health check containers
```

See **[Deployment Guide](./DEPLOYMENT.md)** for detailed deployment options.
