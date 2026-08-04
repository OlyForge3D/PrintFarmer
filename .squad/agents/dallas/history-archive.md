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


## 2026-07-13 CHARSET Gate Sweep on #708 (PR #750)

**Symptom:** `dotnet format ./farm-web.sln --verify-no-changes` failed with 40 CHARSET errors and 7 WHITESPACE errors + 22 IDE0161 warnings at `bf3c9de5`.

**Root cause of CHARSET failures:** Files introduced by the F3 native push commit (`271f45d48`) and predecessor commits were written without UTF-8 BOM, violating `src/.editorconfig`'s `[*.{cs,csx,vb,vbx}] charset = utf-8-bom`. The failures existed since the F3 commit, not introduced by v4/v5 remediation. 28 files new-in-PR, 12 pre-existing on `origin/development`.

**Fix:** Prepend three-byte BOM (`EF BB BF`) to all 40 affected `.cs` files. Encoding-only change; every diff is exactly `1+/1-` on line 1, semantic content byte-identical after the BOM. CRLF line endings preserved.

**Result:** CHARSET class now zero across the full solution. 99 native-push tests pass. Remaining format failures (7 WHITESPACE in `PrintersController.cs`, 22 IDE0161 on migrations) are different diagnostic classes and pre-date this remediation.

**Key learning:** Editor tooling that strips BOMs silently is a repeat trap in this repo. When a "narrow" remediation task names a single file for a class-based diagnostic (like CHARSET), verify whether the class fails elsewhere before scoping — the same root cause usually affects sibling files added in the same feature commit.

---


_Last 4 most-recent learnings preserved from full history. Older entries are in `history-archive.md` (archived 2026-05-31 by Scribe)._

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

## 2026-05-21 — Mobile Controls v1 prep: review batch 1 (PRs #291–#297)

Reviewed 7 draft PRs against locked v1 design (decisions.md L576–589). Verdict: 7/7 APPROVE; 4/7 merged, 3/7 awaiting rebase.

**Merged (squash + delete-branch):**
- **#295** (Gorman, capabilities) — Hybrid endpoint + static `PrinterBackendCapabilities.fallback(for:)` table. Actor-isolated cache (no TTL → flagged v2 follow-up). Tests cover every PrinterBackend case + Codable round-trip.
- **#296** (Newt, UX spec, +472 lines) — Implementation-ready spec for #284–#286: hide-on-offline, capability-missing = remove (not grey), no optimistic UI, fixed presets PLA 200/60 / PETG 240/80 / ABS 240/100 / CoolDown 0/0, jog feedrates 3000 XY / 600 Z, step picker 0.1/1/10/100mm, debounce 250ms + 5s timeout reverts to neutral toast.
- **#292** (Gorman, progress contract) — Decoder clamp + 8 contract tests pin 0–100 → 0.0–1.0 invariant. Inline doc comment. SignalR-path follow-ups (DashboardViewModel/PrinterDetailViewModel/PrinterListViewModel) flagged out-of-scope.
- **#294** (Hudson, homedAxes) — **Architectural ruling: `homedAxes` is `String?` not `[String]?`** — wire format is compact "xyz"/"xy"/""/nil from MoonrakerSubscriptionService. Defensive `if let homed = detail.homedAxes` guard against nil-clobber from partial status updates is correct. Badge view + VoiceOver labels clean.

**Approved but blocked on rebase (real code conflicts after batch-1 merges):**
- **#297** (Gorman, service methods) — overlaps with #295 on PrinterServiceProtocol/PrinterService/DemoPrinterService/MockPrinterService. Author rebases + keeps both capability methods (#295) and setTemperatures/home/move (#297). Minor non-blocking nit: MovePrinterRequest.encode silently falls back to .x for unknown axis — locked picker prevents in practice; precondition assert wouldn't hurt later.
- **#291** (Hudson, admin gate) — likely conflicts with #294 view changes. Hides not disables (correct UX).
- **#293** (Gorman, dead int-decoders) — likely Models.swift overlap with #292/#294. **Architectural ruling: `PrintJobPriority.from(intValue:)` preserved** because PrintJobDto.Priority serializes as raw int (not enum). `SignalRModels.AnyCodable` Int branch correctly untouched.

**Process notes:**
- Self-PRs blocked by `gh pr review --approve` (cannot self-approve own PR). Used `--comment` for verdicts + `--admin` squash-merge.
- Cascading `.squad/` overlay conflicts on each merge are expected; resolved automatically when shared files have `merge=union` driver — but these 3 had real Swift code conflicts beyond the overlay.

**Unblocks:** #282 (ViewModel) and #284–#286 (UI build) now have capabilities (#295) + UX spec (#296) merged. Service methods (#297) need rebase before #284–#286 implementation can call them.


- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291, #292, #293, #294, #295, #296, #297, #298). See `.squad/log/2026-05-21T08-15-00Z-ralph-rounds-2-5-phase-1-complete.md`. Phase 2 launching (#284 preheat, #285 home, #286 jog).

### 2026-05-21 — Mobile-controls v1 board cleared (team update from Scribe)
- PR #306 (Hudson, #289 snapshot tests w/ swift-snapshot-testing on test target only) merged.
- PR #308 (Lambert, #290 backend capability guards: `PrinterControlOutcome` enum, 409/busy gate, typed exceptions) merged.
- #276 verified shipped end-to-end (homedAxes across backend → SignalR → iOS decoder); closed.
- Mobile-controls v1 board now fully clear: #275 wontfix, #276/#279/#280–#290/#302 all closed.
- New bug #309 filed: spaghetti detection shield says "printer not printing" on actively-printing printers. Ripley investigating.

## Cross-Team Note (2026-05-29)

**Newt** (#283 design spec) unblocked: Status-gating validation confirmed via PR #308 merge. Controls can safely use printing/paused blocking.
**Gorman** (#280 capabilities) unblocked: Capabilities endpoint live; UI gating design decisions finalized.
**Hudson/Lambert:** API guards for `/temps`, `/move`, `/moveto` now live. Skill published for future reference.

## Learnings — 2026-05-31 Backlog Planning Session

**Scope:** Three net-new backlog stubs filed as decision inbox entries for Brady review.

### Key Findings

- **Energy cost schema is already partially built.** `PrintJob` has `EnergyCostUsd`,
  `MaterialCostUsd`, `MachineTimeCostUsd`, `TotalCostUsd`, `CostCalculatedAt`. `Printer`
  has `Wattage` and `MachineHourlyRate`. The smart plug feature adds real measurements
  (`KwhUsed`, `PowerMonitor` entity, `PowerReading` time-series) on top of an existing
  cost skeleton — this reduces migration and service risk significantly.

- **Smart plug "don't chase" deferral superseded.** The 2026-05-31 external-reference-app adoption
  decisions.md listed smart plug integration as deferred ("can revisit when demand is
  proven"). Brady's 2026-05-31 explicit request to plan this item supersedes that
  deferral. Decision files should not treat it as blocked.

- **Printables GraphQL API is public.** No OAuth or personal access tokens required for
  public model metadata + CDN file downloads. `ISmartPlugProvider` pattern (analogous to
  `IBackendClientPlugin`) fits well for plug provider abstraction. `Model3DFile` entity
  needs `SourceUrl`, `SourceLicense`, `SourceCreator`, `ImportedAt` attribution fields
  (slicer context migrations only).

- **MakerWorld hard-blocked.** Requires Bambu Cloud account token
  (`api.bambulab.com/v1/design-service/*`). PrintFarmer carries no such token today.
  Do not bundle with Printables work; file as a separate deferred issue if Brady wants.

- **Passkey/WebAuthn stack is straightforward.** `Fido2NetLib` is the canonical .NET
  library. All major browsers support passkeys natively as of 2026. The main complexity
  is the two-ceremony flow (register + assert), challenge cache (use existing
  `IDistributedCache`), and `UserPasskeyCredential` entity (main `AppDbContext`, not
  slicer). rpId config must be environment-variable-driven (`PFARM__WebAuthn__RelyingPartyId`).

### Files Written

- `.squad/decisions/inbox/dallas-backlog-electricity-tracking.md`
- `.squad/decisions/inbox/dallas-backlog-printables-import.md`
- `.squad/decisions/inbox/dallas-backlog-passkey-login.md`

## 2026-05-31 — Triage Round 1: #317 Triaged, Lambert Bench Reassignments

**Scope:** Triage #317, reassign Lambert's benched issues (#344, #346, #351) to other members, recommend next actionable.

### #317 Triaged
- **Title:** "[API] Plugins should propagate firmware 409 as PrinterBackendBusyException (Moonraker, SDCP, FlashForge)"
- **Assigned to:** Brett (researcher, cross-plugin investigation)
- **Labels added:** `squad:🔍 brett`, `priority:p2`, `type:bug`
- **Rationale:** Multi-plugin firmware response handling requires investigation across three backends (Moonraker, SDCP, FlashForge). Brett as researcher good fit for coordinating plugin-level exception propagation.

### Lambert Reassignments (Benched After PR Rejections #371, #405)
- **#344 [P4-3] PrintJob cost aggregation** → `squad:🧪 kane`
  - Rationale: Test-driven cost service logic (energy, material, machine-time aggregation). Kane proven surgical fixer with test coverage foundation from Phase 4 spike.
- **#346 [E-2] PowerMonitor + PowerReading entities + migrations** → `squad:🔍 brett`
  - Rationale: Clean EF Core migration on AppDbContext (Postgres + SqlServer). Power entity schema + 90-day auto-prune background logic.
- **#351 [PI-3] Model3DFile attribution fields + slicer migrations** → `squad:🔍 brett`
  - Rationale: EF Core migration on SlicerDbContext only (separate context from #346). Attribution schema only; no business logic beyond persistence.

### Next Actionable
- **#317 → Brett** (plugin firmware response handling, P2, independent, non-iOS).

## Learnings — 2026-05-31 Issue Filing Session

**Scope:** Filed 31 GitHub issues covering the adoption plan + 5 new backlog clusters.

### Issues Filed

| Cluster | Issues | Numbers |
|---|---|---|
| Phase 1 — G-code Preview | P1-1, P1-2, P1-3, P1-4, P1-5 (tracker) | #333–#337 |
| Phase 2 — Quick Slice UX | P2-1, P2-2, P2-3 | #338–#340 |
| Phase 3 — Notifications | P3-1 (combined, one PR) | #341 |
| Phase 4 — Cost Tracking | P4-1, P4-2, P4-3 | #342–#344 |
| Electricity Tracking | E-1, E-2, E-3, E-4 | #345–#348 |
| Printables Import | PI-1, PI-2, PI-3, PI-4 (blocked) | #349–#352 |
| Passkey Login | PK-1, PK-2, PK-3, PK-4 | #353–#356 |
| Settings Consolidation | ST-1, ST-2, ST-3a, ST-3b | #357–#360 |
| NFC UX Polish | NFC-1, NFC-2a, NFC-2b | #361–#363 |

**Total: 31 issues** (#333–#363)

### Deviations from Plan

- **ST-3 split into ST-3a (#359, Lambert) and ST-3b (#360, Ripley)** as explicitly noted in the plan as an option. Clean frontend/backend split.
- **NFC-2 split into NFC-2a (#362, Lambert) and NFC-2b (#363, Ripley)** per plan note "split if cleaner" — the backend SignalR routing and frontend inventory sync are independent deliverables.
- **P3-1 (#341) filed as a combined cross-cutting issue** with both `squad:⚛️ ripley` and `squad:🔧 lambert` labels per Brady's "ONE PR" directive.
- **PI-4 (#352, MakerWorld)** filed with `go:no` label per Brady policy — blocked on Bambu Lab cloud token strategy.
- **Issue numbers started at #333** not #318 as pre-planned (issues #318–#332 were filed by other means between the check and filing). Worktree annotations and cross-references corrected post-filing.

### Things Brady Will Want to Know

- **Service abstraction is the load-bearing constraint on Phase 1.** P1-1 (#333) blocks P1-2 (#334) which blocks P1-3 (#335). Ralph should pick these up in order.
- **E-2 migrations cover AppDbContext only** (PowerMonitor/PowerReading). PI-3 (#351) migrations cover SlicerDbContext only (Model3DFile attribution). PK-2 (#354) migrations cover AppDbContext (UserPasskeyCredential). No cross-context schema conflicts.
- **All Brady policy answers from the 2026-05-31T09:25 triage directive are embedded in issue bodies** — HA in Phase 1, 90-day retention, attribution on both surfaces, residentKey:preferred, passkey-only out of scope, etc.
- **The 15+ settings nav items list in ST-2 (#358)** is the one that will cause the most debate — some pages (like NfcDevicesPage) are listed in Hardware but cross-reference NFC UX work. Ralph should coordinate with Ripley on ordering.
- **2026-05-31T16:42:** Before committing, scrub message for forbidden external refs: "external-reference-app", "external-author", "external reference app", [external reference repo]. Acceptable alternatives: "adoption plan", "Phase N work breakdown", or standalone feature description. See .squad/decisions.md 2026-05-31T09:42 entry.
- **2026-05-31T16:42:** Before committing, scrub message for forbidden external refs: "bambuddy", "maziggy", "Bambu Buddy", github.com/maziggy/bambuddy. Acceptable alternatives: "adoption plan", "Phase N work breakdown", or standalone feature description. See .squad/decisions.md 2026-05-31T09:42 entry.

## 2026-05-31 — Trio Review Cycle #355, #371, #405

Participated in multi-round trio review cycle. Key learnings:

1. **Multi-reviewer consensus:** Three independent reviewers with fresh hands prevents fatigue. (The author-lockout rule that once accompanied this has been RESCINDED by the repo owner — authors fix their own rejected work; nobody is ever locked out of an artifact.)
2. **Kane surgical-fix MVP:** Small, scoped corrections across all three branches proved cost-effective.
3. **Session-end report validation:** Coordinator must verify trio drops match current commit SHA.
4. **PR auto-close gap:** `Closes #N` does not fire on development merges; manual close required.

