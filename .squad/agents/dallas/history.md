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

## Session: go2rtc Deployment Integration Analysis (2026-05-12)

**Role:** Lead/Architect  
**Status:** Analysis complete, decision written

### Key Findings

- `docker-compose.go2rtc.yml` template exists but is **not wired** into either `deploy-docker.sh` or `compose-generator.sh`
- Neither script references go2rtc — the template is inert without code changes
- Compose assembly uses `INCLUDE_*` booleans + `merge_addon_services()` for opt-in services (Spoolman, Obico ML pattern)
- Recommended approach: `--include-go2rtc` opt-in flag in both scripts, matching existing addon pattern
- ~30 min implementation effort; no architectural changes needed

### Decision Record

- **File:** `.squad/decisions/inbox/dallas-go2rtc-deployment.md`

### 2025-05-20: Mobile API Drift + Basic Printer Controls v1 — 16 GitHub issues filed
Created in `OlyForge3D/PrintFarmer`. Task# → GH#:

| # | GH | Title | Assignee | Phase | Depends on |
|---|----|----|----|----|----|
| 1 | #274 | [iOS] Gate Maintenance toggle on farm_admin role | squad:hudson | Drift cleanup | — |
| 2 | #275 | [iOS] Remove redundant PrinterService.stop() alias | squad:gorman | Drift cleanup | — |
| 3 | #276 | [iOS] Surface homedAxes in PrinterStatusDetail | squad:hudson | Drift cleanup | — |
| 4 | #277 | [iOS] Add unit test pinning Printer.progress 0–100 contract | squad:gorman | Drift cleanup | — |
| 5 | #278 | [iOS] Remove dead int-branch decoders for string-only enums | squad:gorman | Drift cleanup | — |
| 6 | #279 | [API] Spike: confirm /temps and /move enforce server-side guards while printing | squad:ripley | Spike | — |
| 7 | #280 | [iOS] Add PrinterBackendCapabilities model + getBackendCapabilities() | squad:gorman | Foundation | — |
| 8 | #281 | [iOS] Extend PrinterService with setTemperatures, home, homeXY, homeZ, move | squad:gorman | Foundation | — |
| 9 | #282 | [iOS] Create PrinterControlsViewModel (capability cache, command queue) | squad:gorman | Foundation | #280, #281 |
| 10 | #283 | [UX] Design printer-controls section (preheat, home, jog) | squad:newt | Design | — |
| 11 | #284 | [iOS] Build PrinterControlsSection — preheat subgroup | squad:hudson | UI build | #279, #280, #282, #283 |
| 12 | #285 | [iOS] Build PrinterControlsSection — home subgroup | squad:hudson | UI build | #280, #282, #283 |
| 13 | #286 | [iOS] Build PrinterControlsSection — jog/move subgroup | squad:hudson | UI build | #280, #282, #283 |
| 14 | #287 | [iOS] Integrate PrinterControlsSection into PrinterDetailView | squad:hudson | Integration | #284, #285, #286 |
| 15 | #288 | [iOS] Accessibility pass on controls section | squad:hudson | Polish | #287 |
| 16 | #289 | [iOS] Snapshot tests for PrinterControlsSection (Moonraker/FlashForge/SDCP) | squad:hudson | Testing | #279, #287 |

Locked v1 decisions captured in `.squad/decisions/inbox/dallas-mobile-controls-v1-locked.md` (fixed presets PLA/PETG/ABS/CoolDown, fixed feedrates XY=3000 / Z=600, step picker 0.1/1/10/100mm, trust `supportsTemperatureControl` capability, no optimistic UI — wait for `printerupdated` SignalR event, cooldown sets both to 0, match backend auth, hide section when `!isOnline`, block controls while printing/paused, human squad only — no copilot routing).

- 2026-05-21: Ralph Round 1 (Phase 0) completed — see `.squad/log/2026-05-21T09-00-00Z-ralph-round-1-phase-0.md`.
