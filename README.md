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
