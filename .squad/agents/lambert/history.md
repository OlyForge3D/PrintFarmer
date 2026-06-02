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

### External-reference-app Review Pointer — 2026-05-31

external-reference-app repo ([external reference repo]) was reviewed by Brett. Two adoption candidates identified: gcode-preview (toolpath rendering) and client-side 3MF parsing. See decisions.md entries "Consider G-code toolpath preview parity from external-reference-app" and "Consider a richer slice progress contract" for details.

## Team Assignment: External-reference-app Adoption Plan (Scribe Merge, 2026-05-31)

**Incoming Work:** Notification system backend (Phase 3, ~4 work items).

**Context from Research:**
- external-reference-app implements 8-provider notification system: email, Telegram, Discord, generic webhook, ntfy, Pushover, CallMeBot/WhatsApp, Home Assistant
- IProvider pattern identified: `backend/app/schemas/notification.py` ProviderType enum + `backend/app/services/notification_service.py` dispatch logic
- PrintFarmer phased rollout: webhook + Discord + Telegram first; remaining providers in follow-up PRs
- Print farm users demand notifications on their preferred channel (often Telegram/Discord, not email)

**Phase 3 Deliverables (scheduled, not yet assigned to sprint):**
1. Create `INotificationProvider` interface (webhook, Discord, Telegram implementations)
2. Add `NotificationPreferences` entity + EF migration
3. Implement `NotificationService` dispatcher
4. Integrate with existing print lifecycle (completion, failure, queue empty events)

**Linked Decisions:** decisions.md entries "External-reference-app Feature Adoption" and "External-reference-app Feature Sweep — Top Adoption Candidates"


---

### External-reference-app Adoption Finalization — 2026-05-31

**Brady Confirmation:** Notification providers (webhook + Discord + Telegram) ship as ONE PR (Phase 3 ready for scheduling).

**Spoolman Cost Source Confirmed:** Filament cost priority: Spoolman price first, per-material fallback second.

**Backend Stubs Incoming (Backlog Priority):**

1. **Electricity Cost Tracking (Smart Plugs) — ~5 days backend work**
   - New entity: `PowerMonitor` (config per printer)
   - New time-series table: `PowerReading` (watts + timestamp)
   - New providers: `ISmartPlugProvider` (Kasa, Tasmota, Shelly)
   - Hosted service: `PowerMonitorPollingService` (per-printer loop, 10 s intervals)
   - Job completion trigger: `IPowerAggregationService` (kWh aggregation, cost calculation)
   - Add `KwhUsed` to `PrintJob` + migrations
   - Admin CRUD + graph endpoints

2. **Printables.com Import Service — ~2 days backend work**
   - New service: `IPrintablesImportService` (GraphQL fetch, CDN download)
   - New endpoints: `POST /api/3d-models/import-url/preview`, `POST /api/3d-models/import-url`
   - Add `SourceUrl`, `SourceLicense`, `SourceCreator`, `ImportedAt` to `Model3DFile` entity + migrations
   - MakerWorld deferred (blocker: Bambu Cloud token auth)

3. **Passkey (WebAuthn) Login — ~4 days backend work**
   - New NuGet: `Fido2NetLib`
   - New entity: `UserPasskeyCredential` (credential storage + audit)
   - New service: `IPasskeyService` (ceremony orchestration, challenge cache)
   - New endpoints: register/begin, register/complete, login/begin, login/complete, credentials list, revoke
   - Add `AuthMethod` field to login audit

**Linked Decisions:** decisions.md entries "Backlog: Electricity Cost Tracking via Smart Plugs", "Backlog: Printables.com Model Import", "Backlog: Passkey (WebAuthn) Login Support"

## Learnings

- **2026-05-31 — Printables import foundation (#349, PR #375).** `IPrintablesImportService` + `PrintablesGraphQLClient` + `GET /api/3d-models/printables/preview?url=` in `Farm.Slicer.Module.Api`. URL parsing via compiled `Regex` — accepts `/model/{id}` and `/model/{id}-{slug}` forms; `ParseModelId` is `public static` so tests can call it directly. GraphQL client uses raw `HttpClient` (named, 15 s timeout, User-Agent header) — no StrawberryShake. `PrintablesApiException` separates upstream errors (→ 502) from bad-URL parse errors (→ 400). DI in `SlicerApiExtensions.AddSlicerApiServices` via `AddHttpClient<PrintablesGraphQLClient>` + `AddScoped<IPrintablesImportService>`. Tests: URL parsing (Theory), mocked HttpMessageHandler for GraphQL client, Moq for controller outcomes — 18 tests, all green.

- **2026-05-31T16:42:** Before committing, scrub message for forbidden external refs: "bambuddy", "maziggy", "Bambu Buddy", github.com/maziggy/bambuddy. Acceptable alternatives: "adoption plan", "Phase N work breakdown", or standalone feature description. See .squad/decisions.md 2026-05-31T09:42 entry.
- **2026-05-31T16:42:** Before committing, scrub message for forbidden external refs: "external-reference-app", "external-author", "external reference app", [external reference repo]. Acceptable alternatives: "adoption plan", "Phase N work breakdown", or standalone feature description. See .squad/decisions.md 2026-05-31T09:42 entry.

- **2026-05-31 — SmartPlug provider pattern (PR #370).** `ISmartPlugProvider` lives in `src/api/Services/SmartPlug/`. Register all providers as `IEnumerable<ISmartPlugProvider>` singletons in `ServiceCollectionExtensions.RegisterSmartPlugProviders()`. Kasa uses raw TCP (port 9999, XOR obfuscation) — no `IHttpClientFactory` needed. Tasmota, Shelly, HA share the named `SmartPlug` HttpClient (5s timeout). Shelly auto-detects Gen 1 (`/meter/0`) vs Gen 2 (`/rpc/Switch.GetStatus`) by trying Gen 2 first. HA device address format: `{baseUrl}|{entityId}`; token in `HomeAssistant:Token` config key (env `PFARM__HomeAssistant__Token`). No DB entities in this PR — `PowerReading` is a plain record; entities + migrations in #346.

- **2026-05-31 — Artifact metadata endpoint pattern (#336, PR #365).** Added `GET /api/artifacts/{id}/metadata` to slicer-host `ArtifactsController`. Pattern: (1) load artifact, (2) load parent `SliceJob`, (3) compare `job.UserId` vs caller's `ClaimTypes.NameIdentifier` claim — admin bypass via `User.IsInRole("farm_admin")`. `downloadUrl` is hardcoded to `/api/artifacts/{id}` — same as the existing binary download action. DTO is a C# `record` in `Farm.Slicer.Module/Dtos/`. `[ProducesResponseType]` attributes cover 200/404/403. Tests use `ControllerContext` with a `DefaultHttpContext` carrying a `ClaimsPrincipal` for auth-sensitive unit tests — no need to spin up a full HTTP pipeline.

- **2026-05-31 — beads (`bd`) not available in worktrees.** Running `bd` from a worktree directory (e.g. `/Users/jpapiez/s/PFarm1-336`) fails with "no beads database found". The `.beads/` directory lives only in the main tree. Workaround: `BEADS_DIR=/path/to/main-tree/.beads bd ...`, or run `bd` from the main tree path. If `.beads/` is absent from the main tree entirely, the database has not been initialized — skip `bd sync` step and note as a blocker in the health report.

- **2026-05-31 — Spoolman filament cost provider (#342, PR #378).** `IFilamentCostProvider` is the abstraction; `SpoolmanFilamentCostProvider` is the Spoolman-backed implementation. Lives in `src/infra/Services/Cost/`. Uses `IMemoryCache` (5-min TTL, `spoolman_cpg_spool_{id}` / `spoolman_cpg_filament_{id}` keys). Registered as Scoped (not Singleton) to avoid captive-dependency with `ISpoolmanService` typed HttpClient. Optional ctor injection `IFilamentCostProvider? filamentCostProvider = null` follows same pattern as `IJobCostCalculationService?` in `PrintJobCompletionService`. All exceptions caught → `null` return; Spoolman unconfigured also returns `null` (BaseUrl empty check inside `ISpoolmanService`). Multi-spool cost path in `JobCostCalculationService` uses provider as fast path; falls back to settings cascade on null. Cost per gram = Price / InitialWeightG (spool), or Price / Weight (filament).
- **2026-06-01T15:18:38-07:00 — System info API pattern (#435).** `GET /api/system/info` lives in `src/api/Controllers/SystemInfoController.cs`, delegates to `Farm.Infrastructure.Services.SystemStatus.ISystemInfoService`, and returns DTOs from `src/infra/Dtos/SystemInfoDtos.cs`. Host metrics pattern: CPU = `/proc/stat` on Linux or `GetSystemTimes` on Windows; memory = `/proc/meminfo` on Linux or `GlobalMemoryStatusEx` on Windows with `Process.WorkingSet64` fallback; disk/archive sizes come from `IStoragePathService.GetGcodeStorageDirectory()`, DB engine/version/size come from `AppDbContext.Database.ProviderName` + provider-specific scalar queries. Frontend contract lives in `src/Web/ReactApp/src/types/api.ts` + `src/Web/ReactApp/src/services/api.ts`; auth/shape coverage is in `src/tests/Farm.Web.Api.Tests/Integration/SystemInfoIntegrationTests.cs`.
- **2026-06-02T06:49:25.421-07:00 — Printables selected-file import pattern.** `POST /api/3d-models/import/printables` now accepts `fileIds` on `src/slicer/Farm.Slicer.Module/Dtos/PrintablesImportRequest.cs`; the controller stays transport-only in `src/slicer/Farm.Slicer.Module.Api/Controllers/PrintablesImportController.cs` and delegates import selection to `src/slicer/Farm.Slicer.Module/Services/PrintablesImportService.cs`. Service rule: null/empty `fileIds` imports all previewed STL files, unknown IDs throw `ArgumentException` for controller-mapped 400s, and actual file import resolves temporary download links via `PrintablesGraphQLClient.GetStlDownloadUrlAsync`, then pipes each file through `IModel3DFileService.UploadModelAsync` + `SetAttributionAsync`. 

## Issue #353 — WebAuthn/FIDO2 Passkey Ceremony Endpoints (PR #380)

**Branch:** `squad/353-passkey-webauthn-endpoints`

### Package Gotcha
- NuGet package is **`Fido2`** v4.0.1 (by abergs, 5M+ downloads) — NOT `Fido2NetLib` which stalled at `1.0.0-alpha`.
- Namespace is still `Fido2NetLib` despite the different package name.
- Companion: `Fido2.Models` v4.0.1 (types). `Fido2.AspNet` v4.0.1 is optional (not used).

### Fido2 v4 API
- `Fido2(Fido2Configuration)` — concrete class, not interface-backed.
- `RequestNewCredential(RequestNewCredentialParams)` → `CredentialCreateOptions` (sync)
- `MakeNewCredentialAsync(MakeNewCredentialParams, ct)` → `RegisteredPublicKeyCredential`
- `GetAssertionOptions(GetAssertionOptionsParams)` → `AssertionOptions` (sync)
- `MakeAssertionAsync(MakeAssertionParams, ct)` → `VerifiedAssertionResult`
- `CredentialCreateOptions.ToJson()` / `.FromJson(string)` for cache round-trip
- `AssertionOptions.ToJson()` / `.FromJson(string)` for cache round-trip

### CredentialCreateOptions Required Members (v4)
Object initializer requires: `Rp`, `User`, `Challenge`, `PubKeyCredParams`.
- `PublicKeyCredentialRpEntity` has positional constructor: `(string id, string name, string? icon)`
- `Fido2User`: properties `Id`, `Name`, `DisplayName`

### AssertionOptions Required Members (v4)
`Challenge` and `RpId` — can use object initializer: `new() { Challenge = [...], RpId = "localhost" }`

### Vulnerability Warnings
`Fido2` v4.0.1 pulls in `PeterO.Cbor` and `System.IdentityModel.Tokens.Jwt` which have known CVEs.
These are transitive and expected — not blockers for the feature work.

### Architecture Decisions
- Challenges stored in `IDistributedCache` (in-memory; swap for Redis in prod)
- Replay prevention: cache key deleted immediately on read (`LoadOptionsAsync`)
- Credential persistence deferred to #354 — `CompleteRegistration` and `CompleteLogin` log TODO warnings
## 2026-05-31 — Trio Review Cycle #355, #371, #405

Participated in multi-round trio review cycle. Key learnings:

1. **Reviewer-lockout protocol:** Strict three-reviewer consensus with rotation of fresh hands prevents fatigue.
2. **Kane surgical-fix MVP:** Small, scoped corrections across all three branches proved cost-effective.
3. **Session-end report validation:** Coordinator must verify trio drops match current commit SHA.
4. **PR auto-close gap:** `Closes #N` does not fire on development merges; manual close required.
