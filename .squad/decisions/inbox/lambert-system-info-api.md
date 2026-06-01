## Decision Inbox: System Info API data sources

**Author:** Lambert  
**Date:** 2026-06-01T15:18:38-07:00  
**Status:** Proposed

## Decision

Implement `GET /api/system/info` as a thin controller over a read-only infra service.
Store the DTO contract in `src/infra/Dtos/SystemInfoDtos.cs` rather than creating a new
`src/shared/` project path, because this repo already keeps API-facing DTOs in `Farm.Infrastructure`.

## Architectural Calls

1. **Controller stays transport-only** — `SystemInfoController` only enforces `farm_admin`
   and returns the DTO from `ISystemInfoService`.
2. **Cross-platform host sampling stays inside the service** — Linux uses `/proc/stat` +
   `/proc/meminfo`; Windows uses `GetSystemTimes` + `GlobalMemoryStatusEx`; unsupported or
   inaccessible sources fall back to zero or `Process.WorkingSet64` rather than failing the endpoint.
3. **Archive metrics use existing storage abstraction** — `archiveBytes` comes from the
   directory tree rooted at `IStoragePathService.GetGcodeStorageDirectory()` and `archiveCount`
   comes from `AppDbContext.GcodeFiles`.
4. **Database metadata is provider-driven** — engine name comes from
   `AppDbContext.Database.ProviderName`; version and size use provider-specific scalar queries
   with SQLite file-size fallback matching the existing debug endpoint logic.

## Why

This keeps the endpoint read-only, testable, and consistent with the repo's existing DTO/service
layout. It also avoids hardcoding deployment-specific paths or assuming Linux-only host metrics,
which matters because PrintFarmer runs on both Windows and Linux.
