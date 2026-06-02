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

