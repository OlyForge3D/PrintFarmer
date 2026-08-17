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
