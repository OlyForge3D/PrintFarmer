## Failure Detection Badge Placement (2026-03-25)

**Decision:** Consolidate failure detection shield to header badge only; remove camera overlay.

**Owner(s):** Dallas (Lead), Ripley (Frontend Dev)

**Status:** Recommendation ready for team review

**Analysis:**
- Header badge: essential, always visible, glanceable
- Camera overlay: redundant, distracts from video, identical information
- Single source of truth eliminates confusion and visual noise
- Modal entry via header badge maintains full detail access
- Follows PrintFarmer conventions (secondary status in header)

**Implementation:**
1. Remove \`FailureDetectionMonitoringOverlay\` import from CompactPrinterCard.tsx (line 18)
2. Remove overlay prop from PrinterCameraPreview call (lines 230–236)
3. Optionally deprecate overlay component if unused elsewhere

**Affected Components:**
- src/Web/ReactApp/src/features/printers/components/CompactPrinterCard.tsx (lines 176–180, 231–236)
- src/Web/ReactApp/src/features/printers/components/PrinterCameraPreview.tsx (overlay prop)

**Pattern Compliance:**
✅ Maintains \`compact-status-detail-modal\` skill pattern  
✅ Maintains \`monitoring-lifecycle-badges\` skill pattern  
✅ Improves visual focus by removing competing UI  

**Next Step:** Team decision on implementation timeline.

---

## Icon-Only Failure Detection Shield Refinement (2026-03-25)

**Decision:** Refactor failure detection badge to icon-only form; consolidate duplicate status affordance across card header and camera overlay.

**Owner(s):** Ripley (Frontend Dev), Kane (Tester), Dallas (Product Lead)

**Status:** Implemented and approved; ready for merge conditional on regression test validation.

**Implementation Summary:**

1. **Component Refactor:** `FailureDetectionMonitoringBadge.tsx`
   - Removed `Badge` wrapper (pill border eliminated)
   - Removed inline `<span>{label}</span>` text
   - Applied state-based color mapping to shield icon:
     - Monitoring: `text-pf-success` (green)
     - Checking: `text-pf-text-secondary` (gray)
     - Disabled: `text-pf-text-tertiary` (light gray)
     - Error: `text-pf-error` (red)
   - Kept button wrapper + aria-labels + tooltip (`title` attribute)
   - Maintained modal trigger on click
   - Added `hover:bg-white/10` for visual feedback

2. **Overlay Consolidation:** `CompactPrinterCard.tsx` and `DetailedPrinterCard.tsx`
   - Removed `FailureDetectionMonitoringOverlay` imports
   - Removed `overlay` prop from `PrinterCameraPreview` calls
   - Single header badge becomes sole status affordance
   - Modal entry point preserved via badge click

3. **Test Coverage Updates:**
   - 6 focused badge tests in `FailureDetectionMonitoringBadge.test.tsx`
   - 3 updated integration tests in `obico-ml-badge.test.tsx`
   - 106/106 printer tests passing
   - Lint clean, build succeeds (0 errors, 0 warnings)

**Pattern Compliance:**
- ✅ `compact-status-detail-modal` — Icon as clickable trigger, modal for full detail
- ✅ `monitoring-lifecycle-badges` — State reflects active monitoring lifecycle
- ✅ Accessibility mitigations: aria-labels, tooltip fallback, state-based color + additional context

**Kane's Approval & Risk Assessment:**

**Icon-only badge:** APPROVED with 3 mandatory regression tests (Tier 1 blocking gate)
- Tooltip content assertions for all states
- Card header integration: icon-only visible, no inline text
- State styling differentiation validated
- **Accessibility requirement:** Manual screen reader audit to verify `title` attribute announced on button focus

**Overlay removal:** APPROVED for implementation
- Core failure detection logic well-tested at component level
- Integration-layer gaps identified; Kane recommends 2–3 additional integration tests post-removal
- Risk assessment: low-to-medium (layout refactor, not behavior change)

**Key Learnings:**

1. Icon-only badges require strong compensatory UX: tooltip + aria-label critical, not optional
2. State-based color mapping sufficient for sighted users but requires additional context (tooltip, aria-label) for color-blind users
3. Dual-surface redundancy (badge + overlay) creates cognitive load; consolidation to single affordance improves clarity
4. Unit tests excellent; integration-layer regression tests catch layout issues unit tests miss

**Affected Components:**
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringBadge.tsx` (icon-only refactor)
- `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringBadge.test.tsx` (6 focused tests)
- `src/Web/ReactApp/src/test/features/printers/obico-ml-badge.test.tsx` (3 updated tests)
- `src/Web/ReactApp/src/features/printers/components/CompactPrinterCard.tsx` (overlay prop removed)
- `src/Web/ReactApp/src/features/printers/components/DetailedPrinterCard.tsx` (overlay prop removed)

**Next Steps:**
1. Ripley validates Tier 1 regression tests added (blocking gate for merge)
2. Manual accessibility audit with screen reader (verify title announcement on focus)
3. Visual regression check (both card layouts, mobile + desktop)
4. Parker lands clean commit after Kane re-approval and validation

---

## Triple-Model Pre-Commit Code Review Gate (2026-04-01)

**Decision:** All commits must pass a triple-model code review gate before landing.

**Owner(s):** Jeff Papiez (directive), Squad Coordinator (enforcement)

**Status:** ✅ Implemented — agents created, ceremony defined

**Protocol:**
- Three dedicated Code Reviewer agents review every commit in parallel:
  - **Bishop** → GPT-5.4
  - **Hicks** → Gemini 3 Pro Preview
  - **Vasquez** → Claude Opus 4.6
- Each reviewer independently analyzes the diff for bugs, security issues, logic errors, correctness
- Each outputs APPROVE or REQUEST_CHANGES with severity-ranked issues
- 🔴 Critical issues from ANY reviewer → commit blocked until resolved
- 🟡 Warnings should be addressed; may proceed with justification
- 🔵 Info items are advisory
- Top issues consolidated and fixed before commit proceeds

**Rationale:** Multi-model diversity catches more issues than a single model. Different architectures (GPT, Gemini, Claude) have complementary strengths — pattern recognition, logical reasoning, and contextual understanding.

**Affected Files:**
- `.squad/agents/bishop/charter.md` — GPT-5.4 reviewer
- `.squad/agents/hicks/charter.md` — Gemini 3 Pro reviewer
- `.squad/agents/vasquez/charter.md` — Claude Opus 4.6 reviewer
- `.squad/ceremonies.md` — Code Review Gate ceremony added
- `.squad/routing.md` — Code review routing updated
- `.squad/team.md` — Three new members added
- `.squad/casting/registry.json` — Three new registry entries

---

---

## 3D Models Upload & Display: Multi-Agent Investigation (2026-04-05)

**Status:** Multiple coordinated fixes identified and ready  
**Owners:** Dallas (Lead), Ripley (Frontend), Lambert (Backend), Kane (QA)

### Summary

Three independent bugs discovered affecting 3D model upload, display, and download:

1. **Frontend cache mismatch** (Ripley) — Upload modal invalidating wrong query key
2. **Backend schema initialization** (Lambert) — SlicerDbContext never initialized at startup
3. **File path resolution** (Parker/Lambert) — Service returning relative paths instead of absolute

All fixes are surgical and low-risk. Ready for coordinated merge after validation.

### Bug Details

#### Bug A: Models Not Appearing After Upload (Frontend)

**Root Cause:** `ModelUploadModal` was calling `queryClient.invalidateQueries(['models-search'])` but `FileBrowser` uses `['file-browser', viewMode, params]` key.

**Fix:** Remove manual invalidation, rely on `onUploadSuccess()` callback that calls `fileBrowserRef.current?.refetch()`.

**Files Changed:**
- `src/Web/ReactApp/src/common/components/modals/ModelUploadModal.tsx`

**Status:** ✅ Implemented

---

#### Bug B: Models Not Persisting After Upload (Backend Schema)

**Root Cause:** `SlicerDbContext` had no initialization logic. `Model3D` table never created. Uploads appeared to succeed but data wasn't persisted.

**Fix:** Initialize `SlicerDbContext` during startup via `DatabaseInitializationExtensions.InitializeDatabaseAsync()`.

**Files Changed:**
- `src/api/Infrastructure/DatabaseInitializationExtensions.cs`
- `src/api/ProgramHelpers.cs`

**Status:** ✅ Implemented

---

#### Bug C: 404 When Downloading Model Files (Backend Paths)

**Root Cause:** `GetModelFilePathAsync()` and `GetModelThumbnailPathAsync()` returned relative paths (e.g., `filename.stl`). Controller's `File.Exists(filePath)` check failed.

**Fix:** Return absolute paths using `Path.Combine(_modelsPath, model.FileName)`.

**Files Changed:**
- `src/slicer/Farm.Slicer.Module/Services/Model3DFileService.cs`

**Consequence:** 4 models uploaded on Apr 5 became orphaned (database records exist but files unreachable). Users must re-upload.

**Status:** ✅ Implemented

---

#### Bug D: Tag Filtering Not Implemented (Deferred)

**Root Cause:** `Model3DFileService.QueryAsync()` accepts `tagIds` but never passes to repository. Feature is a non-functional stub.

**Fix:** Add `tagIds` parameter to `IModel3DFileRepository.QueryModelsAsync()` and implement cross-context filtering (`Model3DTagMapping` in `AppDbContext`, `Model3D` in `SlicerDbContext`).

**Challenge:** Cross-context query design needed. Requires coordination between Lambert and backend architecture.

**Status:** 🔄 IN PROGRESS (separate work item, not blocking upload/download)

**Test Gap:** No tests for tag filtering exist. Kane adding comprehensive tests.

---

### Validation Checklist

Before merging:
- [ ] Frontend upload shows models immediately (Ripley's cache fix)
- [ ] Database schema exists and accepts inserts (Lambert's init fix)
- [ ] File downloads return 200 OK, not 404 (Parker's path fix)
- [ ] Thumbnails display correctly (if generated)
- [ ] No regression in existing model operations (Kane's tests)

### Team Impact

**Ripley:** Document FileBrowser + upload modal pattern in squad skills  
**Lambert:** Document SlicerDbContext initialization pattern  
**Kane:** Tag filtering is now a documented priority work item  
**Parker:** Coordinate with Lambert on volume mount validation in health checks  

### Next Steps

1. Validation tests run and pass
2. Atomic commit of all three fixes
3. Notify user both upload visibility and file download working
4. File separate issue for tag filtering with proper design

---

## Tag Filtering Implementation Gaps (2026-01-11 — Kane)

**Status:** Bug identified, deferred for proper design  
**Severity:** Medium — Feature non-functional but not affecting core upload/download  

### Root Cause

`Model3DFileService.QueryAsync()` accepts `tagIds` parameter (line 165) but completely ignores it:
- Not passed to repository layer
- Repository interface has no `tagIds` parameter
- No cross-context query logic implemented

### Challenge

`Model3DTagMapping` lives in `AppDbContext`, `Model3D` lives in `SlicerDbContext`. Cross-context join requires:
1. Query model IDs from Model3DTagMapping in AppDbContext
2. Filter Model3D in SlicerDbContext by those IDs
3. Paginate correctly across both contexts

### Recommendation

Coordinate with Lambert on cross-context query strategy. Consider:
- Service-layer join (fetch tag mappings, then filter models) — Simple but less efficient
- Database view (if using SQL Server/PostgreSQL) — Complex but efficient
- Specification pattern with predicate composition — Flexible, testable

### Test Coverage

Kane adding tests:
- Tag filtering with single tag
- Tag filtering with multiple tags (AND/OR logic)
- Filter by non-existent tag (empty results)
- Verify filter doesn't affect pagination

## bed_exclude_area coPoints Display Fix

**Author:** Ripley (Frontend Dev)
**Date:** 2026-07-18
**Status:** COMMITTED
**Impact:** Low (single helper function, all tests pass)

### Context

The toString() helper in MetadataProfileRenderer.tsx is the central string coercion function for displaying slicer settings values. It handles the conversion from raw profile data (strings, numbers, booleans, arrays) to display strings for text inputs.

### Decision

Handle arrays explicitly in toString():
- Empty arrays [] fall back to meta.default (same as null/undefined)
- Non-empty arrays use raw.join(', ') for readable comma-separated display

### Rationale

String([]) in JavaScript returns "" (empty string), which bypasses the existing null/undefined fallback. This caused coPoints fields like bed_exclude_area to render blank when the profile value was an empty array (the common case for most machine profiles).

### Trade-offs

- This changes toString behavior for ALL array-valued settings, not just bed_exclude_area. The new behavior (join with ', ') is strictly better than String() (which joins with bare ',' and no spaces).
- Empty arrays now show the metadata default instead of blank. This is consistent with how null/undefined are handled.

