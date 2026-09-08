---
name: "ralph-operations"
description: "Conditional operating policy for a single Ralph round."
domain: "work-monitor"
confidence: "high"
---

## Triage And Analysis

Every open issue receives exactly one bucket and every blocked report names its live blocker.
Triage missing ownership with one valid `squad:*` label, justified type/priority labels, and an
owner/first-step comment; remove bare `squad` when assigning. Never assign Ralph. Epics are never
implemented: enumerate native sub-issues plus `epic-child` labels, refresh one progress comment,
update the checklist, and close only when children and acceptance criteria are complete.

## Authoritative Label Vocabulary

Implementation/analysis owners are `squad:dallas`, `squad:ripley`, `squad:drake`,
`squad:lambert`, `squad:hudson`, `squad:gorman`, `squad:kane`, `squad:ash`,
`squad:brett`, `squad:parker`, `squad:newt`, and `squad:copilot`. The reviewers
`squad:bishop`, `squad:hicks`, and `squad:vasquez`, plus `squad:scribe` and `squad:ralph`, are
never dispatch owners. The bare `squad` label is a scope marker, not an owner.

The only type labels are `type:feature`, `type:bug`, `type:chore`, `type:docs`,
`type:spike`, and `type:epic`; the only priority labels are `priority:p0`, `priority:p1`,
`priority:p2`, and `priority:p3`. Apply exactly one justified owner and priority label; add one
justified type label when triaging.

Emoji-prefixed duplicate owner labels (for example `squad:⚛️ ripley`) are equivalent to their
plain form. Count both forms as the same owner, remove duplicate forms when safe, and apply only
the plain form to new claims. An emoji/plain pair is one owner, not an ownership conflict.

For a non-mobile epic needing decomposition, unmet architecture gate, or under-specified issue,
apply `status:needs-analysis` and dispatch Dallas for child issues or an issue sign-off—not code.
Do not re-dispatch a live analysis session. Windows only triages mobile work; it never dispatches,
reviews, or merges it.

## Ready Queue

READY means open, exactly one valid owner, unassigned/unclaimed, non-epic, not in-progress,
not needs-analysis, non-mobile on Windows, and no live blocker. Read GitHub native
`blocked_by`/`blocking` edges as authoritative. Resolve “blocked by”/dependency prose markers
live as an additional decaying claim; either live source blocks. Closed blockers do not.

Build the complete dependency graph before selecting READY candidates. Detect cycles, deduplicate
transitive descendants, inherit the highest downstream priority, then sort READY candidates by
effective p0–p3, unblock value descending, creation time, and issue number. Report non-mobile
critical-path work that unblocks macOS issues; never dispatch the mobile dependents.

Re-fetch and confirm each issue immediately before claim/spawn. Maintain at most five live
implementation/analysis sessions. Use `gpt-5.6-terra` medium for implementation and
`gpt-5.6-luna` medium for non-code analysis unless an explicit premium justification exists.
Every implementation and Dallas/non-code analysis kickoff uses `create_session` with
`base_branch: development` in an isolated worktree. Every kickoff passes
`session-terminal-contract.md`. Implementation kickoffs also pass `implementation-pre-pr.md` and
task-specific acceptance criteria; analysis kickoffs state their exact non-code deliverable and
publication location. Before every spawn, perform this exact claim protocol: fresh eligibility
fetch; apply claim label and comment; re-fetch; verify that exact claim landed; then spawn. Abort
on any failed or stale claim.

## Round Report

Report triage, every accounting bucket, epic/analysis status, dispatch order and blockers,
cross-platform deferrals, PR gates, active slots, and the cleanup section from `cleanup.md`.
Finish the report and exit; do not poll or begin another round.
