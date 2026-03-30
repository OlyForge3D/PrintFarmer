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
