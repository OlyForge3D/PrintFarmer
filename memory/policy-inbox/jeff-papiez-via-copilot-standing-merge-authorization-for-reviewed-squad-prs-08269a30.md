---
id: 08269a30-4fcd-49b5-b23b-271ab3a1ed27
class: POLICY
loadGuidance: [ALWAYS]
title: "Standing merge authorization for reviewed squad PRs"
author: "Jeff Papiez (via Copilot)"
createdAt: 2026-10-16T21:12:42.702Z
metadata: {}
---

### 2026-10-16T14:12-07:00: User directive
**By:** Jeff Papiez (via Copilot)
**What:** Do not ask for merge permission. For every squad PR, once the required review gate passes at the current head (verify-squad-verdict REVIEWED/APPROVED, no active rejection) and all required CI is green, mark it ready and merge with `gh pr merge <n> --squash --match-head-commit <reviewedHeadSha>`. This applies every time.
**Still applies:** any head movement after review needs fresh current-head reviews (delta-only); failed or pending CI blocks merging; the review gate is never bypassed.
**Why:** User request. The coordinator kept asking for authorization that was already implied.
