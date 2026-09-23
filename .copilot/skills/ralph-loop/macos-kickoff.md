---
name: "ralph-macos-kickoff"
description: "Preserved owner-approved macOS kickoff clauses."
---

## Applicability

Include both clauses below verbatim in every macOS-mobile implementation or analysis
kickoff. For analysis, the first is conditional and does not authorize code or a PR.
Put any extra lifecycle requirements before the closing clause; keep it last.
Use the **merge** option in the sync clause, consistent with the repository's
current no-rebase policy. Do not resync a green PR merely for `BEHIND`.

After the coordinator settles your assignment, the owning consumer may delete this
session and its worktree through its journaled cleanup (see `cleanup.md`), once the
worktree is clean, every commit is pushed and any PR is merged or closed with its
branch retained. That is why pushing first matters. The worker never archives or
deletes any session itself.

## Verbatim sync clause

BEFORE YOU OPEN YOUR PULL REQUEST, SYNC TO THE CURRENT BASE. Run `git fetch
origin`, merge or rebase `origin/development` into your branch, resolve any
conflicts, and re-run your targeted validation on the merged result. Do this
BEFORE the PR exists, when it is free — no CI has run yet, so nothing is
invalidated. This is not about unblocking a merge: this repository allows a
`BEHIND` PR to merge. It is about correctness — validating against a stale base
means your green checks describe a combination that no longer exists, and two PRs
that each pass independently can still break once combined. If your PR later shows
as `BEHIND` with green checks, do NOT rebase on your own initiative: that discards
those checks for no gain here. Report it and let Ralph decide.

## Verbatim closing clause

When your work is complete — PR merged or definitively closed, or for analysis
work your deliverable recorded on the issue — PUSH YOUR BRANCH FIRST if you have
any commits, then report your final status as your last action and stop. Never
leave committed work unpushed: an unpushed local branch is invisible on GitHub
and will be lost when the worktree is reaped. Do NOT attempt to archive yourself
— the runtime refuses `archive_session` on the current session and the call will
fail. Do not attempt to archive any other session either. Cleanup is handled by
Ralph's `🧹 Ready to reap` report.
