# Dallas History

## Core Context

Dallas is the project lead & product architect. Key contributions:
- Feature prioritization & architecture oversight
- Location hierarchy system design (phase 1 approved)
- Auto-dispatch phase 1 & 2 architecture
- Competitive analysis & market differentiation
- Team coordination & decision governance
- Failure detection & UI polish sessions (2026-03-25)
- Auto-dispatch naming cleanup & consistency (2026-03-25)

Early entries (pre-2026-03-25) summarized for maintainability. See decisions-archive.md for historical context.

---

## Session History Summary (2026-03-25 — 2026-05-12)

**Sessions Archived & Summarized:**
- Failure Detection Badge Placement Review (2026-03-25) — Recommendation to remove camera overlay; keep header badge only
- 11 prior decision reviews and architectural analyses (2026-03-16 — 2026-03-25)

**Themes Across Sessions:**
- UX clarity and visual consistency enforcement
- Backend-agnostic feature scoping (UI-first architecture)
- Competitive analysis and market differentiation
- Team decision governance and conflict resolution

See `.squad/decisions-archive.md` for detailed decision records from archived sessions.

---

## Session: Prusa-StatusBar Camera Integration Research (2026-05-12)

**Role:** Lead/Architect  
**Status:** Research complete; proposal approved for decision registry

### Work Completed
- Analyzed Prusa-Buddy printer camera capabilities and RTSP streaming protocol
- Evaluated integration approaches (direct RTSP URL vs. go2rtc sidecar bridge)
- Assessed Tier 1/2/3 feature breakdown with implementation complexity estimates
- Confirmed no upstream firmware changes needed from Prusa
- Documented auto-discovery and event snapshot strategies

### Key Findings
- Prusa-Buddy printers expose RTSP URLs natively (hardware-built capability)
- PrintFarmer can integrate via: (a) direct RTSP URL config, or (b) go2rtc sidecar for WebRTC fallback
- Event snapshots possible via background timelapse capture from RTSP stream
- Auto-discovery can use Prusa API or manual discovery per printer

### Decision Record
- **File:** `.squad/decisions.md` → Prusa-StatusBar Camera Integration entry
- **Tiers:** MVP (RTSP viewer), Core (auto-bridge), Polish (admin mgmt)
- **Next:** Frontend design (Ripley), backend scoping (Lambert), DevOps planning

### Session Artifacts
- Orchestration Log: `.squad/orchestration-log/2026-05-12T18-18-17Z-dallas.md`
- Session Log: `.squad/log/2026-05-12T18-18-17Z-prusa-camera-research.md`

---

## Session: PFarm1-873d Buddy Camera Architecture (2026-05-12)

**Role:** Lead/Architect
**Status:** Architecture decision written, pending team review

### Work Completed
- Explored full camera + printer infrastructure (entities, DTOs, services, controllers, frontend)
- Designed `BuddyCameraHost` field placement on Printer entity with auto-derived Camera entity lifecycle
- Added new `CameraSource.BuddyCamera` enum value to distinguish from PrusaLink-discovered cameras
- Defined API contract changes (UpdatePrinterDto, CreatePrinterFromDiscoveryDto, PrinterDto response)
- Scoped frontend integration points (EditPrinterModal, conditional visibility for PrusaLink printers)
- Estimated ~9h implementation effort

### Key Architecture Decisions
- **BuddyCameraHost on Printer entity** — user provides IP/hostname, system derives RTSP URL and upserts Camera entity
- **New CameraSource.BuddyCamera** — separate from PrusaLink (different discovery source, different health probe path)
- **Camera upsert/delete in PrinterService** — setting host creates camera, clearing host deletes it
- **SnapshotUrl stays null** until go2rtc sidecar (PFarm1-lzf0) is deployed
- **Conditional UI** — Buddy Camera field shown only for PrusaLink backend printers

### Decision Record
- **File:** `.squad/decisions/inbox/dallas-buddy-camera-architecture.md`

## Learnings


### 2026-05-31 — External-reference-app adoption Phase 2: Work breakdown synthesis

Produced phased rollout plan from Brett's 3 research artifacts + Dallas Phase 1 surface map. Key trade-offs:

- **gcode-preview v2 over v3**: v3 alpha has API churn; v2.18.x is stable and matches external-reference-app's proven pattern. Migration deferred until v3 stabilizes.
- **No client-side 3MF parsing yet**: external-reference-app's main-thread JSZip approach is a perf risk. PrintFarmer already extracts 3MF metadata server-side in `Model3DFileService`. Client-side parsing deferred pending Web Worker architecture.
- **Quick Slice as additive entry point, not replacement**: external-reference-app's preset-first modal hides all params. PrintFarmer's `NewSliceJobPage` is correct for power users. Quick Slice is a simpler alternative, not a replacement.
- **Notifications phased**: 8 providers is too much for one PR. Ship webhook+Discord+Telegram first (covers 80% of home-lab users), then extend.
- **Layer timelapse blocked on go2rtc**: Requires camera infrastructure (PFarm1-lzf0) that hasn't landed yet. Explicitly deferred.
- **PrintFarmer differentiator preserved**: external-reference-app rejects raw .gcode upload (Bambu constraint). PrintFarmer accepts it — this is a competitive advantage for Moonraker/PrusaLink/FlashForge users. No changes to gcode upload flow.

Decision inbox file: `.squad/decisions/inbox/dallas-external-reference-app-adoption-plan.md`

### 2026-05-31 — Slice + 3D model integration surface map

Phase 1 survey for upcoming G-code/3MF viewer integration. Key surfaces:
- Slice submission starts in `src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx`, builds `SubmitSliceJobRequest`, then calls `src/Web/ReactApp/src/services/sliceJobService.ts` -> `POST /api/slice`.
- Slice job monitoring is `src/Web/ReactApp/src/features/slicer/pages/SliceJobsPage.tsx` + `src/Web/ReactApp/src/features/slicer/components/SliceJobsPanel.tsx`, with cache/live updates from `src/Web/ReactApp/src/features/slicer/hooks/useSliceJobsRealtime.ts` and job-specific inline progress from `src/Web/ReactApp/src/features/slicer/hooks/useSliceJobProgress.ts`.
- Slicer SignalR client is `src/Web/ReactApp/src/services/slicerHubService.ts`, connected to `/hubs/slicers`; backend maps hubs in `src/slicer/Farm.Slicer.Module.Api/SlicerApiExtensions.cs` and broadcasts job events from `src/slicer/Farm.Slicer.Module.Api/Services/SliceJobEventService.cs`.
- Backend slice lifecycle is `src/slicer/Farm.Slicer.Module.Api/Controllers/Slicing/SliceJobController.cs`; contracts are `src/slicer/Farm.Slicer.Module/Contracts/SliceJobDtos.cs`; artifacts are stored via `src/slicer/Farm.Slicer.Module.Api/Controllers/ArtifactsController.cs` and `src/slicer/Farm.Slicer.Module.Api/Services/ArtifactsService.cs`.
- G-code results are file-system artifacts rooted by `SlicerArtifactStorageSettings.RootPath` (`src/slicer/Farm.Slicer.Module/Services/Configuration/SlicerArtifactStorageSettings.cs`), exposed as `/api/artifacts/{id}/download` and `/api/artifacts/job/{jobId}`.
- 3D model library lives in `src/Web/ReactApp/src/features/models3d/pages/ModelsPage.tsx`, with grid/list cards in `ModelGridView.tsx`/`ModelListView.tsx`; upload uses `apiClient.uploadModel3dFile`; preview uses lazy `ModelViewer3D` or `GCodeViewer3D`.
- A separate slicer-bed workspace already exists at `src/Web/ReactApp/src/features/slicer/components/viewer/SlicerWorkspace.tsx` + `SlicerBedVisualization.tsx` for multi-model arrangement before submit.
- Backend 3D model routes live in `src/slicer/Farm.Slicer.Module.Api/Controllers/Model3DFilesController.cs`; storage is handled by `src/slicer/Farm.Slicer.Module/Services/Model3DFileService.cs`, with URLs built in `src/infra/Services/FileManagement/StoredFileOperationsService.cs`.
- Microservice route ownership is documented/enforced by `deploy/nginx/nginx-proxy-split.conf`: `/api/slicers`, `/api/artifacts`, `/api/3d-models`, `/api/slice`, `/api/slicer`, and `/hubs/slicers` go to `slicer-host:5246`; normal `deploy/nginx/nginx-proxy.conf` sends generic `/api` and `/hubs` to main API.

### 2026-05-28 — #290 Status-Gate Guards

- **Layer choice:** 409 gate lives in the **controller** (`GatePrinterControlAsync`), not in `PrintersService`. Rationale: the cache read is a cross-cutting HTTP concern; the service shouldn't know about HTTP status codes. Service layer handles plugin-level busy signals via `PrinterBackendBusyException` → `PrinterControlOutcome.BackendBusy` → 502.
- **Gate states:** `PrinterControlGate.BusyStates` = Printing, Pausing, Paused, Resuming, Cancelling, Heating — aligned with `PrintFailureMonitorService` active-print set (PR #310 keeps them in sync).
- **502 vs 409:** 409 = "our cache says you're printing, stop client-side." 502 = "firmware said no after we tried." Two distinct failure modes, two distinct codes.
- **Plugin 409 propagation (OctoPrint + PrusaLink):** Both `OctoPrintClient` and `PrusaLinkApiClient` detect `HttpStatusCode.Conflict` in the temp/jog HTTP responses and throw `PrinterBackendBusyException`. `PrintersService` catches it → `BackendBusy`. This is scoped to SetTemp/Jog only — other capabilities unchanged.
- **Key file paths:**
  - `src/infra/Services/Printers/PrinterControlGate.cs`
  - `src/infra/Services/Printers/PrinterControlOutcome.cs`
  - `src/infra/Services/Printers/PrinterBackendBusyException.cs`
  - `src/api/Controllers/PrintersController.cs` (lines ~2044–2155, `GatePrinterControlAsync` + `MapControlOutcome`)
  - `src/tests/Farm.Web.Api.Tests/Controllers/PrintersControllerControlGuardsTests.cs`
  - `src/backends/Farm.Backend.Plugin.OctoPrint/OctoPrintClient.cs` (409 throws in SetBed/SetHotend/Jog)
  - `src/backends/Farm.Backend.Plugin.PrusaLink/PrusaLinkApiClient.cs` (409 throws in SetToolTemp/SetBedTemp/JogPrintHead)
- **Test pattern for gated endpoints:** mock `IPrintersService.FindByIdAsync` + `IPrinterStatusCacheReader.GetStatus`, assert no downstream service call when gate fires.

- Printer entity already has `CameraStreamUrl`/`CameraSnapshotUrl` fields + `ICollection<Camera> Cameras` nav property — two parallel camera tracks exist
- `EditPrinterModal.tsx` already has a Camera Configuration section with Auto-Detect button (lines ~1040-1070)
- `CameraService` has `CreateForPrinterAsync(printerId, dto)` — can be reused for Buddy camera creation
- Camera health monitoring runs on 5-minute intervals via `CameraHealthMonitorService` — RTSP probe (PFarm1-3sbh) will extend this
- `UpdatePrinterDto` and `CreatePrinterFromDiscoveryDto` already carry `CameraStreamUrl`/`CameraSnapshotUrl` — `BuddyCameraHost` follows same pattern
- Key file paths: `src/infra/Domain/Printer.cs`, `src/infra/Domain/Camera.cs`, `src/infra/Domain/Enums/CameraEnums.cs`, `src/infra/Services/Cameras/CameraService.cs`, `src/api/Controllers/CamerasController.cs`, `src/Web/ReactApp/src/features/printers/components/EditPrinterModal.tsx`

