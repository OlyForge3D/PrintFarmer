# PrintFarmer

A Blazor WebAssembly (hosted) dashboard for managing multiple 3D printers. Supports Moonraker and PrusaLink backends, normalizes camera URLs, resolves hostnames to IPs, and streams live status via SignalR.

## Features
- Multi-backend: Moonraker and PrusaLink
- Hostname → IP resolution with preservation of original URL
- Camera snapshot/stream URL normalization
- Live printer status via SignalR
- Embedded SQLite for simple local setup

## Repository structure
```
src/
  client/   # Blazor WebAssembly client
  server/   # ASP.NET Core server (hosts the client + API)
  shared/   # Shared DTOs and models
  farm-web.sln
```

## Prerequisites
- .NET SDK 8.0+
- Windows/macOS/Linux

Verify:
```powershell
dotnet --info
```

## Quick start (development)
Run the hosted server (which serves the client and the API):
```powershell
# From repo root
cd .\src
# Restore and build
dotnet restore .\farm-web.sln
dotnet build .\farm-web.sln -c Debug
# Run the server
dotnet run --project .\server\Farm.Web.Server.csproj
```
- Browse to the URL printed in the console (typically http://localhost:5xxx).
- Stop with Ctrl+C.

Faster inner loop with hot reload:
```powershell
cd .\src
dotnet watch --project .\server\Farm.Web.Server.csproj run
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

## Data
- SQLite file lives under `src/server` by default (e.g., `farm.db`). Startup includes safety steps for local development.

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

Examples:
- DB_PROVIDER=Sqlite and ConnectionStrings__Default=Data Source=/data/farm.db
- DB_PROVIDER=Postgres and ConnectionStrings__Postgres=Host=postgres;Database=forgeiq;Username=postgres;Password=postgres
- DB_PROVIDER=SqlServer and ConnectionStrings__SqlServer=Server=sqlserver;Database=forgeiq;User Id=sa;Password=Your_password123;TrustServerCertificate=True;
- DB_PROVIDER=MySql and ConnectionStrings__MySql=Server=mysql;Database=forgeiq;User=root;Password=example;

Note: Sqlite applies EF migrations by default. Other providers currently use EnsureCreated unless DB_INIT_MODE=EnsureCreated is explicitly set.

### Environment variables
- ASPNETCORE_URLS: Listening URL inside the container (default set to http://0.0.0.0:8080).
- ASPNETCORE_ENVIRONMENT: Development or Production (defaults to Production in compose sample).
- ConnectionStrings__Default: EF Core connection string; use `Data Source=/data/farm.db` to persist under the mounted volume.

### Volumes and data
- The default appsettings uses `Data Source=farm.db` (relative), which lives in the working directory.
- In containers, prefer mounting a volume and overriding the connection string to `/data/farm.db`.
- Compose sample defines a named volume `printfarmer-data` mapped to `/data`.
