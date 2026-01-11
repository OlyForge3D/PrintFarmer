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
- TypeScript: Use camelCase for variables/functions, PascalCase for components/types, strict mode enabled
- Run formatter/analyzers locally:
```powershell
# Format C# code in the solution
cd .\src
dotnet format .\farm-web.sln

# Lint React/TypeScript code
cd .\src\Web\ReactApp
npm run lint
```

## Tag Management System

PrintFarmer includes a comprehensive **polymorphic tagging system** for organizing 3D models and G-code files. See `docs/TAGGING_SYSTEM.md` for complete details.

### Quick Tag System Overview

**For End Users:**
- Navigate to `Admin → Tag Management` to create/manage tags
- Edit model tags in the Models page detail view
- Use bulk tagging for multiple models at once
- View tag analytics in the `Admin → Tag Management → Analytics` tab

**For Developers Adding Tags to New Objects:**

1. **Create Mapping Entity** (e.g., `PrinterTag` for tagging printers):
   ```csharp
   public class PrinterTag
   {
       public Guid PrinterId { get; set; }
       public Guid TagId { get; set; }
       public DateTime TaggedAt { get; set; }
       
       public Printer Printer { get; set; }
       public Tag Tag { get; set; }
   }
   ```

2. **Configure in DbContext:**
   ```csharp
   modelBuilder.Entity<PrinterTag>()
       .HasKey(pt => new { pt.PrinterId, pt.TagId });
   
   modelBuilder.Entity<PrinterTag>()
       .HasOne(pt => pt.Printer)
       .WithMany(p => p.TagMappings)
       .HasForeignKey(pt => pt.PrinterId)
       .OnDelete(DeleteBehavior.Cascade);
   
   modelBuilder.Entity<PrinterTag>()
       .HasOne(pt => pt.Tag)
       .WithMany()
       .HasForeignKey(pt => pt.TagId)
       .OnDelete(DeleteBehavior.Cascade);
   ```

3. **Add Repository Interface:**
   ```csharp
   public interface IPrinterTagRepository
   {
       Task<IEnumerable<Tag>> GetTagsForPrinterAsync(Guid printerId);
       Task AddTagToPrinterAsync(Guid printerId, Guid tagId);
       Task RemoveTagFromPrinterAsync(Guid printerId, Guid tagId);
   }
   ```

4. **Add Controller Endpoints:**
   ```csharp
   [HttpPost("{printerId}/tags/{tagId}")]
   public async Task<IActionResult> AddTag(Guid printerId, Guid tagId)
   {
       await _printerTagRepository.AddTagToPrinterAsync(printerId, tagId);
       return Ok();
   }
   ```

5. **Test thoroughly** and update documentation.

**Tag Name Normalization:**
- All tag names are automatically normalized to PascalCase
- "my-tag" → "MyTag", "MY_TAG" → "MyTag", "my tag" → "MyTag"
- Ensures consistency and prevents duplicate tags with different cases

**Polymorphic Architecture Benefits:**
- One `Tag` entity shared across all object types (3D Models, G-code Files, Printers, etc.)
- Single tag management interface for all taggable objects
- Efficient queries with type-specific mapping tables
- Easy to extend to new object types

For comprehensive guidance, see `docs/TAGGING_SYSTEM.md` which includes:
- Full architecture documentation
- User workflows (creating, assigning, bulk tagging)
- API endpoint reference
- Component guide
- Developer guide for extending the system

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
