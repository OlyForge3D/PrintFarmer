---
id: dc61c598-3589-4a5d-9245-c0f7c52aa214
class: DECISION
loadGuidance: [ALWAYS]
title: "copilot-directive-2026-10-21-no-touch-optimization-ui-consistency"
author: "Jeff Papiez (via Copilot)"
createdAt: 2026-10-22T01:14:24.405Z
metadata: {}
---

### 2026-10-21T18:14-07:00: User directive
**By:** Jeff Papiez (via Copilot)
**What:** "we do not have to optimize the UI for touch". Printer UI (sidebar/detailed card) controls should match existing app conventions on the same surface rather than enforcing 44px touch targets. Related same-session directives: new sections must reuse existing CollapsibleSection styling and the same header/inner-content font size and style as existing groups (e.g. Statistics, Version) — consistency over bespoke design.
**Why:** User request — captured for team memory (PR #3460).
