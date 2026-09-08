---
name: "ralph-cleanup"
description: "Report-only cleanup-candidate assessment for Ralph's existing round."
domain: "work-monitor"
confidence: "high"
---

## Candidate Assessment

There is one Ralph automation, not a reaper. Reuse the round's session inventory, PR results, and
fresh remote evidence. Call `assessCleanupCandidate`; it only classifies and never authorizes an
action. Fail closed for active/unknown activity (age alone is insufficient), unknown or dirty
tracked/untracked worktrees, missing clean/pushed attestations, unsettled terminal state, or
unknown post-merge history.

A merged PR requires its merge commit verified on `origin/development`, proof that current HEAD
preserves that merge result, and no known commits after merge. A closed-unmerged PR additionally
requires the explicit `CLOSED WITHOUT MERGE` report, closure reason, and verified linked-issue
disposition. A no-PR session requires a completed, verified deliverable. Reuse the session's own
remote branch comparison; squash merge does not prove feature-head containment.

## Report Only

Always report `Sessions retained` and `🧹 Ready to reap`, including empty headings. Never invoke
`archive_session` or `delete_item`; `archive_session` cannot archive earlier-round children.
Ralph's only follow-up is a confirmed-action handoff: after a human explicitly confirms each exact
candidate session name, report those names for the human to act on. Ralph still never invokes a
deletion. Only that human may later use `delete_item`; unrelated audit/UI automations are out of
scope.
