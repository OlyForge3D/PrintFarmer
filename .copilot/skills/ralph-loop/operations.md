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
Do not re-dispatch a live analysis session. Windows only dispatches mobile work through the
enabled verified SSH adapter; it never performs mobile work, reviews, or merges it locally.

## Ready Queue

READY means open, exactly one valid owner, unassigned/unclaimed, non-epic, not in-progress,
not needs-analysis, and no live blocker. On Windows, mobile work is READY only when the verified
SSH adapter is explicitly enabled and its trusted configuration/readiness checks pass; otherwise
it remains deferred to macOS. Read GitHub native `blocked_by`/`blocking` edges as authoritative.
Resolve “blocked by”/dependency prose markers live as an additional decaying claim; either live
source blocks. Closed blockers do not.

Build the complete dependency graph before selecting READY candidates. Detect cycles, deduplicate
transitive descendants, inherit the highest downstream priority, then sort READY candidates by
effective p0–p3, unblock value descending, creation time, and issue number. Report non-mobile
critical-path work that unblocks macOS issues. A verified, explicitly enabled SSH adapter may
dispatch a mobile dependent only after it reserves the issue in the shared Windows-owned
PrintFarmer ledger; legacy Mac Ralph admission must be drained before activation. Never use GitHub
labels/comments or the round cache as admission authorization, never fall back to local Windows,
and leave the reservation in place for offline, timeout, or uncorrelated acknowledgement results.

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

Every enabled local or SSH implementation/analysis dispatch reserves the same PrintFarmer
admission ledger before delivery through `scripts/ci/ralph-admission.mjs`. The scheduled Ralph
prompt must use only these one-shot JSON-stdin commands—never a naked `create_session` or SSH
delivery:

1. After the fresh claim re-fetch, run `node scripts/ci/ralph-admission.mjs reserve-local` with
   `{"job":...,"eligibility":...,"controllerPid":...}` on stdin, using the app Ralph controller's
   own process ID—not the one-shot command's PID. Preserve the returned `jobId` and `fence` in the
   `create_session` kickoff as the stable job marker.
2. Create the local app session only after `reserve-local` succeeds. If creation times out or
   returns no session ID, retain the reservation; a later round must discover the marker in the
   session inventory and run `acknowledge-local`, never create a duplicate. If authoritative
   inventory proves no matching session exists, the reservation lease has expired, and its
   controller PID is dead, run `recover-local` with `{"jobId":...,"sessionAbsent":true}`.
3. A created session is not a started session. After creation returns a session ID, wait a brief
   grace window, then confirm from `get_sessions_status` that the session is busy, awaiting input,
   or awaiting plan approval, or that the session store records at least one turn for that session
   ID. Idle with no recorded turn means the kickoff was never received: resend the exact kickoff
   once with `send_session_message`, wait the same grace window, and re-confirm. Never acknowledge
   a session whose processing was not observed.
4. Once the app returns the real session ID and that session's kickoff processing is confirmed, run
   `acknowledge-local` with
   `{"jobId":...,"sessionId":...,"kickoffVerified":true,"kickoffRetried":...}`, where
   `kickoffRetried` is true only when the kickoff had to be resent. If processing is still
   unconfirmed after the single resend, run `fail-local-kickoff` with
   `{"jobId":...,"sessionId":...,"controllerPid":...,"kickoffUnverified":true}` instead of
   acknowledging, passing the calling controller's own process ID — the reserving controller
   releases its own reservation, while a later controller may release one only after confirming the
   recorded owner PID is dead and its lease has expired. That releases the reservation to terminal
   `kickoff-unverified` failure and records the stranded session as `strandedSessionId`. The
   stranded session is never archived, deleted, or cleaned up, and its issue claim stays in place:
   leave the claim, report the issue and stranded session, and never re-dispatch that issue while
   the stranded session exists. The ledger enforces this — reserving that issue again fails with
   `STRANDED_SESSION` until `clear-stranded-kickoff` with `{"jobId":...,"sessionAbsent":true}`
   proves the stranded session is gone. A `kickoff-unverified` failure never makes that claim stale,
   so the claim-reconciliation rule for terminal ledger states does not apply to it. On terminal completion, run `terminal-local` with the matching
   session ID and verified head, exit, validation, clean-worktree, and pushed-commit evidence.
5. For an eligible mobile issue only, run `dispatch-remote` with
   `{"job":...,"eligibility":...,"controllerPid":...}` using the app Ralph controller's own
   process ID instead of local session creation. It reserves, records a PID-and-lease-fenced
   intent, and sends SSH in one durable operation; lost acknowledgement/timeouts remain reserved.
   For a stranded `delivery-intent`, run `recover-remote` only after the lease expires and the
   owning controller is demonstrably dead. This changes it to `uncertain`, not terminal.
   For an `accepted`, `running`, or `uncertain` remote job, run `status-remote` with
   `{"jobId":"the-existing-job"}`. New reservations persist their exact immutable wire job.
   Status recovery never requires fresh issue eligibility, re-claiming a closed issue, or
   `dispatch-remote`. That command queries
   the trusted worker and releases the reservation only from its fence-bound process/Git terminal
   attestation, correlated pre-launch failure, or explicit attestation that no durable worker
   record exists for an uncertain delivery. Successful terminal evidence must bind the configured
   origin, admitted base ancestry, clean worktree, and exact pushed branch. Never submit
   caller-authored remote terminal claims. If a supervisor is lost, reconciliation discovers the
   child by its unguessable launch token and retains the slot while that exact process is alive;
   only the trusted worker may emit `SUPERVISOR_LOST` after the launch lease and fenced process
   have both ended.

## Reconcile Before Admission

Only one round or explicitly designated repair session may own operational reconciliation.
Check for another active round before writing the shared ledger; agree ownership rather than
racing it. A user-requested admission repair is maintenance, not a backlog implementation slot.
Do not launch extra implementation sessions to perform the repair.

The ledger records cooperating Ralph dispatches, but cannot observe arbitrary app sessions.
Before admitting anything, compare all active ledger entries with fresh live app inventory,
archived session history and terminal evidence. Report both the ledger count and the distinct
union of unresolved reservations and live implementation/analysis handoffs. Deduplicate by job
and session identity, not issue title. Idle alone is not absent or terminal. Count live work
whose old ledger entry is terminal; never resurrect that completed entry or ignore the session.

Account an existing local handoff using `account-local-session` with `{"job":...,"sessionEvidence":...}`.
For a coordinated repair, also pass `expectedGeneration` from the freshly inspected ledger:
the locked write rejects changed generations with `STALE_LEDGER`; refresh all evidence before retrying.
This does not spawn, claim an issue, or require new-issue eligibility. Supply a new stable job ID,
the observed repository/issue/owner/base and current non-secret work criteria. Evidence contains
`repository`, `issue`, `sessionId`, `state:"active"`, `observedAt` and `source` (the exact app
inventory/history observation reference). Evidence must be at most five minutes old.
For resumed work in a previously terminal session, also supply `previousJobId` and
`resumedAfterTerminal:true` in evidence, backed by a turn/work observation after that terminal
record. The old audit remains unchanged; the new job/fence accounts the resumed work.
A session can resume on a different issue: use the actual current issue from fresh GitHub and
session evidence, not the historical session title. Cross-issue linkage still requires the
exact same session ID as the terminal predecessor and verified later work; it never authorizes
linking an unrelated session or duplicating an active session/issue.
Send that new job/fence to the existing session for its terminal report; do not resend its kickoff.
The operation atomically deduplicates sessions/issues and enforces five slots. If accounting fails
because the ledger is full, retain the untracked session in union capacity and block new dispatches
until reconciliation makes room. Never temporarily clear unresolved entries to fit a handoff.

For an accepted/running local job whose claimed session is authoritatively gone, use
`recover-local-session` with `jobId`, `sessionAbsent:true`, and `sessionEvidence` containing
`repository`, `issue`, `sessionId`, `fence`, `state:"absent"`, `observedAt`, `source`,
`liveInventoryChecked:true`, `archivedHistoryChecked:true`, and `terminalHistoryChecked:true`.
An absent inventory row is insufficient: inspect archived/terminal history and rule out ongoing
work. If verified terminal proof exists, use `terminal-local` instead. Unavailable history is a
blocker, not absence. Recovery records `abandoned`/`session-lost` and retains the session, fence,
digest and evidence; it is never success and does not authorize cleanup.

### Legacy Remote Records

Older reservations stored only a digest. `LEGACY_PAYLOAD_REQUIRED` explicitly retains the slot:
provide `status-remote` with the exact original `job` from a recorded dispatch artifact or trusted
worker record. A supplied fence must match; every digest-bound field must match byte-for-byte
(including criterion order and charter). Verified originals are persisted for future recovery.
Never guess criteria, substitute current issue text, fabricate fresh eligibility, or clear a slot
because the issue/PR closed or a Windows controller PID died.

If the original payload is unavailable, `status-remote` accepts
`{"jobId":"the-existing-job","legacyIdentity":true}` only through a compatible trusted worker.
This sends the ledger identity, fence and admission digest via `reconcile-ledger`, not a dispatch.
The worker validates its original full-wire digest, recomputes the admission digest, and binds
repository, issue, owner, base, host and any known session before inspecting process/Git evidence.
The response must echo the admission digest. Live or orphaned processes retain the slot.
No-record failure requires no residual worktree/process and a known admitted host; it cannot
prove a previously accepted session ended. Missing historical host can be recovered only from
an existing worker record whose recomputed digest matches. Preserve all other uncertainty.
Before attesting no-record failure, the worker atomically persists a terminal absence tombstone
under its job lock. A delayed original dispatch cannot start that job afterward. Require
`dispatchFenced:true`; older unfenced no-record responses retain the slot.

Old workers reject `reconcile-ledger`; SSH errors, unsupported requests, missing digest responses
and timeouts are explicit recovery blockers, never absence. Do not fall back to dispatch.
Deploying the updated `scripts/ci/ralph-macos-worker.mjs` to the configured trusted worker path
requires separate authorization and must preserve its existing state/worktrees and runtime
configuration. No new dependency is required. Until then, exact-original-payload status requests
can recover existing old-worker records with correlated live/terminal evidence, but an old
worker's unfenced no-record response is not sufficient to release capacity.

Persist only allowlisted job fields and observation references. Never put credentials, SSH
configuration, environment contents or secrets in criteria, charter, evidence or ledger data.
Before enabling remote dispatch, drain or account for legacy Mac Ralph admission so Windows is
the single coordinator. A round may resume filling slots only when the reconciled union is below
five and every live handoff is accounted; the per-Mac Xcode gate still applies.

## Round Report

Report triage, every accounting bucket, epic/analysis status, dispatch order and blockers,
cross-platform deferrals, PR gates, active slots, and the cleanup section from `cleanup.md`.
Name every resent kickoff and every `kickoff-unverified` release with its issue and
`strandedSessionId` under dispatch order and blockers.
Include exact before/after ledger and union counts, every recovered/retained job and its evidence
or blocker, and any resumed handoff's old/new job IDs. Distinguish failed/abandoned from completed.
Finish the report and exit; do not poll or begin another round.
