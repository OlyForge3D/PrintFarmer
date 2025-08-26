# Contributing to PrintFarmer

Thanks for your interest in contributing! This guide has common, practical instructions for a C#/.NET 8 Blazor WebAssembly (hosted) solution.

## Prerequisites
- .NET SDK 8.0 or later
- Git + a code editor (VS Code recommended)
- Optional (VS Code): Extensions "C#", "C# Dev Kit", and "Razor Language Server"

Verify your setup:
```powershell
# PowerShell
dotnet --info
```

## Repository layout
- `src/`
  - `client/` — Blazor WebAssembly Client
  - `server/` — ASP.NET Core Server (hosts the client and API)
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

## Run (hosted)
The Server project hosts the Client; running the server is usually all you need during development.
```powershell
# From repo root
cd .\src
# Run Server (serves API + Blazor WASM)
dotnet run --project .\server\Farm.Web.Server.csproj
```
- Default URLs are printed to the console (typically http://localhost:5xxx).
- Stop with Ctrl+C.

Tip: Use hot-reload/watch during active development:
```powershell
# Watch mode with hot reload
cd .\src
dotnet watch --project .\server\Farm.Web.Server.csproj run
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
rd /s /q .\src\server\bin; rd /s /q .\src\server\obj
rd /s /q .\src\shared\bin; rd /s /q .\src\shared\obj
cd .\src; dotnet restore .\farm-web.sln; dotnet build .\farm-web.sln -c Debug
```
- If ports are occupied, change the launch profile URLs in `server/Properties/launchSettings.json` or set `ASPNETCORE_URLS`.

## Questions
Open a discussion or an issue with clear repro steps, logs, and environment info (`dotnet --info`).
