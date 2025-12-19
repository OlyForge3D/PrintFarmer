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
│   ASP.NET Core 9.0 API Backend         │
│ (Kestrel @ localhost:5245)             │
│  - REST API endpoints                  │
│  - Printer integration (Moonraker,     │
│    PrusaLink, SDCP)                    │
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
- `JobsController` - Job queue management
- `ProfilesController` - Slicer profiles

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
- `IMoonrakerClient` - Moonraker API
- `IPrusaLinkClient` - PrusaLink API
- `ISdcpClient` - SDCP protocol
- `ISpoolmanClient` - Spoolman integration

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

## Frontend Architecture

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
   - Simple binary protocol
   - Basic status and control

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
- Component composition
- Custom hooks for logic sharing
- TypeScript for type safety
- React Testing Library for tests
- Error boundaries for resilience

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
