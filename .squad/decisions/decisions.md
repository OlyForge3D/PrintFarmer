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
