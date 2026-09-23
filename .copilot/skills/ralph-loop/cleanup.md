---
name: "ralph-cleanup"
description: "Cleanup assessment for Ralph's round, and owning-consumer deletion of its own settled workers."
domain: "work-monitor"
confidence: "high"
---

## Candidate Assessment

There are two host-scoped Ralph instances, neither a general reaper. Reuse the round's session
inventory, PR results, and fresh remote evidence. `assessCleanupCandidate` only classifies and
never authorizes an action by itself. Fail closed for active/unknown activity (age alone is
insufficient), unknown or dirty tracked/untracked worktrees, missing clean/pushed evidence,
unsettled terminal state, or unknown post-merge history.

A merged PR requires its merge commit verified on `origin/development`, proof that current HEAD
preserves that merge result, and no known commits after merge. A closed-unmerged PR additionally
requires the explicit `CLOSED WITHOUT MERGE` report, closure reason, and a retained
`origin/<branch>`. A no-PR session requires a completed, verified deliverable. Reuse the
session's own remote branch comparison; squash merge does not prove feature-head containment.

## Owning-Consumer Deletion Of Settled Workers

This is the only automatic deletion Ralph performs. It is a standing, narrowly scoped maintainer
authorization delivered through the personally approved policy package, not a per-session
confirmation, and it covers only the macOS consumer's own mapped workers. `archive_session`
cannot archive earlier-round children (the creator run is gone before settlement), so Ralph never
calls it. The coordinator never deletes. Windows consumers stay report-only until a separate
follow-up. The native role contract in `native-roles.md` defines the exact request fields.

A mapped worker is eligible only when the runtime's read-only `cleanup-plan` returns it:

- **Owned and settled.** Its journal mapping belongs to this consumer's worker ID, the mailbox
  assignment is coordinator-settled (`terminal`), and the retained local evidence digest equals the
  correlated `terminal-reported` receipt and terminal commitment. The runtime keeps that proof in
  the journal before any delete; deletion never clears or blocks readiness by itself.
- **Ceased.** The same-worker final ACK already proved no children, continuation, or future
  delivery. A fresh `get_session` shows the worker is not busy and has no pending input, active
  Agent merge, or attached automation. Unknown is not false.
- **Clean.** The recorded worktree exists, contains `.git`, has empty `git status --porcelain`
  (tracked and untracked), and has no unpushed commits.
- **PR state** (from `gh pr list --head <branch> --state all`), with a 15-minute settling time
  measured from the later of the terminal receipt and the PR merge/close:
  - Merged: merge commit contained in `origin/development`, HEAD preserved, no commits after merge.
  - Closed-unmerged: `origin/<branch>` exists, nothing unpushed, closure reason recorded.
  - Open PR: never deleted.
  - No PR with zero commits ahead of `origin/development`: research/analysis only, after the
    runtime re-reads the durable issue-comment artifact (and it matches any retained artifact).
  - No PR with pushed commits: human review only. With unpushed commits: retained with a warning.

Never eligible: role sessions and any session named `Ralph…`/`Reaper…`, the main checkout, the
calling session, unmapped or unrelated maintainer sessions, other workers' or hosts' sessions, and
non-terminal assignments. At most five deletions per round, oldest settled first.

For each eligible item, in plan order: `record-deletion-intent` journals the intent and returns
the single `delete_item` call; make exactly that call; then read back `get_session` for the mapped
session ID and every returned alias (for example the `project_session_id`) and check the worktree
directory; submit those facts with `record-deletion-result`. The deletion is recorded only when
every identifier is not found and the worktree is absent. A lost, failed, or unconfirmed result
stays pending and is inspected again on later rounds; never call `delete_item` twice for the same
intent. Once recorded, the runtime retires that mapping from later readiness without live
evidence, and any reappearance under a known identifier fails closed.

## Report

Always report `Sessions retained` and `🧹 Ready to reap`, including empty headings. List every
retained or pending worker with its reasons and every deletion result. For sessions outside the
owning-consumer rule, the only follow-up is a confirmed-action handoff: after a human
explicitly confirms each exact candidate session name, report those names for the human. Ralph
never invokes a deletion for them; only that human may later use `delete_item`. Porting the old
Session Reaper for non-Ralph sessions is a separate follow-up, and unrelated audit/UI automations
are out of scope.
