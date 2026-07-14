# Newt History

## 2026-06-02: Design Language & Theme QA Audit

**Scope:** Frontend design system, visual QA across 7 themes on deployed app  
**Status:** Decisions and findings merged to squad/decisions.md

- Completed visual QA audit across all 7 supported themes on deployed instance (http://10.0.0.20)
- Filed issue #467: Login backdrop darkens empty viewport (UX issue)
- Filed issue #468: Logo SVG not recolorable per-theme (design system gap)
- Filed issue #469: QA blocked by auth credentials (process improvement)
- Confirmed 7-theme system functioning at foundation level (body typeface, background, text-primary per-theme)
- Identified component-level issues (logo, login backdrop) vs token-level (none)

## 2026-06-03: 3D Viewer Doubled-`/api/api/` Fix Verification (PR #495)

**Scope:** Confirm PR #495 fix is deployed to http://10.0.0.20 and that 3D models no longer float above the build plate in the Files page and slicer viewers.

**What was verified:**
- Source: `baseURL: ''` override present at `ModelViewer3D.tsx:427` and `ThreeMFViewer.tsx:303`.
- Deployed bundle: confirmed the same minified pattern `Ye.get(...,{responseType:"arraybuffer",baseURL:""})` ships in `ModelViewer3D-Ciee1SDf.js` (Files page) and `NewSliceJobPage-wSLTl2rl.js` (slicer ThreeMFViewer chunk).
- Unauthenticated network probing showed zero `/api/api/` doubled-prefix requests.

**What was NOT verified:**
- Live visual check of build-plate placement on the Files/Slicer 3D viewers. **Credential blocker (#469) recurred.** No QA account; self-register lands inactive ("requires admin approval"); `admin` is now temp-locked from probing variants. Filed verification status as a comment on #496.

**Recommendation:** Provide a QA-tier account or activate the freshly-registered `newtqa` user so visual checks (including the recent unified Files page from #500) can be run end-to-end without re-blocking on auth every audit.

## Core Context

Newt is a deployment & DevOps specialist. Key contributions:
- Docker build optimization & multi-stage Dockerfile refactoring
- Backend plugin system Docker integration
- Container image size reduction & layer optimization
- Deployment script improvements & error handling
- Camera fit revision & UI integration (2026-03-25)
- FailureDetectionMonitoringSummary redesign (2026-06-10)
- Infrastructure automation & cloud deployment

## Team Coordination (2026-06-02)

**Scribe Session 17:44:47Z**
- Merged Theme Contrast Tokens For Accent-Filled Controls decision (Newt)
- Processed 2 inbox decisions; cleaned up inbox workflow
- Created orchestration logs for ripley-14 and newt-8 sessions
- decisions.md: 268,270 bytes → 2 entries merged

## Learnings

- Completed the authenticated theme QA sweep across Dashboard, Printers, Settings, Preferences, and the major authenticated nav routes for all 7 supported themes.
- Filed issue #470 for unread notification badge contrast failures across authenticated themes.
- Filed issue #471 for accent and danger control contrast failures on Settings and Preferences.
- Filed issue #472 for unreadable theme selector labels on Preferences.
- The current theme system is still strong at the token/foundation layer, but shared component variants that sit on accent fills need dedicated on-accent foreground tokens instead of generic white text.

## Archived History

Older entries archived to history-archive.md for size management.


## 2026-06-03: Full-Route Theme Audit & QA Review Session

**Scope:** Complete theme audit on http://10.0.0.20 and QA assessment  
**Status:** SUCCESS — findings documented and merged to decisions.md

**Key Findings:**
- Identified boundary scope issue affecting app shell stability (#473, #475)
- Page-level crashes wipe entire shell due to ErrorBoundary positioned outside layout
- Route transitions blank entire shell due to Suspense positioned outside layout
- Recommended solution: move Suspense and ErrorBoundary inside layout to wrap only page slot
- Settings theme preview assessment: metadata-driven approach validated
- Theme coverage across all 7 themes tested and confirmed

**QA Review Results:**
- Settings UI Polish review completed
- Command-K (command palette) integration validated
- Settings 2-pane layout restructuring confirmed
- Accent foreground token application verified
- Profile discoverability improvements assessed
- 3MF bed placement consistency confirmed

**Decisions Documented:**
- Scope Suspense + ErrorBoundary to the page slot, not the root
- Settings Shell Uses A Fixed-Height Two-Pane Surface confirmed
- Settings Theme Preview Stays Metadata-Driven validated
- Accent Foreground Tokens For Shared Frontend Controls in effect

**Status:** Ready for code review trio (Bishop, Hicks, Vasquez)


## 2026-07-14: Issue #715 F10 Offline Idempotency — Round 7 Remediation (.NET backend)

**Role this round:** Remediation author (backend) — fresh r7 author; locked out from delegating to Lambert, Kane, Dallas, Hudson, Ripley, Apone, Frost.
**Branch:** jpapiez-squad-715-idempotency-backend
**Parent HEAD:** 8930b10af97f68d14b0f33b5a1a87e2c18a0f26b (Frost r6 BIN2 collation work + base de289394a from #578/#751)
**Context:** r6 (Frost) applied `Latin1_General_100_BIN2` to Sku / Bins.Code / OperationKey / HarvestOperationKey. Bishop APPROVED; Vasquez + Hicks REQUEST_CHANGES (4 blockers).

**Blockers fixed:**
- **V1 (Vasquez) — CHECK constraints full-scan under Sch-M lock:** Recreated `CK_PartInventories_Sku_Normalized` and `CK_Bins_Code_Normalized` with `WITH NOCHECK` (metadata-only) in both Up() and Down() of `20260713235657_ExtendCaseSensitiveCollationToSkuAndOperationKey.cs`. Existing rows grandfathered; future DML still enforced. Safe: feature <1 week old; NormalizeSku/NormalizeBinCode always ToUpperInvariant.
- **V2 (Vasquez) — offline ledger index rebuild:** Converted the two indexed ledger columns (`PrintJobs.HarvestOperationKey`, `PartInventoryAdjustments.OperationKey`) from EF AlterColumn to raw DROP INDEX → ALTER COLUMN COLLATE → CREATE UNIQUE NONCLUSTERED INDEX, with an **edition-aware** ONLINE choice via `SERVERPROPERTY('EngineEdition') IN (3,5,8)`. **Scope deviation (documented):** the task claimed ONLINE=ON is "silently ignored" on Standard — verified FALSE (Standard hard-errors), so I used runtime edition detection instead of hardcoding ONLINE=ON, avoiding a re-introduced deploy failure. Filtered predicates reproduced byte-exact so the model snapshot is unchanged. Sku/Code left on EF AlterColumn (offline, per V2 scope).
- **H1 (Hicks) — Down() didn't restore collation:** Added explicit `collation: "SQL_Latin1_General_CP1_CI_AS"` to Down() AlterColumns in BOTH `20260713235657_Extend...` and `20260713163813_AddIdempotencyKeyCaseSensitiveCollation.cs` (UserId/RouteKey/IdempotencyKey), plus raw `COLLATE ...CI_AS` on ledger Down. EF's oldCollation: is metadata-only and emits no COLLATE. Added rollback-hazard doc comments (BIN2-distinct rows may collapse under CI_AS unique index).
- **H2 (Hicks) — client could pre-occupy server `harvest:` keys:** Generalized `IdempotencyKeyUtilities.IsReservedOperationKey` to a `ReservedOperationKeyPrefixes` set `{ "idem:", "harvest:" }` (added `HarvestOperationKeyPrefix` const), preserving NFKC pre-normalization + Trim + OrdinalIgnoreCase loop. `ReservedOperationKeyPrefixAttribute` + `PartInventoryService` guard auto-pick-up the extended set (they delegate); generalized their messages while retaining the literal `idem:` (test contract). Did NOT touch PartHarvestService server-side `harvest:` generation (bypasses DTO validation). Added unit + service tests for harvest:/Harvest:/fullwidth ｈａｒｖｅｓｔ：ｆｏｏ (rejected) and harvestable-tote (accepted). Documented in XML that new server namespaces MUST be added to the reserved set.

**Validation:**
- `dotnet format` — my 8 files clean (verified via --include). Whole-solution format check fails on ~40 pre-existing unrelated files (OrcaSlicer worker tests, other migrations) — pre-dates r7, out of scope, not touched.
- `dotnet build farm-web.sln -c Debug` — 0 warnings, 0 errors (warnings-as-errors).
- Focused tests ×3 (Idempotency|PartInventory|PartsInventory|PartHarvest) — 211 passed, 0 failed, deterministic.
- `ef migrations has-pending-model-changes` — clean for BOTH sqlserver and postgres (raw-SQL conversion left model/snapshot untouched).
- Full suite — only the 4 pre-approved failures (3× OrcaSlicerAssetRegistryTests CRLF, 1× FilamentCoverageControllerTests perf budget). No other failures.

**Files touched:** 2 migrations + 3 infra source (`IdempotencyKeyUtilities`, `ReservedOperationKeyPrefixAttribute`, `PartInventoryService`) + 3 test files. No push, no PR (per instructions).

