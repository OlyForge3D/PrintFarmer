---
name: "ralph-operations"
description: "Conditional operating policy for a single Ralph round."
domain: "work-monitor"
confidence: "high"
---

## Triage And Analysis

Scheduled instances first follow [the shared lifecycle](automation.md) and its
verified host profile. PR recovery is independent of the new-issue READY filter
below. The Windows-owned ledger remains the authority for its local and remote
jobs; an independent macOS controller must not initialize a second ledger and
claim cross-host coordination. Unknown remote ownership and shared-file overlap
retain the affected work. No new remote mobile dispatch occurs under the split
macOS-mobile/Windows-general profiles; reconcile existing remote jobs normally.

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
Do not re-dispatch a live analysis session. Windows never admits new mobile,
mixed or unknown-scope work, even with an enabled SSH adapter. The adapter is
retained only for existing-worker reconciliation, not new dispatch.

## Ready Queue

READY means open, exactly one valid owner, unassigned/unclaimed, non-epic, not in-progress,
not needs-analysis, and no live blocker. On Windows, mobile work is always
deferred to macOS. A trusted SSH configuration never overrides this prohibition.
Read GitHub native `blocked_by`/`blocking` edges as authoritative.
Resolve “blocked by”/dependency prose markers live as an additional decaying claim; either live
source blocks. Closed blockers do not.

Build the complete dependency graph before selecting READY candidates. Detect cycles, deduplicate
transitive descendants, inherit the highest downstream priority, then sort READY candidates by
effective p0–p3, unblock value descending, creation time, and issue number. Report non-mobile
critical-path work that unblocks macOS issues. Never newly dispatch mobile
dependents from Windows; reconcile historical remote workers by existing job ID.
Never use GitHub
labels/comments or the round cache as admission authorization, never fall back to local Windows,
and leave the reservation in place for offline, timeout, or uncorrelated acknowledgement results.

Re-fetch and confirm each issue immediately before claim/spawn. Enforce the
shared lifecycle's hard category quotas across implementation, analysis,
queued/reserved work and PR recovery: macOS 1 mobile + 4 general (5 total),
Windows 0 mobile + 5 general. Never borrow category slots. Run `capacity-check`
with complete fresh union inventory, and retain uncertain claims. Mac general
admission remains blocked pending an authenticated shared authority path; do not
mistake eligibility or spare capacity for duplicate-safe cross-host admission.
Use `gpt-5.6-terra` medium for implementation and
`gpt-5.6-luna` medium for non-code analysis unless an explicit premium justification exists.
Every implementation and Dallas/non-code analysis kickoff uses `create_session` with
`base_branch: development` in an isolated worktree. Every kickoff passes
`session-terminal-contract.md`. Implementation kickoffs also pass `implementation-pre-pr.md` and
task-specific acceptance criteria; analysis kickoffs state their exact non-code deliverable and
publication location. Before every spawn, perform this exact claim protocol: fresh eligibility
fetch; apply claim label and comment; re-fetch; verify that exact claim landed; then spawn. Abort
on any failed or stale claim.

Every Windows-controlled local or SSH implementation/analysis dispatch reserves the same PrintFarmer
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
   App-managed task completion without a process-exit result uses the separate
   `complete-local-session` route below; never synthesize an exit code.
5. `dispatch-remote` is retired for new work and fails closed. For a historical
   stranded `delivery-intent`, run `recover-remote` only after the lease expires and the
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

New Windows `reserve-local` eligibility must include `scope:"general"` and
`classificationComplete:true` backed by the current paths/labels/acceptance
criteria. Missing/unknown/mixed/mobile evidence rejects admission; mobile path
or acceptance signals cannot be relabelled general. Existing-session accounting
and terminal recovery remain available to retain historical ownership.

## Reconcile Before Admission

For existing general PR recovery on the Windows authority, use
[`reserve-local-pr`](pr-recovery-admission.md), not new-issue eligibility with
fabricated `linkedPr:false`. It uses the same ledger, CAS, capacity and conservative
acknowledgment/recovery lifecycle. Native macOS rounds continue their separate
verified claim protocol and must not initialize a substitute shared ledger.

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
The observation must postdate every terminal record for that session, not merely the named
predecessor; selecting an older predecessor cannot revive activity that subsequently ended.
Send that new job/fence to the existing session for its terminal report; do not resend its kickoff.
The operation atomically deduplicates sessions/issues and enforces five slots. If accounting fails
because the ledger is full, retain the untracked session in union capacity and block new dispatches
until reconciliation makes room. Never temporarily clear unresolved entries to fit a handoff.

For an accepted/running local job whose claimed session is authoritatively gone, use
`recover-local-session` with `jobId`, `sessionAbsent:true`, and `sessionEvidence` containing
`repository`, `issue`, `sessionId`, `fence`, `state:"absent"`, `observedAt`, `source`,
`liveInventoryChecked:true`, `archivedHistoryChecked:true`, and `terminalHistoryChecked:true`.
An absent inventory row is insufficient: inspect archived/terminal history and rule out ongoing
work. If verified terminal proof exists, use `terminal-local` or the supported app-session
completion route below instead. Unavailable history is a
blocker, not absence. Recovery records `abandoned`/`session-lost` and retains the session, fence,
digest and evidence; it is never success and does not authorize cleanup.

### App-Session Completion Without Process Exit

A persistent app host can record successful task completion without terminating its OS process.
`terminal-local` remains the process-result contract and requires a real integer `exitCode`.
Do not infer one from idle status, `task_complete`, a Git command or PR closure. Do not classify
an existing completed session as lost. Use `complete-local-session` for a verified code deliverable:

```json
{
  "expectedGeneration": 206,
  "result": {
    "jobId": "the-admitted-job",
    "fence": 198,
    "sessionId": "the-exact-app-session-uuid",
    "taskCompleteEventId": "runtime-event-uuid",
    "turnEndEventId": "runtime-event-uuid",
    "headSha": "full-40-character-delivered-head",
    "publicationRef": "refs/pull/2722/head",
    "workingTreeClean": true,
    "allCommitsPushed": true,
    "validationEvidence": {
      "headSha": "full-40-character-delivered-head",
      "passed": true,
      "source": "Exact-head test/CI result references independently checked by the controller"
    },
    "observation": {
      "repository": "OlyForge3D/PrintFarmer",
      "issue": 2720,
      "jobId": "the-admitted-job",
      "fence": 198,
      "sessionId": "the-exact-app-session-uuid",
      "observedAt": "fresh-UTC-timestamp",
      "source": "Exact fresh host app inventory/queue observation and sole-writer handoff",
      "running": false,
      "followUpPending": false,
      "journalSha256": "SHA256-of-current-events.jsonl-bytes",
      "runtimeHeadEventId": "last-runtime-event-uuid"
    }
  }
}
```

The controller reads only that UUID's `events.jsonl` from the local user's
`~/.copilot/session-state` root; the CLI does not accept a caller-supplied journal path or event
body. It requires the runtime's `session.start` identity, successful `session.task_complete`
and matching `assistant.turn_end`, after admission and every same-session terminal predecessor.
The selected root completion report must name that exact `jobId` and `fence`; chronology alone
must not accidentally attribute a later task's result to an earlier admission.
The journal multiplexes subagents: turns are tracked by the runtime envelope's `agentId`,
not a globally unique turn number. Selected completion/end events must belong to the root
session (no `agentId`); an embedded agent's successful task cannot finish the admitted parent.
Every tracked agent turn and hook must have ended before accounting completes.
Host `external_tool.completed` receipts are correlated to the existing
`external_tool.requested` by UUID `requestId`; their bridge-owned parent IDs can lie outside
the journal. Every other event must reference an already observed parent in the append-ordered
causal graph (native bridge events can branch rather than form one linear chain). All timestamps remain
ordered, and pending/unmatched external requests reject completion. These receipts never
substitute for root task/turn completion or excuse later activity.
Subsequent user/tool/turn activity, unknown event types or unfinished hooks retain accounting.
The publication ref must be an exact `refs/heads/...` or `refs/pull/N/head` on origin. The adapter
checks the runtime-recorded worktree's repository identity, admitted-base ancestry, HEAD,
clean Git status and exact remote ref. Feature branch deletion after merge is supported through
the retained PR head ref. Validation evidence must name that same HEAD; verify its actual
tests/acceptance criteria before attesting it. Neither PR closure nor task success alone is enough.

Acquire sole-writer ownership, read the journal fingerprint, then freshly observe the app's
running and queued/follow-up state without waking the child. Observation must be no more than
60 seconds old and at least as recent as the journal tail. Do not reuse an old app snapshot.
The adapter streams and validates the full runtime lifecycle and verifies Git outside the ledger
lock, then streams a hash-only journal recheck. It never materializes the whole journal as one
string. Under the short ledger lock it rechecks generation, identity, chronology, observation
freshness and the journal's filesystem identity/size/nanosecond modification metadata. Changed
or unavailable evidence fails explicitly without releasing a slot. Other admission operations
are not locked out during network or journal scanning. Refresh evidence before retrying.

**Trust and race boundary:** the journal is host-owned local runtime evidence, not authenticated
by caller prose, and not a cryptographic attestation against another process with the same OS
account's filesystem privileges. App inventory/queue and validation observations are the trusted
coordinator's attestations; this standalone CLI cannot query or lock the app host. Sole-writer
coordination must cover reconciliation and avoid steering the child during this short window.
The ledger lock does not freeze the app. Re-read live inventory after recording completion and
again before any admission: later/concurrent follow-up work still consumes effective-union
capacity until accounted as a new job/fence. Never treat a saved stopped observation as a lease
guaranteeing future inactivity. If host queue/activity cannot be established, retain a named
evidence blocker rather than declaring an OS exit or silently freeing capacity.

This writes `completed` with `sessionCompletion.kind:"app-session-task"` and immutable
event IDs/timestamps, journal fingerprint, publication and validation provenance. It never
inserts `exitCode`, deletes files, archives the session or authorizes further dispatch.
The old job ID remains fenced. An exact proof replay with the current ledger generation
returns the historical record without another write; it is **not** fresh liveness evidence
and cannot release or hide resumed work. Different proof for a terminal job is rejected.
All remote terminal/abandonment rules remain unchanged.

### Atomic Completed-Task To Resumed-Task Handoff

If the same app session has already started another explicitly assigned issue, ordinary
`complete-local-session` must reject its old completion. Do not release and re-reserve in two
steps, reuse the old fence for new work, or use a later task's completion as the earlier result.
With explicit authorization for the exact issue pair, use `handoff-local-session`. It commits
the old task's `completed` result and a new `accepted` job/fence with `predecessorJobId` in one
expected-generation transaction. Occupancy is unchanged, including when all five slots are
occupied. No momentarily free slot or session duplication is exposed.

Supply the same `result` identity/event/publication/validation fields, plus `successor`:
its canonical `job`, the same `sessionId`, `assignmentEventId`, `assignmentSource` (the exact
host-recorded `agent-<coordinator UUID>` source), `assignmentContentSha256`, `activityEventId`,
`deliveryEventId` and `deliveryContentSha256`. Assignment is a root user event containing the
new issue number, strictly after the old root turn ended with no pending turn/hook/request.
Activity must be the corresponding root turn start with the same interaction ID.
Any intervening new activity before that selected assignment rejects an ambiguous boundary.

`deliveryEventId` identifies an existing successful root tool receipt, before old completion,
whose exact content hash and delivered HEAD match the request. Independently examine its
recorded command and result for historical clean-worktree/validation evidence; a successful
tool wrapper alone does not prove those facts. The old root task summary must explicitly
attest clean worktree and pushed commits. The adapter verifies old published HEAD and base
ancestry live but does not require the now-resumed worktree to still equal the old HEAD or
be clean. New work is allowed to be dirty. Validation and historical clean-worktree meaning
remain trusted coordinator attestations tied to actual immutable runtime receipts, not
guessed from current state. Never rerun old commands or rewrite journals to manufacture proof.

The fresh `result.observation` binds the old `jobId`/`fence`/session, new `issue` and
`successorJobId`, selected `assignmentEventId`, `activeWork:true`, actual boolean `running`,
`currentAssignmentConfirmed:true` and the current `latestUserEventId`, alongside the same
source/timestamp/journal hash/tail fields. Inspect every later root instruction to confirm it
still concerns the authorized successor; if the user repurposed the session, stop and report
the different scope. A task-complete event after the new assignment rejects this active
handoff route; never invent active evidence for a successor that already ended.

Both old and new identities, runtime boundaries, provenance and digests persist. Existing
successor identifiers, active/stranded duplicate issues, different sessions, incomplete delivery,
stale observations, missing/failed/ambiguous boundaries or concurrent ledger/journal changes
reject without either partial write. Exact replay returns the historical linked pair without
changing generation or asserting present liveness. Later completion/resumption must still use
its own actual job identity; no remote or process-result semantics are changed.

### Reconciliation After Both Tasks Already Completed

An active handoff must not invent active work when the successor finished before accounting
caught up. For an explicitly authorized exact historical task pair, `complete-local-handoff`
records both completed tasks atomically. It is a separate command, not a fallback of
`handoff-local-session` or `complete-local-session`. The old occupied reservation is released
once, and the previously unaccounted successor gets a permanent **retrospective** job/fence
and predecessor link, directly in terminal state. No transient free/re-reserved slot or
fabricated prior reservation exists.

Use the atomic handoff request plus `successor.completion` containing its own
`taskCompleteEventId`, `turnEndEventId`, `deliveryEventId`, `deliveryContentSha256`, `headSha`,
`publicationRef` (`refs/pull/N/head`), `workingTreeClean:true`, `allCommitsPushed:true` and
exact-HEAD `validationEvidence`. These are the successor's existing runtime events, never
the predecessor's events. The root completion report must identify the new issue and attest
clean/pushed delivery; the separate successful delivery receipt must follow its assignment
and precede its completion. Its actual current clean HEAD, admitted-base ancestry and exact
remote publication are verified in addition to the predecessor's historical proof.

For this retrospective route, `successor.job` contains **only** `jobId`, `repository`,
`issue`, `owner`, `baseSha` and nonempty `acceptanceCriteria`. Model, effort, agent and
expected remote host were not necessarily assigned when the local session was reused;
do not guess them. They are absent from the immutable local job and its audit states
`dispatchMetadataRecorded:false`. Normal active admission and remote validators are unchanged.

Persistent sessions may have earlier successful delivery-ready or no-op review checkpoints.
Declare every relevant earlier root completion in chronological `priorTaskCompleteEventIds`
on `result` and, for the second task, `successor.completion`. The default empty list asserts
there were none. Each checkpoint must succeed inside its own paired root turn on the correct
side of assignment; failed, undeclared, missing, duplicate, overlapping or unordered
checkpoints reject. Earlier events before this admission belong to historical tasks, not
these lists. The adapter retains checkpoint IDs, timestamps, summary hashes and paired turn
ends in the evidence digest and replay audit. Checkpoints never replace final delivery or
terminal proof. Selected delivery receipts consume unique root tool starts; the successor's
start must occur after its own assignment/activity, not in the predecessor's task.

The observation names the authorized successor issue and latest root instruction but must
now assert `activeWork:false`, `running:false`, `followUpPending:false` with the same fresh
journal fingerprint and observation timing. All agent turns/hooks/external requests must have
ended. Later activity or another repurposing rejects the operation. Routine native
`session.shutdown` receipts may follow completion, but never supply or imply an exit code.
Never steer the child to create a new report just for accounting.

Native histories may also contain an interrupted runtime epoch during implementation.
A root routine shutdown immediately followed by root `session.resume` with
`sessionWasActive:false`, `alreadyInUse:false` and identical runtime cwd starts a new
turn namespace only when no hook or external request remains pending. The adapter records
these epoch boundaries and the number of interrupted turns; it does not call them successful
tasks or process exits. A later real successful root task/end and fresh no-follow-up evidence
are still mandatory. A matching inactive resume without a preceding shutdown does not clear
any pending lifecycle state. Resume after the selected final completion is new activity and rejects.

The successor records `sessionCompletion.kind:"app-session-task-retrospective"` and
`runtimeReportedAdmission:false`, its actual completion timestamps, and a separate
`accountedAt`. Its newly allocated audit fence was **not** present in the old runtime report:
do not manufacture or backdate that assertion. The prior task must still report its actual
old job/fence. Exact replay is historical/audit-only; changed proof and concurrent generation
changes fail closed. All other reservations and original retirement evidence remain untouched.
Use the same sole-writer, independent proof verification and after-write effective-union
checks as the other routes. This is two verified delivered tasks, not abandonment.

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
Deploying the updated `scripts/ci/ralph-macos-worker.mjs` requires separate authorization.
Identify the standalone JavaScript implementation invoked by the configured worker command;
preserve any shell wrapper that supplies runtime configuration. Back up the implementation,
stage and syntax/hash-check the replacement beside it, retain its permissions, and atomically
replace only that implementation. Preserve existing state/worktrees and runtime configuration.
No new dependency is required. Until then, exact-original-payload status requests
can recover existing old-worker records with correlated live/terminal evidence, but an old
worker's unfenced no-record response is not sufficient to release capacity.

### Stopped But Incomplete Remote Work

Exit zero does not prove delivery: a stopped job can retain dirty files or lack its exact pushed
branch. Normal `status-remote` continues to require complete success evidence. Do not rerun an
implementation merely to manufacture that evidence or free a slot.

When explicitly authorized to abandon incomplete work, use `abandon-incomplete-remote` with the
existing `jobId` (or exact original `job`); `legacyIdentity:true` is available for a missing payload
only on a compatible worker. This is a separate opt-in operation, never an automatic status fallback.
The controller rejects unsolicited abandonment on ordinary status or dispatch. If the worker
persisted abandonment but its reply was lost, retry the explicitly authorized abandonment
operation; normal status cannot import that outcome. A terminal result already recorded in
Windows remains readable without contacting the worker.
The worker must validate the original job/fence/digests and recorded successful process termination,
then inspect Git evidence and freshly prove the recorded supervisor/child PIDs and all job-fence,
launch-token and supervisor-token processes are absent, under the existing job lock. Alive, unknown,
missing or mismatched evidence blocks release. A fully delivered job must use normal status instead.

The worker durably records `abandoned` / `INCOMPLETE_DELIVERY`, with process timestamps, checked
cessation, incomplete Git evidence and `dispatchFenced:true`. Delayed dispatch and supervisor launch
are rejected. Preserve the entire prior worker record, process result, logs, dirty files and Git
state; no cleanup, reset, push, relaunch or issue-success claim is authorized. Windows records the
correlated attestation, never a caller-authored terminal claim. Repeated calls return the same
terminal result. This is not completed, delivered, merged or validated work.

Both abandonment request types require the updated standalone worker; unsupported old workers
retain the reservation. Rollback must retain all worker records and terminal tombstones. An older
worker/controller may reject new terminal types, but must not be made compatible by deleting
evidence or redispatching a terminal job. Replacing the worker implementation does not authorize
restarting existing supervisors or changing their runtime configuration.

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
