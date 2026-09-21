---
name: "ralph-cross-host-automation"
description: "Shared one-shot lifecycle for macOS mobile and Windows general Ralph automations."
---

## Authority And Preconditions

Do **one round and exit**. No sleep, polling, waiting for CI/owners, self-scheduling,
implementation, builds, tests, worktree edits or cleanup. Use the current isolated
automation worktree; never read or mutate the main checkout. Fetching origin in
this worktree is allowed. Report stale local base refs; do not rewrite them.
All implementation and conflict fixes belong to isolated owning sessions.

The saved automation contains only [bootstrap.md](bootstrap.md)'s invocation and
explicit host bindings. Require its approved policy commit and successful
`ralph-automation.mjs preflight`. Missing, unmerged, changed, stale or unverified
policy/host configuration means **report the configuration blocker and exit**.
Do not install, edit, downgrade or fetch a different runtime to bypass it.
Compare the private host configuration with the actual workflow/project/host
from native tools. `verified:true` is a deployment attestation, not discovery.

This document governs both instances; [hosts.json](hosts.json) supplies only
scope/capabilities/limits/overrides. Machine paths and SSH configuration remain
private on the owning host. No schedules are enabled by this policy.
`macos-mobile` owns mobile/iOS; `windows-general` owns general work. Classify from
paths, labels and acceptance criteria, never member identity. Windows must not
start new remote mobile jobs under this split. It must still reconcile historical
remote jobs through the existing shared authority. The disabled/unverified
Windows profile is not permission to assume Windows workers have stopped.

Use [operations.md](operations.md) for vocabulary, dependencies and the existing
admission/terminal APIs; [pr-merge.md](pr-merge.md) for trusted merge gates;
[session-terminal-contract.md](session-terminal-contract.md) for delivery;
[cleanup.md](cleanup.md) for report-only retention. This policy supersedes legacy
issue-first ordering and machine-local copies, not stronger safety checks.
Preserve profile-specific explicit kickoff clauses and model overrides. Report
unresolved conflicts instead of selecting a convenient weaker rule.

## Inventory And Ownership Before Action

1. Establish one active controller for this host. Reuse native live inventory,
   queued work and archived/terminal history. A busy, waiting, queued or
   uncertain controller blocks a second round's mutations. Do not wake it merely
   for bookkeeping. Coordinate with designated repair sessions before acting.
2. Collect **all open PRs, including drafts**, and all issues with complete
   pagination, independently of parent assignment, blocked or analysis labels.
   Reuse `ralph-github-snapshot.mjs` once with this workflow and policy version.
   Its coverage gaps require live reads; unchanged is not completed. On failure,
   one complete manual paginated scan is allowed; incomplete evidence never
   means an empty board. Never use old machine-local scanners or snapshots as
   authority. Live CodeQL/required checks remain mandatory.
3. Read current PR head, changed files (including previous paths for renames),
   linked issue/parent identities, explicit holds, claims and owner session.
   Fetch files completely; compare the count with GitHub's `changed_files`.
   Unknown/truncated files reserve unknown scope, not an empty file set.
   Fetch current reviewer findings/check URLs only for affected work.
4. Reconcile **all** existing claims before new admission. On Windows use the
   existing platform-default five-slot ledger and JSON-stdin admission commands,
   never an independently initialized ledger elsewhere. Reconcile remote jobs
   by existing job ID with the original digest/fence/host binding; offline,
   unavailable, ambiguous or old-worker responses retain the claim. Do not
   clear remote ownership because it is missing from local App inventory.
   On macOS reconcile native local claims and the Mac worker's existing records;
   a Windows-owned job is not a native local claim. If its owning authority is
   unavailable, retain and report that specific ownership blocker.
5. Capacity is the distinct union of reservations and live/resumed handoffs,
   including analysis and untracked work. Apply the configured local ceiling
   and existing shared five-slot ceiling where used. Free shared slots do not
   prove the Mac/Xcode host is idle. Keep one Xcode job on the Mac. Never erase
   uncertainty, historical records or tombstones to make space.

Idle is not dead. A label, comment, session creation response, elapsed interval or
controller exit is not evidence of worker cessation. For known local sessions,
inspect current activity, queued delivery, archived and terminal history and
actual work/branch evidence. Use existing supported completion/recovery routes,
not invented exit codes. `recover-local` still requires absence, expired
reservation and dead controller; `recover-local-session` requires correlated
live/archive/terminal absence. These predicates never apply to a different host.
Explicit abandonment and atomic cross-task handoffs still need their separate
user authorization. A prior approval for historical work does not authorize it.

## PR-First Recovery And Conflict Admission

Recovery precedes new slices: **current-head requested changes, CI failures,
missing/stale review, approved draft integration**, then new issue work.
A blocked parent does not block repair of its existing PR. Explicit PR holds
and verified real implementation prerequisites still apply.

Run `verify-squad-verdict.mjs` at the live head. Only its authenticated output is
review evidence. A stale rejection becomes missing-current-review work, not a
current rejection. `APPROVED` owner precedence is unchanged; retain overridden
findings as overridden, not resolved. Raw `reviewDecision` is not a second veto.
Read findings from the corresponding accepted records; never promote arbitrary
comment prose into authority. Failures are not passing evidence.

Use `node scripts/ci/ralph-automation.mjs plan < observations.json` with the
fresh normalized inputs described in [bootstrap.md](bootstrap.md). It selects
non-overlapping local recovery candidates and explicitly retains live, unknown
and other-host ownership. It is **observation-only**, not a claim, dispatch or
merge authorization. Refresh the affected live facts before each actual action.

Reserve the full changed-file set for each unresolved owning PR. In particular,
`test-typecheck-baseline.json`, shared manifests and generated exact-count files
are integration conflicts even across nominally different issue areas. Only one
owner may revise/sync/integrate a conflicting set at a time; unrelated file sets
may proceed concurrently. Recompute after every head change and verified merge.
Unknown remote owner or overlapping files on the other host means **hold those
conflicting PRs and escalate for a verified sole-owner handoff**. Do not start a
second repair or assume disjoint host roles guarantee disjoint files.

There is no new distributed lock service. The Windows admission ledger already
coordinates its local and remote jobs; this policy does not make it accessible
to an independent macOS App controller. Native macOS work stays in its own
verified scope. Cross-host overlap cannot be automatically admitted until the
existing authority and both owners are reconciled. Never create a Git ref,
comment-based lock or second ledger to bypass that boundary. New-issue work
with unknown/overlapping intended files must likewise wait or get a scoped
analysis handoff; do not evade a recovery hold by opening another slice.

## Real Handoff, Not A Notification

Prefer the existing implementation session, preserving its PR branch and work.
For a live owner, deliver only new unhandled findings using its exact session ID;
do not create a duplicate or resend an unchanged request every round. If inactive
but resumable, resume that same session with the exact PR/head/findings. For a
genuinely absent owner, reconcile its claim first and open an isolated session on
the existing PR through the supported native PR-session tool, not a fresh
development branch masquerading as a repair. An explicit reviewer lockout must
route to a different eligible implementer.

Before any creation/resumption: re-fetch the PR/head, issues, newest comments,
remote branches, ownership, queue, file conflicts and capacity. On Windows the
existing issue reservation API rejects `linkedPr:true`; **do not lie by passing
false** to reserve recovery. Reuse an already admitted owning session when
supported, or report `PR recovery admission unsupported` until the authority can
account the exact handoff. `account-local-session` can account an already live
handoff; it does not authorize creating one without reservation.
On native macOS follow its existing claim-before-create protocol after proving
sole local ownership and no remote conflict.

Each durable `<!-- ralph-pr-handoff -->` record on the PR must contain:
repository, PR number, exact starting head, task kind, finding IDs and evidence
URLs/check names, owning host, actual session ID, job/fence if admitted, affected
files, delivery receipt, and observation proving kickoff processing. Treat
comments as discoverable audit references, never an atomic lock or liveness proof.
Append dispositions; never erase a predecessor or stale claim to hide it.

A created session is not a started session. Confirm native busy/waiting state or
a recorded turn corresponding to that exact delivery. If delivery is unconfirmed,
one resend of the exact kickoff is permitted; do not poll or sleep. If still
unconfirmed, retain a pending/stranded delivery and report it, not “sent back” or
“dispatched.” On Windows use existing `acknowledge-local`/`fail-local-kickoff`
semantics and stranded-session fencing. Record accepted handoff only after real
processing; include the same identity in the worker's terminal contract.

## Review, Integration And Dependencies

Drafts are scanned and repaired, not silently skipped. An approved draft goes
to its owning implementer for final acceptance/current-head CI and readiness;
do not mark ready because an older head passed. Reviewers are read-only task
agents: no builds, tests, installs, linters, scripts or tracking files.
macOS may commission required mobile reviews; Windows routes review gaps to the
owning implementer's pre-PR process. Both use the canonical risk-based count,
distinct lenses, delta-only rereview, exact-head records, and owner precedence.

Serialize all merges. Require bare `squad`, scope, non-draft, no explicit hold,
mergeability, required CI/CodeQL and verified current-head authorization; use
`--match-head-commit` on squash merge. `BEHIND` alone is not a reason to rewrite
a green PR. Verify landing and actual linked-issue closure before the next.

After each merge/closure, **re-fetch the native dependency edges and their actual
issue states**, plus named textual/external prerequisites. Recompute dependent
readiness, analysis gates and epic progress. Remove a stale `status:blocked` only
when every live blocking reason is demonstrably resolved; retain manual holds,
unknown lookups and unmet acceptance/evidence. A merged PR is not proof all
linked blockers closed. Do not blindly clear a parent or sibling's labels.
This same reconciliation happens each round even when a prior webhook was missed.

## New Work And Exit

Only after recovery dispositions are established, use the dependency-ready queue
and existing claim/reservation protocol for eligible new work. Do not dispatch
epics or invent missing Windows configuration. Respect human assignments; Squad
ownership is a cast label plus a real job/session, not a human proxy. All kickoffs
name the cast agent, task, explicit configured model/effort, targeted validation,
current-head review and terminal contracts. macOS retains its Dallas override
and both unchanged [macos-kickoff.md](macos-kickoff.md) clauses.

Report each issue/PR exactly once: accepted handoff with host/session/head,
in-flight, held with concrete blocker, cross-host pending acceptance, or
unaccounted. Distinguish plan candidates from accepted delivery. Include
before/after ledger and effective union counts, dependency changes, review/CI
SHA evidence, retained claims, configuration limitations, remaining backlog,
`Sessions retained`, `🧹 Ready to reap` and `⚠️ Unpushed work`.
Cleanup is report-only; never archive/delete. Never declare the board clear with
unknown ownership or incomplete coverage. Exit; the next schedule is the next round.
