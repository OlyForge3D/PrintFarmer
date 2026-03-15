# Camera Research Session Log

**Date:** 2026-03-15  
**Duration:** Parallel research agents (Brett + Lambert)  
**Participants:** Brett (competitive research), Lambert (technical analysis)  
**Outcome:** Camera control reclassified from "won't fix" → Phase 1.5 feature

## Session Summary

User challenged closing camera control as won't-fix. Two parallel research agents investigated:

1. **Brett (Competitive Research):** Validated that camera management exists ABOVE firmware level across industry. All 5 major competitors (SimplyPrint, 3DPrinterOS, Repetier, Mainsail, Fluidd) decouple cameras from printer firmware. User demand clear (Reddit analysis: 9/10 farm operators want bandwidth control, 6/10 want health monitoring).

2. **Lambert (Technical Analysis):** Mapped existing camera infrastructure. Finding: 80% already built (Camera entity, controller, UI). Only gap is PrinterId FK to link cameras to printers. Effort to complete: 11-16 hours (4 phases; Phase 1+2 delivers MVP in 6-9 hours).

## Decision

**Camera control reclassified to Phase 1.5.** Not blocked. Pairs with analytics dashboard. Implementation path clear. Competitive parity requires it.

## Next Steps

1. Merge research decisions into main decisions.md
2. Update sprint planning to prioritize Phase 1+2 (unify camera model, extend API)
3. Tech lead review of Lambert's migration strategy

---

**Research artifacts:**
- `.squad/decisions/inbox/brett-camera-research-revised.md` (market research)
- `.squad/decisions/inbox/lambert-camera-infrastructure.md` (technical analysis)
- `.squad/orchestration-log/2026-03-15T01-46-46Z-brett-camera-research.md`
- `.squad/orchestration-log/2026-03-15T01-46-46Z-lambert-camera-infrastructure.md`
