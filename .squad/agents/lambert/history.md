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

- **2026-05-12 — go2rtc Sidecar Implementation (PFarm1-lzf0).** Added go2rtc service container to docker-compose, `PrinterStreamRegistry` for transcode URL resolution, `/api/rtc/*` route handlers, and `transcodeStreamUrl` on `UpdatePrinterDto`. Bridges Buddy/Prusa RTSP cameras to frontend WebRTC viewers.
- **2026-05-12 — Event-Driven Camera Snapshots (PFarm1-y3n1).** New `CameraSnapshot` entity + `ICameraSnapshotService` capturing JPEG snapshots on `PrintStarted`, `PrintCompleted`, `PrintFailed`. Storage layout `{snapshotRoot}/{printerId}/{jobId}/{timestamp}_{event}_{cameraId}.jpg`. Optional nullable constructor injection pattern; fire-and-forget try/catch so snapshot failures never block print status updates. Named `HttpClient "CameraSnapshot"` (10s timeout). Use `IEntityTypeConfiguration<T>` (not just DbSet) to get configured indexes/FK behaviors via `ApplyConfigurationsFromAssembly`.
- **2026-05-12 — SpeedMultiplier SignalR propagation (PFarm1-00u1).** `int? SpeedMultiplier` added to `PrusaCompositeStatus`, `PrinterStatusDto`, and frontend `PrinterJobInfo`. Wired through `PrusaLinkClient.GetCompositeStatusAsync()` + `PrusaLinkPollingService` SignalR broadcast. Other backends (Moonraker, OctoPrint, FlashForge, SDCP) leave it null until their pipelines surface speed. Positional records with default-null params remain backward-compatible additions.
- **2026-05-12 — Buddy Camera + RTSP probe (PFarm1-873d / PFarm1-3sbh).** `BuddyCameraHost` field (253 chars, IP/hostname-only) on `Printer`; `PrinterService` upserts/deletes companion `Camera` entities on update. RTSP health probe uses OPTIONS via `rtsp://{streamUrl}` with TCP-port-554 fallback; integrated into `CameraHealthMonitorService` dispatcher. Migrations created for both PostgreSQL and SQL Server.
- **2026-04-05 — OrcaSlicer import endpoint.** `POST /api/slicer/profiles/import/orca` (`farm_admin` policy) parses bundle JSON via `IOrcaBundleParsingService.ParseBundle`, filters by `SelectedPrinters` / `SelectedFilaments` / `SelectedProcesses`, calls `IProfilesService.ImportProfileAsync` per preset (content-hash dedup applies). Per-preset failures collected as warnings; whole-import failures return 500. Closes the missing leg of the `OrcaImportWizard` upload→preview→import flow.

- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291, #292, #293, #294, #295, #296, #297, #298). See `.squad/log/2026-05-21T08-15-00Z-ralph-rounds-2-5-phase-1-complete.md`.

## Learnings

- **2025-11-22 — PrintersService MMU gate semantics (issue #302, PR #303).** `mmuGateCount` parameter on `CreateMmuVirtualToolheads` / `SyncMmuVirtualToolheads` / `SyncMmuToolheadsOnEntity` means **total number of AMS gates**, NOT a strict upper-bound index. Loop must be `for (int i = 1; i <= mmuGateCount; i++)` to produce N gates at indices 1..N (T0 reserved for Physical hotend). Companion helpers `SetToolheadSpoolAsync` / `ClearToolheadSpoolAsync` use `Math.Max(4, toolheadIndex)` (no `+1`) — index N maps directly to a count-of-N. Bambu defaults to `mmuGateCount = 4`. Existing data already seeded under the old `<` bound is **not** auto-repaired — backfill needs a separate hosted service or migration.

- **2026-05-21 — Issue #302 closed.** Ripley's frontend dedup (PR #305) merged on top of my backend gate-count fix (PR #303). Backend + frontend both shipped; AMS slot rendering is correct end-to-end now.
