# Contributing to PrintFarmer

Thanks for your interest in contributing! This guide has common, practical instructions for a C#/.NET 9 solution with separate API backend and React frontend.

> If `.soft-freeze` exists at the repo root, a soft freeze is active. Only feature / test / doc changes should be made without an exception. See `SOFT_FREEZE.md` for restricted files and how to request an exception.

## Prerequisites
- .NET SDK 9.0 or later
- Git + a code editor (VS Code recommended)
- Optional (VS Code): Extensions "C#", "C# Dev Kit", and "Razor Language Server"

Verify your setup:
```powershell
# PowerShell
dotnet --info
```

## Repository layout
- `src/`
  - `client/` — Blazor WebAssembly Client (standalone frontend)
  - `api/` — ASP.NET Core API Server (standalone backend)
  - `shared/` — Shared DTOs/models
  - `farm-web.sln` — Solution file

## Restore and build
```powershell
# From repo root
cd .\src
# Restore
dotnet restore .\farm-web.sln
# Build (Debug)
dotnet build .\farm-web.sln -c Debug
# Build (Release)
dotnet build .\farm-web.sln -c Release
```

## Run (development)
Both API server and client need to be run separately during development.

**API Server (Backend):**
```powershell
# From repo root
cd .\src
# Run API Server
dotnet run --project .\api\Farm.Web.Api.csproj
```
API will be available at http://localhost:5245

**Client (Frontend) - Run in separate terminal:**
```powershell
# From repo root  
cd .\src
# Run Client
dotnet run --project .\client\Farm.Web.Client.csproj
```
Client will be available at http://localhost:5000

Stop both with Ctrl+C.

Tip: Use hot-reload/watch during active development:
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

## Code style and formatting
- C#: prefer conventional .NET style (PascalCase for types/members, camelCase for locals/params).
- Run formatter/analyzers locally:
```powershell
# Format C# code in the solution
cd .\src
dotnet format .\farm-web.sln
```

## EF Core and SQLite
- The application includes startup safety for SQLite to ease local development.
- Migrations are not required for typical dev runs; the app will bring the local DB to a usable state on startup.

## Git and PRs
- Create feature branches from `main`.
- Keep PRs small and focused; include a short summary of changes and any notes for reviewers.
- Ensure builds and tests pass locally before opening a PR.

## Troubleshooting
- Locked files during build (Windows): close any running app instances that might hold `bin/`/`obj/` outputs.
- Clean rebuild:
```powershell
# From repo root
rd /s /q .\src\client\bin; rd /s /q .\src\client\obj
rd /s /q .\src\api\bin; rd /s /q .\src\api\obj
rd /s /q .\src\shared\bin; rd /s /q .\src\shared\obj
cd .\src; dotnet restore .\farm-web.sln; dotnet build .\farm-web.sln -c Debug
```
- If ports are occupied, change the launch profile URLs in `api/Properties/launchSettings.json` or set `ASPNETCORE_URLS`.

## Questions
Open a discussion or an issue with clear repro steps, logs, and environment info (`dotnet --info`).
