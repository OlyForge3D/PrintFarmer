# Architecture Plan: Blocked & Deferred TODO Items

## Team Analysis Summary

Three Squad members analyzed the 5 blocked/deferred items in parallel:
- **🏗️ Dallas (Lead)** — Architecture decisions, interface design, implementation plans
- **🔧 Lambert (Backend)** — Deep codebase analysis, feasibility, risk factors
- **🔍 Brett (Researcher)** — Competitive analysis across 10+ print farm tools

## Consolidated Decisions

### ✅ ITEM 1: Camera Management → REOPENED as Phase 1.5 (Platform Feature)

**Revised consensus: Implement as platform-level camera management layer.**

The original analysis focused narrowly on firmware-level camera control (Moonraker/PrusaLink APIs don't support enable/disable). The user correctly identified that camera management belongs ABOVE the backend level — PrintFarmer should manage cameras as first-class entities independent of printer firmware.

**What already exists (80% built):**
- `Camera` entity with `IsEnabled`, `Name`, `StreamUrl`, `SnapshotUrl`, CRUD, DTOs ✅
- `CamerasController` with full CRUD + toggle endpoint ✅
- `ISupportsCamera` interface (Moonraker, OctoPrint, PrusaLink) ✅
- `DisplayCameraDto` merging standalone + printer cameras ✅
- React `CamerasPage`, `CameraCard`, `cameraService` ✅
- Camera URL discovery during printer registration ✅
- `NetworkUrlRewriteService` for Docker/native URL rewriting ✅

**What's missing (the 20% gap):**
1. No `PrinterId` FK on `Camera` — standalone and printer cameras are two separate systems
2. No multi-camera per printer — printer has flat `CameraStreamUrl`/`CameraSnapshotUrl` strings
3. No health monitoring — no polling to detect dead streams
4. Toggle exists but doesn't suppress cameras in printer cards/dashboard

**Competitive validation (Brett):**
- All 5 major competitors manage cameras as independent entities linked to printers
- 7/10 farm operators use multiple cameras per printer
- 9/10 want bandwidth/polling control
- Implementation is ~200 lines C# + ~300 lines React + 1 migration

**Architecture approach (Lambert):**
- Add `PrinterId` FK + `Source` tracking to `Camera` entity
- Data migration: promote printer URL strings into proper Camera rows
- Unified model gives: enable/disable, multi-camera, external cameras, health monitoring
- Estimated effort: 11-16 hours total, phased

### ✅ ITEM 2: Slicer Artifact Uploads → Phase 3E (Implement)

**Team consensus: Must-have, implement after job queue stabilizes.**

- **Lambert's critical finding**: `/api/artifacts` endpoint DOES NOT EXIST. The upload flow has a sender but no receiver.
- **Brett's finding**: Must-have for analytics. All competitors show thumbnails.
- **Dallas's design**: Full architecture — `Artifact` entity, `ArtifactsController`, storage strategy, metadata key conventions.

**Scope**: Entity + migration, controller, `SlicingArtifactKeys` constants, worker upload logic, frontend thumbnails.
**Assign**: Lambert (backend) + Ripley (frontend)
**Complexity**: L (Large)

### ✅ ITEM 3: OpenAPI Migration → CLOSE (Already Done!)

**Lambert's discovery: Already complete!** `Program.cs` already uses `.NET 10 native AddOpenApi()`. `ExampleSchemaFilter.cs` is 100% dead code.
**Action**: Delete dead code file.

### ⚠️ ITEM 4: Tag Support → DEFER (Projects are better)

**Brett strongly recommends SKIP**: "Projects win, free-form tags fail." No competing print farm tool has meaningful tag adoption.
**Action**: Close TODO. Projects feature provides better organization.

### ✅ ITEM 5: OrcaSlicer Types → Phase 3E (Implement)

**Team consensus: Straightforward when slicer integration stabilizes.**
**Scope**: `OrcaSlicerProfile`/`OrcaSlicerSettings` types, `manifest.json`, asset registry parsing, UI provider update.
**Assign**: Lambert
**Complexity**: M (Medium)

## Priority Order

| # | Item | Decision | Phase |
|---|------|----------|-------|
| 1 | OpenAPI Migration | ✅ Already done — dead code deleted | Done ✅ |
| 2 | Tag Support | ⚠️ Defer — projects are better | Done ✅ |
| 3 | Camera Management | ✅ Reopened — platform-level camera management | 1.5 |
| 4 | Slicer Artifacts | ✅ Implement full artifact pipeline | 3E |
| 5 | OrcaSlicer Types | ✅ Implement profile/settings/manifest | 3E |

## Completed Actions ✅

- [x] Delete `ExampleSchemaFilter.cs` (dead code)
- [x] Close camera control TODOs with firmware limitation explanation
- [x] Close tag support TODO with Projects explanation
- [x] Update slicer artifact TODO with Phase 3E reference

## Phase 1.5: Camera Management (Next Sprint)

### Backend (Lambert)
- [ ] Add `PrinterId` FK + `Source` + `CameraType` + `IsHealthy` fields to `Camera` entity
- [ ] EF Core migration for Camera schema changes
- [ ] Data migration: promote Printer.CameraStreamUrl/SnapshotUrl into Camera rows
- [ ] Update `CamerasController` to support printer-linked cameras (CRUD by printer)
- [ ] Camera health monitoring service (periodic snapshot probe, dead stream detection)
- [ ] Update `PrintersService` camera stubs to use Camera entity toggle
- [ ] API endpoint: `GET /api/printers/{id}/cameras` (multi-camera per printer)

### Frontend (Ripley)
- [ ] Multi-camera support in printer detail view
- [ ] Camera enable/disable toggle in printer cards
- [ ] "Add External Camera" flow (IP camera, USB cam on separate host)
- [ ] Camera health indicators (green/yellow/red based on last successful poll)
- [ ] Camera grid view improvements on CamerasPage

### Testing (Kane)
- [ ] Unit tests for Camera entity FK relationships
- [ ] Integration tests for camera CRUD with printer association
- [ ] Health monitoring service tests
- [ ] Frontend component tests for multi-camera UI

## Phase 3E Work Items (Future Sprint)

- [ ] Artifact entity + EF Core migration
- [ ] ArtifactsController (upload/download)
- [ ] SlicingArtifactKeys constants
- [ ] HttpJobPollerService multi-artifact upload
- [ ] OrcaSlicerProfile + OrcaSlicerSettings types
- [ ] Manifest parsing in OrcaSlicerAssetRegistry
- [ ] OrcaSlicerUIProvider type references
- [ ] Frontend thumbnail display

## References

- `.squad/decisions/inbox/dallas-blocked-items-architecture.md`
- `.squad/decisions/inbox/lambert-codebase-analysis.md`
- `.squad/decisions/inbox/brett-competitor-research.md`
