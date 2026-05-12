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

- Printer entity already has `CameraStreamUrl`/`CameraSnapshotUrl` fields + `ICollection<Camera> Cameras` nav property — two parallel camera tracks exist
- `EditPrinterModal.tsx` already has a Camera Configuration section with Auto-Detect button (lines ~1040-1070)
- `CameraService` has `CreateForPrinterAsync(printerId, dto)` — can be reused for Buddy camera creation
- Camera health monitoring runs on 5-minute intervals via `CameraHealthMonitorService` — RTSP probe (PFarm1-3sbh) will extend this
- `UpdatePrinterDto` and `CreatePrinterFromDiscoveryDto` already carry `CameraStreamUrl`/`CameraSnapshotUrl` — `BuddyCameraHost` follows same pattern
- Key file paths: `src/infra/Domain/Printer.cs`, `src/infra/Domain/Camera.cs`, `src/infra/Domain/Enums/CameraEnums.cs`, `src/infra/Services/Cameras/CameraService.cs`, `src/api/Controllers/CamerasController.cs`, `src/Web/ReactApp/src/features/printers/components/EditPrinterModal.tsx`

## 2026-05-12 Session Wrap-Up

**Outcome:** PFarm1-873d architecture decision merged into decisions.md  
**Scope:** Buddy Camera auto-discovery field architecture, schema, API contract, implementation roadmap  
**Status:** Ready for Lambert's implementation (completed ✅)

### Key Decisions Documented

- Buddy camera as Printer entity field (not standalone) for UX coherence
- Auto-upsert Camera entity on printer save/update/delete lifecycle
- CameraSource.BuddyCamera enum distinguishes from backend-discovered cameras
- Frontend conditional visibility (PrusaLink only)
- URL auto-derivation: rtsp://{buddyCameraHost}:554/live/

### Downstream Dependencies Identified

- PFarm1-3sbh (RTSP health probe) ✅ Implemented by Lambert
- PFarm1-y3n1 (Event snapshots) — Feature-ready when Lambert completes
- PFarm1-lzf0 (go2rtc sidecar) — Snapshot URL integration available post-go2rtc
