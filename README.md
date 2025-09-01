# PrintFarmer

A Blazor WebAssembly dashboard for managing multiple 3D printers. Supports Moonraker and PrusaLink backends, normalizes camera URLs, resolves hostnames to IPs, and streams live status via SignalR.

## Features
- Multi-backend: Moonraker and PrusaLink
- Hostname → IP resolution with preservation of original URL
- Camera snapshot/stream URL normalization
- Live printer status via SignalR
- Embedded SQLite for simple local setup

## Repository structure
```
src/
  client/   # Blazor WebAssembly client (standalone frontend)
  api/      # ASP.NET Core API server (standalone backend)
  shared/   # Shared DTOs and models
  farm-web.sln
```

## Prerequisites
- .NET SDK 9.0+
- Windows/macOS/Linux

Verify:
```powershell
dotnet --info
```

## Quick start (development)
Run both the API server and client separately:

**API Server (Backend):**
```powershell
# From repo root
cd .\src
# Restore and build
dotnet restore .\farm-web.sln
dotnet build .\farm-web.sln -c Debug
# Run the API server
dotnet run --project .\api\Farm.Web.Api.csproj
```
API will be available at http://localhost:5245

**Client (Frontend) - Run in separate terminal:**
```powershell
cd .\src
# Run the client
dotnet run --project .\client\Farm.Web.Client.csproj
```
Client will be available at http://localhost:5000

Stop both with Ctrl+C.

Faster inner loop with hot reload:
```powershell
# API server (first terminal)
cd .\src
dotnet watch --project .\api\Farm.Web.Api.csproj run

# Client (second terminal)
cd .\src
dotnet watch --project .\client\Farm.Web.Client.csproj run
```

## Tests
```powershell
# From repo root
dotnet test .\src\farm-web.sln -c Debug
```

## API (quick peek)
- GET `/api/printers` — list printers
- POST `/api/printers` — add a printer
- PUT `/api/printers/{id}` — update a printer
- DELETE `/api/printers/{id}` — remove a printer
- POST `/api/printers/resolve` — resolve/normalize a server URL and return IP-based URL
- GET `/healthz` — health probe

Note: The server normalizes camera URLs and stores both the original and IP-based server URLs.

## Conventions (minimal)
- Async-suffix: All asynchronous methods in controllers and services use the `Async` suffix (for example, `GetPrintersAsync`). This clarifies intent and aligns with analyzers; route templates remain unchanged.
- CreatedAtRoute for POST: Resource-creating POST endpoints return 201 Created with a Location header via `CreatedAtRoute(...)`. Ensure the matching GET-by-id route is named (for example, `Name = "GetPrinterById"`) so POSTs can reference it.

## Data
- SQLite file lives under `src/api` by default (e.g., `farm.db`). Startup includes safety steps for local development.

## Contributing
See [.github/CONTRIBUTING.md](.github/CONTRIBUTING.md) for setup, workflows, and troubleshooting.

## Troubleshooting
- Build/restore issues: try a clean restore and rebuild.
- Locked files on Windows: close any running app instances that may hold `bin/`/`obj/` outputs.

## Docker

Two-container setup is provided: web (Nginx + HTTPS serving the WASM client and reverse proxy) and api (ASP.NET Core).

### Image build
```powershell
docker build -t printfarmer:latest .
```

### Run (single container)
```powershell
# Persist database to a host volume and expose port 8080
docker run -d --name printfarmer -p 8080:8080 ^
  -e ASPNETCORE_URLS=http://0.0.0.0:8080 ^
  -e ASPNETCORE_ENVIRONMENT=Production ^
  -e ConnectionStrings__Default=Data Source=/data/farm.db ^
  -v printfarmer-data:/data ^
  printfarmer:latest
```

### Docker Compose
```yaml
services:
  printfarmer:
    build: .
    image: printfarmer:latest
    container_name: printfarmer
    environment:
      - ASPNETCORE_URLS=http://0.0.0.0:8080
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__Default=Data Source=/data/farm.db
    ports:
      - "8080:8080"
    volumes:
      - printfarmer-data:/data
    restart: unless-stopped

volumes:
  printfarmer-data:
```

For the 2-container setup and HTTPS, use the provided docker-compose.yml at repo root:

```
docker compose up -d --build
```

Web will be available at http://localhost:8081 and https://localhost:8443. API is proxied at /api.

### Database providers

The server supports Sqlite (default), SqlServer, Postgres, and MySql. Select with DB_PROVIDER and matching ConnectionStrings__ variables.

**Shared migrations:** The system uses provider-agnostic EF Core migrations that work across all supported databases. Migrations are attempted first, with fallback to EnsureCreated if they fail.

**Testing different providers:**

1. **Using provided database services:**
   ```bash
   # Start PostgreSQL for testing
   docker compose -f docker-compose.databases.yml up postgres -d
   
   # Run API with PostgreSQL  
   docker compose up api web -d \
     -e DB_PROVIDER=Postgres \
     -e ConnectionStrings__Postgres="Host=localhost;Database=printfarmer;Username=postgres;Password=postgres"
   ```

2. **Automated testing:**
   ```bash  
   # Test all providers with the included script
   ./test-providers.sh
   ```

Examples:
- DB_PROVIDER=Sqlite and ConnectionStrings__Default=Data Source=/data/farm.db
- DB_PROVIDER=Postgres and ConnectionStrings__Postgres=Host=postgres;Database=printfarmer;Username=postgres;Password=postgres
- DB_PROVIDER=SqlServer and ConnectionStrings__SqlServer=Server=sqlserver;Database=printfarmer;User Id=sa;Password=Your_password123;TrustServerCertificate=True;
- DB_PROVIDER=MySql and ConnectionStrings__MySql=Server=mysql;Database=printfarmer;User=root;Password=example;

Note: All providers now use shared migrations that are applied automatically on startup.

### Environment variables
- ASPNETCORE_URLS: Listening URL inside the container (default set to http://0.0.0.0:8080).
- ASPNETCORE_ENVIRONMENT: Development or Production (defaults to Production in compose sample).
- ConnectionStrings__Default: EF Core connection string; use `Data Source=/data/farm.db` to persist under the mounted volume.

### Volumes and data
- The default appsettings uses `Data Source=farm.db` (relative), which lives in the working directory.
- In containers, prefer mounting a volume and overriding the connection string to `/data/farm.db`.
- Compose sample defines a named volume `printfarmer-data` mapped to `/data`.
