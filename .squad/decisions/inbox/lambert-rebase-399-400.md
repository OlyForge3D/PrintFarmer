# Lambert Rebase Decision Note — PR #399 & PR #400
**Date:** 2026-05-31T13:11:00-07:00  
**Author:** Lambert (Backend Dev)  
**Requested by:** Brady

---

## PR #399 — `squad/335-preview-button-and-url-helper`

### Conflicts resolved: 3 commit steps

**Commit 1 — gcodePreviewService (add/add):**
- Development had already integrated and improved `gcodePreviewService.ts` with Z-hop detection (`pendingZ` logic — defers layer promotion until first extrusion confirms the Z increase is not a hop).
- Branch's original commit carried the earlier simpler implementation (bare Z-increase promotion).
- **Decision:** Took development's version for both `gcodePreviewService.ts` and its test file. Development's Z-hop logic is correct and more complete; the branch's tests are a subset. No functional regression to PR #335 — it consumes `IGcodePreviewService` by interface only.

**Commit 3 — ArtifactsController.cs (content conflict in `GetMetadataAsync`):**
- PR #335's fix commit tried to add `downloadUrl` to the old anonymous object in `GetMetadataAsync`.
- Development (via PR #336) replaced the anonymous object with typed `ArtifactMetadataDto` plus an ownership auth check (`farm_admin` or job owner) and `ProducesResponseType` attributes. The `ArtifactMetadataDto` already includes `downloadUrl`.
- **Decision:** Kept development's `GetMetadataAsync` (typed DTO, auth gate, attributes). PR's `downloadUrl` intent is fully satisfied by `ArtifactMetadataDto`. PR's `downloadUrl` addition to `ListByJobAsync` was preserved unchanged (no conflict there).

---

## PR #400 — `squad/339-bed-type-override`

### Conflicts resolved: 2 commit steps

**Commit 1 — QuickSliceModal (add/add):**
- `navigate()` target: development used `/slice-jobs` (exists as a redirect to `/admin/workers?tab=jobs`); branch used `/slicer/jobs` (no route defined in App.tsx).
- **Decision:** Kept `/slice-jobs` (development). Updated test assertion to match.

**Commit 2 — NewSliceJobPage.tsx (content conflict):**
- Development wrapped `SlicerSettingsPanel` in `AdvancedSettingsDisclosure` (PR #340).
- Branch added a Bed Type Override `<Select>` UI panel immediately before the settings panel.
- Both changes are independent and compose cleanly.
- **Decision:** Preserved the Bed Type Override section first, then the `SlicerSettingsPanel` wrapped in `AdvancedSettingsDisclosure`. Both features fully present.

---

## Build / Test Verification

| PR | Backend build | Frontend build | Tests |
|---|---|---|---|
| #399 | ✅ 0 errors, 8 pre-existing warnings | — | — |
| #400 | — | ✅ clean | ✅ 10 failed / 2073 total — identical to development baseline (10 failed / 2066); +7 new passing tests from PR |

Pre-existing failures are unrelated to these PRs: `PrinterCostFields` (missing QueryClientProvider), `FailureDetectionMonitoringOverlay` (text matcher), `metadata-editors`, and `NewSliceJobPage > slicer settings panel` (hidden by AdvancedSettingsDisclosure — development regression, tracked separately).

---

## Bambuddy / Maziggy Check

No references found in any changed file.

---

## Post-push Status

- PR #399: `MERGEABLE` (mergeStateStatus: UNSTABLE — pre-existing)
- PR #400: `MERGEABLE` (mergeStateStatus: UNSTABLE — pre-existing)
- Comments posted to both PRs.
