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
## Learnings — #941 backend fix (2026-07-25)

**Bulk vs per-key POST split is a common source of drift.** When two endpoints
target the same underlying resource, the "cheap" per-item endpoint tends to
grow independently of the "canonical" bulk one and drops guarantees the bulk
side owns. In this repo that manifested as (a) the per-key endpoint missing
the `farm_admin` role gate the bulk endpoint has, and (b) missing the
`IValidatableSetting.Validate()` call. Both slipped because the frontend
migration in #935 flipped the primary save path without either side asking
"does this endpoint have parity with the one it's replacing?"

**Attribute-level defects need HTTP-level tests.** Unit tests that `new` the
controller and call the method directly bypass the auth pipeline and model
binding entirely — that's why zero tests caught the missing
`[Authorize(Roles)]` attribute for weeks. When gating on filters or
attributes, use `CustomWebApplicationFactory`'s `CreateAuthenticatedClientAsync`
(non-admin) vs `CreateAdminClientAsync` (with farm_admin) and assert on the
HttpStatusCode. Those two helpers exist for exactly this scenario.

**Deliberate-break proof works.** Removing each fix in turn and confirming the
corresponding test failed (then restoring) is the cheapest possible way to
prove that new tests actually exercise the change. Skipping this step is how
every prior #941 reviewer submitted "green" work that still had holes.

**Shared error-response shape matters.** The React SettingsPage error parser
splits `errors`-dict keys on `.` into `section.field` — meaning a per-key
endpoint returning `errors[string.Empty] = message` vs `errors[sectionKey] = message`
renders in different places in the UI (or nowhere). When copying a validation
response shape across endpoints, match it byte-for-byte. Extract a shared
helper so drift is impossible.

**CA1859 fires on ActionResult return types.** When adding a private helper
that always returns a `BadRequestObjectResult`, declare its return type as
`BadRequestObjectResult` (not the broader `ActionResult`) or the analyzer
warns about a boxed return. Small detail but worth remembering — the "0
warnings" baseline is unforgiving.


## Printer list endpoint missing RowVersion (2026-08-16)

`GET /api/printers` (`CompletePrinterDto[]`) omitted `RowVersion` /
`ConfigurationRevision`, so the React printers page — which sources every printer
object from that list — hit the `rowVersion`-unavailable guard on every mutation
(spool assign/eject/change, material loadout, calibration, enable/disable). Fixed by
adding both members to `CompletePrinterDto` and populating them in
`GetAllCompleteDtosAsync` (both success and offline-fallback branches), mirroring
`PrinterDto` / `GetAllWithStatusDtosAsync`.

**Audited the other list DTOs:** `PrinterFastDto` has no live endpoint (`getPrintersFast`
actually calls `/printers`); `PrinterSummaryDto` is dashboard/alert display-only;
`PrinterWithCapabilitiesDto` is export-only. None source a mutable printer object, so
none got `RowVersion` — deliberately avoided bloating unused/display DTOs.

**Encoding gotcha:** editing a large file with `Set-Content` stripped its UTF-8 BOM and
`dotnet format` flagged CHARSET. Restored with `UTF8Encoding($true)`. Prefer the `edit`
tool over whole-file PowerShell rewrites on BOM'd C# files.

Added `PrinterListDtoRowVersionTests`: list DTO carries non-null base64 `RowVersion`
(Revision > 0) and round-trips to the single-printer endpoint value. Build clean (no new
warnings), all 1370 Farm.Web.Api.Tests pass.


## OrcaSlicer profile-family backend trace (2026-08-25)

- Registered printers resolve Orca machine variants through `Printer.ModelId` -> `PrinterModelAlias.SlicerModelName` -> worker `printer_model`; the machine lookup never reads custom `SlicerDbContext` profiles.
- Single-profile clone writes a user-owned DB `MachineProfile` but leaves raw `name`/`printer_model`/`inherits` unchanged and does not create an alias or worker entry. This data-source split is the definitive clone-loop cause; worker cache invalidation cannot help.
- Backend has `MachineModelProfile` + child `MachineProfile.MachineModelProfileId`, but seeding does not populate the child FK and the family entity has no user owner.
- A family clone must create one family plus all nozzle variants, rewrite exact variant names and shared `printer_model`, link every child, add/resolve target association, and materialize process/filament `compatible_printers` with the new exact names. Prefer computing family-editable fields as the invariant intersection across resolved variants with a per-nozzle denylist.
- Detailed trace: `decisions/inbox/lambert-profile-family-backend.md`.


## OrcaSlicer profile-family inheritance follow-up (2026-08-25)

- Real Voron machine families do not inherit from a shared model-settings base: `machine_model` is metadata, while every nozzle child inherits `fdm_klipper_common` and repeats family geometry/identity. A custom family therefore still needs one child per nozzle.
- Voron process compatibility is primarily exact system-preset names (`compatible_printers`), not `printer_model`. Preserve a custom child's exact source preset name as its compatibility identity while keeping custom family/child names for selection and display.
- Orca's user-preset compatibility identity comes from `inherits`, but PrintFarmer's current `WithSystemPresetInherits` rewrites that value to the DTO display name. Genuine custom children need a separate path that preserves their source-system ancestor.
- DB/custom profiles do not traverse the worker resource inheritance resolver: the API snapshots `RawJson` verbatim and workers expect complete flat settings. Small override-only inheritance requires a new resolver/materializer before slice-job snapshotting.
- Recommended model: persist source preset identity + Orca provenance/version, small family overrides, and one child per nozzle; materialize against the worker catalog; use source preset identity for process/filament matching; use DB family/target-printer association for UI selection. This avoids duplicating all process/filament profiles.
- `clone-from-template` clones process rows only and sets a soft printer pointer; it does not create a custom machine profile or family associated with the target catalog model.

## 2026-08-25 — Machine profile family cloning Phase 1 + Phase 2b

Implemented reason-coded 404s for both profile lookup gates, the SlicerDbContext family/variant metadata and render-state model, PostgreSQL/SQL Server/SQLite migrations, transactional `clone-family`, deterministic non-null hashes, native Orca family rendering with per-nozzle deltas and resolved compatibility, AppDbContext alias creation, and the atomic Parker worker bundle client. Added fidelity, Prusa-condition, empty/universal filament, missing-source, persistence/conflict, worker-contract, discovery, execution, migration, and Phase 1 tests. Full build passed; all three snapshots have no pending changes; Lambert-scoped format passed. The required full test run exposed and drove a fix for the string-converted enum default; all relevant post-fix targeted tests passed. See `decisions/inbox/lambert-phase2b-impl.md` for contracts and full evidence.


## 2026-08-25 — Phase 1 frontend contract reconciliation

Reconciled both profile lookup gates with Ripley's landed consumer: coded 404 bodies now serialize exactly `code` and optional `detail` (null detail omitted), replacing the initially implemented `message` field. Added a wire serialization test that rejects `message`; focused controller tests pass 8/8 and scoped formatting passes. Recorded the complete camelCase `POST /api/slicer/profiles/clone-family` request, 201 response, and error-code/status map in `decisions/inbox/lambert-phase2b-impl.md` for the Phase 3 wizard.

## 2026-08-25 — Worker custom-inheritance 422 preservation

Reconciled Parker's final custom-bundle mutation behavior: HTTP 422 `failures[]` is parsed by `ProfileFamilyWorkerClient`, preserving bundle/family/profile/missing-parent details in `ProfileFamilySourceException`. This keeps failed render state while allowing the clone-family controller to return `source_preset_unavailable` 422 rather than generic worker-unavailable 503. Added adapter and endpoint contract coverage; focused tests pass 3/3 and scoped formatting passes.

## 2026-08-25 — Final profile-family migration regression verification

Confirmed Parker's 1,352-test SQLite cascade came from the transient enum-default mapping and no longer reproduces: explicit string conversion plus SQL string default passes the exact provider-aware SQLite migration test. Full suite now passes Slicer.Module 1,178/1,178 and exposes only six documented missing-server-environment tests plus two stale expected migration lists; added the PostgreSQL/SQL Server IDs and their focused contract passes 2/2. All three provider snapshots are clean, and custom family/variant hashes remain deterministic and non-null for SQL Server's unfiltered unique index.