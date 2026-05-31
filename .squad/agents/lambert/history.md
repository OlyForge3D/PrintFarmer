# Lambert History

## Core Context

Lambert is the backend and infrastructure architect. Key retained context:
- Owns multi-database backend changes, background services, EF migrations, and most failure-detection / Obico runtime logic.
- Key backend pattern: singleton workers resolve scoped services via `IServiceScopeFactory`, and configuration-sensitive monitors should reread persisted settings rather than assume in-memory state.
- Prefers behavior-safe adapters: add compatibility for new upstream contracts without forcing migrations for older deployments, then protect the seam with focused tests.
- Important current references: `PrintFailureMonitorService`, `ObicoFailureDetectionService`, `ObicoServerController`, and the focused Obico controller/service test files.
- Per-toolhead slicer estimates: GcodeFile stores FilamentPerExtruderWeightG as a JSON string array, parse with System.Text.Json.JsonSerializer.Deserialize<double[]>.

Early detailed entries were summarized on 2026-03-25 for maintainability. See decisions and orchestration logs for source detail.

### Summarized history
- 2026-03-07 to 2026-03-16: Delivered major backend work across auto-dispatch, analytics, camera platform prep, initial failure detection, and multi-server Obico support.
- 2026-03-25: Normalized PendingReady backend state, clarified warmup/attention boundaries, separated runtime reachability issues from route bugs, and adapted Obico to the upstream GET-first contract.
- 2026-03-26: Implemented failure-detection incident history persistence, enriched frontend alerts with job context, finalized plugin gap analysis, and validated architecture principles.


## Recent

_Last 5 most-recent learnings preserved from full history. Older entries are in `history-archive.md` (archived 2026-05-21 by Scribe)._

- **2026-05-29 — PR #316 Merged (Bishop, SHA 8becf256).** Control-gate pattern applied to `/home`, `/homexy`, `/homez` endpoints. Conflict resolved (test union merge). Next: extend pattern consistency across remaining endpoints (backlog priority).
- **2026-05-12 — go2rtc Sidecar Implementation (PFarm1-lzf0).** Added go2rtc service container to docker-compose, `PrinterStreamRegistry` for transcode URL resolution, `/api/rtc/*` route handlers, and `transcodeStreamUrl` on `UpdatePrinterDto`. Bridges Buddy/Prusa RTSP cameras to frontend WebRTC viewers.
- **2026-05-12 — Event-Driven Camera Snapshots (PFarm1-y3n1).** New `CameraSnapshot` entity + `ICameraSnapshotService` capturing JPEG snapshots on `PrintStarted`, `PrintCompleted`, `PrintFailed`. Storage layout `{snapshotRoot}/{printerId}/{jobId}/{timestamp}_{event}_{cameraId}.jpg`. Optional nullable constructor injection pattern; fire-and-forget try/catch so snapshot failures never block print status updates. Named `HttpClient "CameraSnapshot"` (10s timeout). Use `IEntityTypeConfiguration<T>` (not just DbSet) to get configured indexes/FK behaviors via `ApplyConfigurationsFromAssembly`.
- **2026-05-12 — SpeedMultiplier SignalR propagation (PFarm1-00u1).** `int? SpeedMultiplier` added to `PrusaCompositeStatus`, `PrinterStatusDto`, and frontend `PrinterJobInfo`. Wired through `PrusaLinkClient.GetCompositeStatusAsync()` + `PrusaLinkPollingService` SignalR broadcast. Other backends (Moonraker, OctoPrint, FlashForge, SDCP) leave it null until their pipelines surface speed. Positional records with default-null params remain backward-compatible additions.
- **2026-05-12 — Buddy Camera + RTSP probe (PFarm1-873d / PFarm1-3sbh).** `BuddyCameraHost` field (253 chars, IP/hostname-only) on `Printer`; `PrinterService` upserts/deletes companion `Camera` entities on update. RTSP health probe uses OPTIONS via `rtsp://{streamUrl}` with TCP-port-554 fallback; integrated into `CameraHealthMonitorService` dispatcher. Migrations created for both PostgreSQL and SQL Server.
- **2026-04-05 — OrcaSlicer import endpoint.** `POST /api/slicer/profiles/import/orca` (`farm_admin` policy) parses bundle JSON via `IOrcaBundleParsingService.ParseBundle`, filters by `SelectedPrinters` / `SelectedFilaments` / `SelectedProcesses`, calls `IProfilesService.ImportProfileAsync` per preset (content-hash dedup applies). Per-preset failures collected as warnings; whole-import failures return 500. Closes the missing leg of the `OrcaImportWizard` upload→preview→import flow.

- **2025-11-24 — Error-translation test pattern for plugin backends (PR #318 review).** When testing plugin error translation (firmware rejection → `PrinterBackendBusyException` → controller outcome), test the **full mutation path end-to-end**, not just the helper logic in isolation. Example: mock `StartPrintAsync` to throw on rejection, call the actual mutation, assert exception propagated — don't just test the parsing helper separately. Helper correctness is compile-time verifiable; the seam (backend rejects → exception raised → controller maps) is the critical contract that needs integration-level validation. Applies to all three backends (Moonraker, SDCP, FlashForge) symmetrically.

- **2025-11-24 — Real-transport test pattern for plugin backends (PR #318 fix-up).** Spinning up Kestrel WebSocket for SDCP + TcpListener for FlashForge exercises the full rejected-mutation → status-roundtrip → exception propagation path. Much higher fidelity than mocking the helper layer. Tests `SdcpClientBusyTests` and `FlashForgeClientStartPrintBusyTests` validate the seam (backend rejects → exception raised → controller maps to outcome) end-to-end. Ack=1 + CurrentStatus=[1] → busy; code 9 (starting) → busy; code 0 (idle) → false (SDCP); ~M23 rejection + BUILDING_FROM_SD → busy; BUILDING → busy; READY → false (FlashForge). All 6 behavior-level tests pass; `dotnet format --verify-no-changes` clean.

- **2026-05-28 — Gate pattern reused for home endpoints (#314, PR #316):** The `GatePrinterControlAsync` + `MapControlOutcome` pattern in `PrintersController` is the canonical way to add status-gated control to any printer endpoint. Pattern lives at lines ~2122–2155. The three home handlers (`/home`, `/homexy`, `/homez`) previously returned `bool` from service methods — gate sits in front and short-circuits with 409 before the service call; the `bool` result mapping stays unchanged after the gate.

- **2026-05-28 — Backend busy-error propagation:** Plugin-specific firmware signals (HTTP 409/503 for Moonraker; status round-trip on Ack for SDCP; `~M119` echo for FlashForge) translated into `PrinterBackendBusyException`. Moonraker `SendGcodePrivateAsync` throws on HTTP 409/503; SDCP round-trips status on Ack failure; FlashForge echoes `~M119` check on rejection. All backends map to `BackendBusy` → 502 Bad Gateway. Archived older learnings for this pattern (2025-11-23, 2025-11-24) in history-archive.md.

- **2026-05-28 — Plugin-propagation gap deferred (follow-up #317):** Moonraker, SDCP, and FlashForge plugins do not translate firmware busy responses into `PrinterBackendBusyException`. Controller gate is sufficient as primary defense; race-condition gap tracked as P2 in issue #317.

### Bambuddy Review Pointer — 2026-05-31

bambuddy repo (https://github.com/maziggy/bambuddy) was reviewed by Brett. Two adoption candidates identified: gcode-preview (toolpath rendering) and client-side 3MF parsing. See decisions.md entries "Consider G-code toolpath preview parity from bambuddy" and "Consider a richer slice progress contract" for details.
