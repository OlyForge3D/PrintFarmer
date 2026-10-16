### 2026-10-16T21-01-28: Standing authorization: auto-ready and merge squad PRs once review gate and CI are green
**By:** Squad (Coordinator)
**What:** Standing authorization: auto-ready and merge squad PRs once review gate and CI are green
**Why:** ### 2026-10-16T14:01-07:00: User directive
**By:** Jeff Papiez (via Copilot)
**What:** "Of course I want you to mark 3394 ready and merged once ci is green. I want you to do that every single time!" — For every squad PR, once the squad/pre-pr-verdict gate passes at the current head (verify-squad-verdict REVIEWED/APPROVED) and all required CI is green, mark the PR ready and merge it with `--match-head-commit <reviewedHeadSha>`. Do not ask the user for merge authorization.
**Still required (unchanged):** current-head review evidence, no unresolved REQUEST_CHANGES, green required CI. Never merge on red/pending CI or a stale verdict.
**Why:** User request — repeated merge-authorization prompts were unwanted friction.