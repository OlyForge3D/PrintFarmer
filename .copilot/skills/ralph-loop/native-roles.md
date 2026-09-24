---
name: ralph-native-mailbox-roles
description: Sole mini coordinator and native local consumers using a private GitHub queue.
---

## Binding And Trust

NATIVE-MAILBOX-ROLE-V2. This contract replaces legacy independent host dispatch
**only for explicitly attested native role packages**. Never combine the old
dispatcher with this role. Run one round and exit, no sleep or self-scheduling.
No SSH workers, CLI workers or cross-host native session creation.

Use the saved package's single approved commit for all origin, fetched/current
controlled-policy, ancestry and untracked-file checks in
[bootstrap.md](bootstrap.md). Do not execute helpers before those checks pass.
The native runtime also calls the existing non-authorizing filesystem preflight.
The maintainer explicitly accepts `executionTrust:"local-owner-v1"`: the local
owner/config and approved shared GitHub writers are trusted. Workflow, project,
environment and worker IDs are deployment assertions, **not** independently
authenticated current execution facts. Supported tools expose no in-session
current-automation association. Never synthesize `native.actual`, undocumented
environment variables or app database access. Real filesystem/repository checks
establish isolation and approved content; atomic round tokens establish ownership.
Acquire `begin-round` before **any** triage-label, claim or native-work mutation;
the role gate covers the entire round, not just individual queue writes.

The selected control repository is `OlyForge3D/PrintFarmer-Ralph-Control`.
Discover its numeric ID and verify exact private visibility and current writer
permission; never infer these from its name. Private configuration pins the
numeric ID, canonical name, ref, registry and owner-approved genesis commit.
Every queue operation rechecks the actual `gh` principal and live write access.
Missing, public, wrong, archived or transferred repositories fail closed.

The owner accepts **shared-writer trust**: every approved private repository
writer can technically impersonate another role. These are trusted same-owner
agents. Role enforcement is in approved helpers/prompts, not GitHub path-level
permissions or authenticated Git author fields. No signing keys are required.
Never describe this as independent coordinator authentication.

## Private Packages And Initialization

Stage a distinct coordinator and consumer workflow/package on the mini, and a
consumer package on Windows. Each package's `host.json` contains `role`,
`workerId`, exact native bindings, `approvedPolicy`, `control`, `stateDirectory`,
`automationWorkflowIds`, `executionTrust:"local-owner-v1"`, `verified:false` and
`migrationAttested:false`.
Mini coordinator and mini consumer MUST have distinct native workflow IDs.
After native verification, include both known mini automation workflow IDs in
each mini package's private `automationWorkflowIds`; never exempt arbitrary work
from inventory by calling it an automation.

The private worker registry schema is:

```json
{
  "version": 1,
  "authorityId": "primary",
  "epoch": 1,
  "writers": ["APPROVED-GITHUB-LOGIN"],
  "workers": [
    { "workerId": "mini", "host": "macos-mobile", "capabilities": ["general", "ios"] },
    { "workerId": "windows", "host": "windows-general", "capabilities": ["general"] }
  ]
}
```

Worker names are opaque routing IDs, not discovered identities. Capabilities
must be verified against actual tools before readiness is published. No device
tags on issues are required. No credentials, paths, prompts or native session
IDs go to the control repository; only opaque assignment correlations and
content/evidence digests. Native observations and session mappings remain in
the private `native-state` directory beside host configuration.

Setup does **not** create/init the queue or authorize takeover. Reconcile old
controllers, Windows ledger and every historical worker under original
ownership. Require terminal evidence or an explicitly authorized fenced
handoff, preserve history, and never infer cessation from disabled schedules.
Only after explicit owner attestation may `verified` and `migrationAttested`
become true. A separately owner-authorized **manual setup session or terminal**
in an approved isolated worktree with the coordinator package may request
`initialize`, supplying fresh migration evidence and
`explicitInitializationApproval:true`. An existing ref is never replaced.
For an empty private repository the initializer verifies the actual default
branch and creates `mailbox.json` through the Contents API without an update
SHA, then verifies the exact root commit. It never overwrites an existing file.
On populated repositories it creates only a new explicitly selected ref.
Pin the returned `genesisSha` in every private package before ordinary rounds.
An uncertain initialization must be inspected, never blindly retried.
The runtime writes `native-state/initialization.json` before any initialization
write. A repeated attempt is blocked, including after a lost response. Inspect
this intent and the actual repository/ref; adopt only a verified matching genesis
after explicit owner reconciliation. Never erase an intent or create a second
authority to bypass uncertainty.

After explicit permission for this exact initialization, prepare a private JSON
request using the approved pin and actual migration observations:

```json
{
  "type": "initialize",
  "approvedPolicy": "<approved-full-SHA>",
  "explicitInitializationApproval": true,
  "evidence": {
    "source": "<retained migration/handoff evidence reference>",
    "observedAt": "<fresh ISO timestamp>",
    "legacyAuthoritiesReconciled": true,
    "cessationOrFencedHandoffProven": true
  }
}
```

Use the runtime command below in a supported terminal. Those booleans summarize
real retained evidence, not permission to assert it without inspection. No
workflow run, current session ID or `native.actual` is needed. An existing queue
is read and reconciled under its existing genesis; **do not initialize it again**.

No schedules, permissions, accounts, source configuration or Reaper are changed
by this procedure. Enabling any role is a separate owner decision.

## Runtime Interface And Durable State

From the isolated approved PrintFarmer worktree:

```bash
node scripts/ci/ralph-native-runtime.mjs --host-config "/absolute/private/host.json" < "/absolute/private/request.json"
```

The script is also supported from PowerShell with a native Windows path:

```powershell
Get-Content -Raw -LiteralPath 'C:\private\request.json' |
  node scripts/ci/ralph-native-runtime.mjs --host-config 'C:\private\host.json'
```

Requests contain `approvedPolicy`, `type`, a unique opaque `id`, `roundId`, `data` and, when
required, `evidence`. `inspect` returns queue state without authorizing dispatch.
`begin-round` obtains the persistent gate for the exact role; `end-round`
releases it. `begin-round` has no caller token: under the atomic local transaction
lock, the runtime creates a random token and persists its digest/context before
publishing acquisition. Only the successful first response contains `roundToken`.
Supply it on every subsequent transition (including `research-plan`, recovery of
a consumer, and `end-round`). Keep it in private session artifacts, never mailbox,
issue or PR content. The same path or caller-selected round ID is not ownership.
Same-worktree concurrent callers cannot independently acquire/reuse the gate.
Only digests, never paths or tokens, reach the queue. Keep event IDs
stable when reconciling a lost response. Every transition is a single-parent
Git commit extending the observed queue head with `force:false`; conflicts
require reread/revalidation, not stale-state overwrite.
Runtime calls for one private journal MUST be serialized. Let each command
finish, capturing its output to a private file; never use `stop_bash`, a shell
timeout, or process termination to shorten a research-plan batch or stop verbose
output. A background command is still running: read its completion instead of
starting another call. Keep batches bounded and do not launch duplicate calls
to obtain their output. Use `try/finally` in round drivers to attempt `end-round`
with the acquired token after ordinary failures; an uncertain publication must
still be reconciled before retrying or ending ownership.

The process-local transaction lock records its PID, request and worktree context.
Normal exit and handled SIGTERM/SIGINT release only that process's lock. This
does NOT settle assignments, end the persistent round, roll back a remote write,
or clear an interrupted `.pending` journal. SIGKILL/power loss can still leave
the lock. Never steal it using age or a PID check alone. Under explicit maintainer
recovery authorization, pause the affected role and verify the owning native
invocation has ceased, all issued runtime commands/children have stopped, and
no scheduled/manual caller remains. Preserve an exact private copy of the lock,
journal and any pending write; quarantine only the proven orphan lock, retaining
its identity and evidence. Do not discard `.pending` content or edit the journal.
Then inspect/replay the mailbox and use the existing original-event or
`recover-coordinator-round`/`reconcile-round` protocol. An unresolved pending
write or uncertain remote publication remains blocked, not automatically retried.
Exact already-committed events remain idempotent after their observation expires;
retry the unchanged request/evidence with the original round token.
The runtime checks the saved request digest and committed event before returning
a non-authorizing replay, even after the original role round ended.
This never permits a stale new transition. Records over 1 MiB are rejected before
publication so every published record remains readable. Narrow oversized task
scope rather than truncating its conflict evidence.

The private control repository is the **single durable reservation ledger**,
not an additional host scheduler. A `reserve` commit must persist before a
separate `publish` transition makes it deliverable. The local journal fsyncs
event intent before network writes and retains verified queue checkpoints and
native mappings. Worker receipts are evidence, not another reservation authority.
No native API can be called as an alternative to a failed reservation.

Round gates do not expire by time. A crashed gate stays blocked. A new owner-authorized coordinator execution can submit `recover-coordinator-round` only with the
exact prior invocation digest and proven cessation/live/queue/history evidence.
The coordinator may `reconcile-round` for a consumer using the same proven
old-round evidence, not a heartbeat timeout. Recovery atomically returns a NEW
round token once; it does not revive the old token. The private journal retains
each acquisition's filesystem context and digest for correlation. Unknown
cessation blocks recovery. PID death is not worker completion. Transaction lock
or `.pending` journal remnants require explicit
inspection/recovery; never delete them merely because they are old.

A cross-role mailbox head race can leave a local acquisition intent without a
published gate or returned token. Do not retry it with a new token or delete the
journal. Use `type:"abandon-acquisition"`, its original `roundId` and
`data:{"acquisitionId":"<original-begin-event-id>"}`. This **local-only,
non-authorizing** operation retains history and returns no token. The runtime
records the exact one-parent candidate/base before attempting a ref update.
Abandonment requires either that no ref update was ever attempted under this
record-before-ref protocol, or verified mailbox descent beyond that base with
the event absent from complete history. In the latter case any delayed candidate
can no longer fast-forward. An unchanged head/network timeout remains uncertain;
absence alone is insufficient. Published events, even if their rounds later
ended, cannot be abandoned. After proven abandonment, use a NEW round/event ID;
the old intent stays immutable and cannot be reused. If the old gate actually
published, use cessation-based round recovery instead.

### Observations Available To The Controller

Use `list_sessions_and_chats`, `get_sessions_status`, `get_session`, returned
creation handles and supported session history for actual inventory/correlation.
Do not assume these expose native workflow identity or a complete pending-input
queue. `queueChecked` means **reconciled controller delivery records plus worker
acknowledgments** under the same trusted-owner boundary, not an invented queue API.
Before cutover, reconcile historical Ralph authorities/workers and known manually
queued input to those workers. During operation inventory is **Ralph-owned
lineage across rounds**, not every session in the app project. The coordinator
tracks its mailbox assignments; each consumer reconciles its durable delivery
intents, mapped workers and their descendants. Unrelated maintainer sessions and
other automations are not Ralph's work, consume no Ralph slots and require no
terminal attestation. Do not archive, adopt or mutate them to clear readiness.
The owning consumer is the
single sender for its mapped task session. Retain each intended/sent delivery's
correlation and returned acceptance in private records BEFORE/AFTER sending.
The worker's correlated terminal acknowledgment must identify the final delivery
it handled and confirm no worker-initiated children/continuations remain.
All controller-owned deliveries must be acknowledged; uncertain send/ACK retains
the slot. Known external intervention requires reconciliation. The owner agrees
not to queue unrecorded work into managed sessions; this is a coordination boundary,
not proof that the app's unseen global queue is empty.
`noPendingContinuation` has this same bounded meaning. Never require a nonexistent
queue API, infer completion from missing/delayed history, or turn idle alone into
terminal evidence. Persist received ACKs locally because history may lag.

For `ready`, supply `ownershipScope:"ralph-owned-v1"` and `lineageChecked:true`
only after reconciling retained creation/delivery records with actual native
readback and ancestry. `complete`, `queueChecked` and `historyChecked` cover this
owned lineage, not the app's global session/message inventory. Project membership,
a matching name, a workflow ID or an idle state does not establish ownership.
Keep journal mappings across rounds, including terminal workers so later
resumption remains detectable. Every retained mapping, including terminal workers,
requires an explicit entry backed by current readback or verified cessation in each
readiness inventory; omission blocks. The sole exception is a mapped worker whose
retirement (outcome `deleted` or `archived`) the consumer recorded through
`record-deletion-result` (see "Owning-Consumer Worker Cleanup"): omit it even though
`get_session` still resolves an archived one, because any inventory entry for its
session ID or a known alias is treated as reappearance and fails closed.
Every unresolved local creation intent remains
owned even when its native creation response was lost; a missing ID never makes
that intent unrelated or authorizes another creation.

The runtime scopes a supplied inventory to recorded worker IDs/correlations,
verified local role roots, and transitive descendants. Normalize actual native
`creator_session_id` to `session.creatorSessionId`, retaining intermediate
ancestor readbacks even when idle/archived. A locally retained creation response
or correlated delivery ACK may establish `session.assignmentCorrelation`; issue
text, titles and guessed IDs may not. Reconcile all recorded intents and obtain
missing ancestry before asserting `lineageChecked:true`. Broad native listings
are discovery inputs, not evidence that every returned session belongs to Ralph.
Unrelated observations are excluded from the capacity inventory digest.
Owned creator chains must be complete and acyclic through a root with no creator.
Ancestor readbacks establish relationships only; unrelated ancestors and their
other descendants are not adopted as work or required to be terminal.
An archived creator's supported `get_session` readback may have `path:""`.
For this ancestry-only entry, retain its verified ID and creator relationship
(a verified root has no creator); a worktree path and `roleObservation` are NOT
required. Do not manufacture a role exemption for an archived ancestor or restore
its checkout merely to satisfy inventory. The actual mapped worker still needs
its own live/retirement evidence. An archive response alone never proves that
mapped task's completion. Missing or cyclic creator relationships still block.

The runtime retains verified creator relationships in private
`journal.nativeLineage` and returns them in `inspect.retainedLineage`. Successful
`ready` calls automatically retain supplied ancestry entries marked
`nativeReadbackVerified:true`, with the inventory's source and observation time.
It stores only ID/creator/source/time, never liveness, terminal flags or role
exemptions. Later inventories automatically resolve missing ancestry-only
entries from these records; do not revoke capacity merely because a creator's
app record disappeared. Fresh mapped-worker, descendant and delivery checks
remain mandatory. Conflicting parent/root observations and cycles fail closed.

Before a creator can disappear, persist its complete verified chain using
`type:"record-lineage"` under the consumer's acquired gate. Supply fresh
`evidence.source`/`observedAt` and `evidence.observations`, each containing
`session:{id,creatorSessionId?}`, `nativeReadbackVerified:true`, and the original
readback's `source`/`observedAt`. Omit the creator only for a verified root,
not because a particular tool omitted the field. Reconcile native listings,
creation responses and readbacks first. Historical supported readbacks can be
recorded with their original timestamps; never relabel them as fresh.
This local-only operation issues no capacity or mailbox event. It is also the
migration path for existing packages with retained native evidence but no
`nativeLineage`; do not edit the journal or fabricate unavailable ancestry.
If an ancestor is still unknown, the runtime names its missing ID.

For a retained terminal worker archived/deleted after settlement, keep its ID,
`assignmentCorrelation`, retained ancestry and `terminalVerified:true` entry.
When live session readback is unavailable, supply `session.retirementObservation`
with fresh `observedAt`, a supported evidence `source`, `status:"archived"` or
`"deleted"`, `liveChecked:true`, `cessationProven:true`,
`noPendingContinuation:true`, `noFutureDelivery:true` and `terminalEvidenceDigest`.
The digest must match the retained local delivery evidence and the assignment's
final correlated terminal receipt/commitment. Current verified cessation plus
that retained proof substitutes for live readback, never omission, idle, an archive
label, missing history or a failed lookup alone. A pending, unconfirmed deletion
intent still needs this live or retirement evidence until `record-deletion-result`
confirms it. Do not archive/delete sessions to manufacture readiness. These are trusted-controller observations, not invented
native API fields; if supported evidence cannot prove cessation, remain blocked.

For owned role-session inventory exemptions, a workflow ID alone is ignored.
Use `session.nativeReadbackVerified:true` and `session.roleObservation` with
`role`, `workerId`, `projectId`, `worktreePath`,
`ownerConfiguredRoleVerified:true`, `noTaskExecutionVerified:true` only after
actual session readback and its known owner-configured prompt/history establish
bounded role-only work. These are trusted controller observations, not app API
fields or independent attestation. Unknown **Ralph-owned** purpose/session blocks
readiness; an unrelated project's/session's existence does not. A role exemption
never overrides a recorded task mapping, including a resumed terminal worker.
Never exempt task work merely because a name or supplied workflow ID matches.
Only coordinator/consumer session work is exempt, not substantial research.

For completion, require the correlated worker's explicit terminal report,
delivered-message accounting, current session readback and pushed/clean artifact
evidence. If queue/history or cessation cannot be established, report uncertain
and retain the slot. Do not translate app `idle` into `terminalVerified:true`.
`historyChecked` may reconcile retained request/response/ACK records with supported
history when available; a delayed history index is not itself a missing delivery
when the actual correlated ACK was retained. Never use absent rows as evidence.

Queue history must extend the pinned genesis and every locally observed
checkpoint. Rewind, discontinuity, malformed content, altered task bindings and
unknown event IDs fail closed. Cold replay is bounded at 2,000 transitions;
at that boundary plan an explicitly reviewed checkpoint migration, never
truncate history or silently initialize another authority.

## Coordinator: Global Triage And Assignment

The mini coordinator alone scans all issues/PRs with full pagination, not just
mobile-labelled work. Classify mobile/general scope and tooling from paths,
labels and acceptance criteria. Validate type, priority and Squad owner labels;
never personally assign `jpapiez`. Squad owner is the responsible specialist,
not the execution device. Reconcile native dependencies, analysis gates, epic
child declarations/readiness, explicit holds and all existing ownership using
the existing [operations](operations.md) and [review gates](pr-merge.md).
Use those references for vocabulary/gates, not their legacy host dispatch entrypoints.
Existing PR repairs take precedence over new slices. Preserve held PRs and
reviewer lockout; do not let a blocked parent erase permitted owned-PR repair.
Retain approved host kickoff clauses and model overrides, including Dallas
Astra/xhigh on macOS; the new role split does not replace those rules.

### Research Gates

`go:needs-research` is actionable investigation **before implementation**, as
defined in [.squad/issue-lifecycle.md](../../../.squad/issue-lifecycle.md#common-issue-lifecycle-patterns).
Live `go:yes` means ready to implement; `go:no` means not pursuing. The latter
and explicit human holds block new research too. Never skip research permanently,
treat its label as implementation permission, or clear it merely to improve
backlog counts.

Use runtime `type:"research-plan"` with fresh issue/label evidence while holding
the coordinator gate. It returns a non-mutating disposition. Reconcile any
existing research assignment first; follow that owner instead of duplicating it.
Reserve bounded research with purpose `research`, the appropriate Squad owner,
compatible device, concrete questions/exit criteria and normal category capacity.
The task classifier enforces research-only admission while the label remains.
Consumers may investigate and document the assigned findings, not implement the
feature or independently transition global labels.

Research reports need concrete findings, linked evidence, recommended decisions
and separate research/implementation blockers. **No merged research PR is required
for investigation-only work.** The consumer persists full findings in one
correlated issue comment and reads it back. The coordinator appends a concise
research summary, decision, implementation plan and comment link to the original
issue description, preserving the report and acceptance criteria. Re-read before
editing and verify the result; never overwrite a concurrent maintainer update.

After a verified terminal research assignment, `research-plan` can propose
removing `go:needs-research` when the **research questions** are answered. The bug
and its implementation acceptance criteria need not already be fixed. Required
findings fields are `summary`, nonempty `acceptanceCriteria`,
`remainingResearchBlockers:[]`, `researchQuestionsAnswered:true`,
`exitCriteriaMet:true`, `approvalRequired:false`, `implementationPlanVerified:true`,
`issueCommentUrl`, `issueEvidenceReadback:true`, `issueDescriptionUpdated:true`,
`repositoryFilesChanged`, `implementationOwner`, `implementationReady` and
`implementationBlockers`. Readback assertions refer to actual GitHub artifacts.

Only if `repositoryFilesChanged:true` also verify `researchPrUrl`,
`researchPrMerged:true` and exact `researchHeadSha`. The research-PR/implementation-issue
pattern is conditional on actual repository artifacts, not a mandatory ceremony.
Investigation-only work sets `repositoryFilesChanged:false`, with no PR fields.
Keep the original issue as the implementation issue by default. If a separate
child is explicitly required, link it and supply `implementationIssue`; never
invent a child simply to remove a research label.

The coordinator alone applies the proposal after refreshing holds, ownership,
dependencies, labels and artifact evidence. A ready same-issue handoff removes
`go:needs-research`, changes the owner from researcher to implementation specialist
where appropriate, and adds `go:yes`. If research is resolved but implementation
has outstanding dependencies, remove the research label without adding readiness;
do not repeatedly dispatch the completed investigation. Apply any child readiness
separately after checking that child's actual state. Inconclusive research,
unresolved research decisions or required approval retain the research gate.
Never close an implementation issue because research finished. A research-only
PR references, not `Closes`, an unresolved implementation issue.

### Admission And Capacity

Reconcile the durable ledger and consumer capacity offers before reservation.
`ready` publishes a **finite willingness to receive queued assignments**, not a
heartbeat or proof the device is online. From freshly reconciled local evidence
the runtime computes unused mobile/general credits: hard category limits minus
all nonterminal assignments, including reserved, published and uncertain work.
The offer binds its event ID, worker, authority/registry, approved policy and
verified capabilities. A fresh offer atomically REPLACES remaining credits; it
never adds them. Concurrent assignment/receipt/blocker or offer/revocation changes
invalidate the captured replacement, requiring reread and real reconciliation.

Coordinator reservations consume credits atomically with global overlap/quota
checks. One mini offer can fund four general reservations without interleaved
consumer rounds. Neither release, withdrawal, duplicate replay nor elapsed time
refunds a credit. Only another reconciled consumer offer can replenish capacity.
An offline device may receive **only its unused pre-offered credits**; those
reservations stay counted indefinitely. Missing/offline never means idle.
`unavailable` or an assignment blocker revokes remaining credits without changing
any reservation. A new policy pin requires a matching new offer; incompatible
registry/authority configuration cannot reuse old offers/history.

Independent hourly schedules may be arbitrarily offset. Offers, publication and
durable terminal commitments do not expire between role rounds. The 60-second
bound still applies to NEW event evidence and the consumer's own same-round
inventory immediately before starting; it is not a cross-workflow rendezvous.
An expired local observation requires a genuine local recheck, not a restamped
cache. A slower/failed round exits or retains its gate on uncertainty; never
synchronize devices by sleeps, fast manual choreography or polling.

Reserve with `data.assignmentId`, `data.workerId` and fresh `evidence` containing:
`repository`, issue and/or PR number, exact `headSha`, title, labels,
`acceptanceCriteria`, scope/classification completeness, required `capabilities`,
complete repository-relative `files`, and true verified `claimsReconciled`,
`holdsChecked`, `dependenciesReady`, `epicChildrenReady`, `analysisReady`,
`filesComplete`, `reviewGatesChecked`. These are normalized facts obtained from
supported tools, not instructions to assert success. The helper computes task,
requirements and case-folded file digests without publishing raw local evidence.
Mixed/unknown scope reserves mobile capacity; unknown files block.
Also supply current `issueState:"open"` and `githubAssignees:[]`. Admission
mechanically requires one allowed Squad owner, type and priority; emoji/plain
duplicates normalize to the same owner. Reviewer/Ralph/device names are not
dispatch owners. Epics and `status:needs-analysis` permit only bounded
analysis/research. For research, `analysisReady` means its bounded investigation
scope is ready, not that the unanswered research has already been completed.

#### Published Task Packets

`requirementsDigest` hashes coordinator-authored `acceptanceCriteria`, and
`fileKeys` hash coordinator-selected `files`. A consumer holding only the public
issue cannot re-derive either, so the runtime publishes the facts themselves.
Before `reserve`, the runtime re-reads the live issue (and PR) through GitHub and
fails `task-readback-mismatch` if the reserve evidence's title, labels, state or
assignees differ. It then builds a versioned `taskPacket`
(`version:"ralph-task-packet-v1"`): `repository`, issue/PR, `purpose`, `headSha`,
`title`, `labels`, `acceptanceCriteria`, repository-relative `files`, `scope`,
`classificationComplete`, `capabilities` and `sourceBodySha256` (SHA-256 of the
issue body at reservation). It has exact keys and a 64 KiB bound; absolute,
home-relative, drive-letter and `..` paths are rejected. It never contains
secrets, local paths, prompts or native IDs: acceptance criteria, scope and
files that mention a home-style, drive (`C:\` or `D:/`) or UNC path,
`.printfarmer-ralph`, a UUID, a token-shaped credential or a private key are
rejected before reservation. The reducer accepts the packet only
if it reproduces the reserved task digest (`task-packet-tampered` otherwise), so
a rewritten control-repository record also fails replay.

Coordinator `publish` and consumer `dispatch-plan`/`starting` do not re-author
task facts. The runtime reads the published packet, re-verifies it against
`requirementsDigest`, `fileKeys` and `taskDigest`, re-reads GitHub, and composes
the task evidence itself. Consumer evidence supplies only `observedAt`, `source`,
`holdsChecked`, `ownershipReconciled` and `nativeCapabilities`. Supplied task
facts are optional, but must equal the packet exactly (`task-packet-mismatch`) or
the live readback (`github-readback-mismatch`). These fail closed before kickoff:

| Code | Cause | Consumer action |
|---|---|---|
| `native-evidence-missing` | Packetless (pre-#2958) reservation | `report-blocker` with prestart-proof |
| `task-changed` | Title, labels or body digest changed after reservation | `report-blocker` with prestart-proof |
| `held` | A human hold or `go:no` label is now present | `report-blocker` with prestart-proof |
| `task-packet-tampered` | Packet no longer reproduces the task digest | Stop; the mailbox is untrusted |

`startup-check` repeats this recheck before it authorizes the first substantive
continuation. An edit, hold or assignee added after `starting` fails `task-changed`
or `held` with no continuation: keep the child startup-only and `report-blocker`.
A failed GitHub readback fails `github-readback-invalid`; retry the same check.

The coordinator withdraws such a binding from the published blocker and, when
still eligible, re-reserves from a fresh readback. Nobody relays evidence by hand.
Existing packetless assignments keep blocking; only an already-delivered
packetless start may replay its own saved request. A blocker recorded before
#2958 committed only its proof digest; `prestart-proof` then mints a fresh proof
(`alreadyReported:false`) so the consumer re-reports it with the full payload
and the coordinator withdraws from the mailbox alone.

The coordinator reserves Mac **1 mobile + 4 general, 5 total** and Windows
**0 mobile + 5 general, 5 total**, with no borrowing. Global issue/PR/file
overlap is excluded atomically. Substantial analysis is assigned and accounted
like implementation; do not spawn an uncounted analyst. Reviews run source-only
within the owning session via the approved reviewer tools. A new native work
session requires another valid reservation; no hidden review/recovery slots.
One Xcode job remains an additional Mac safeguard.

Publish the exact reserved binding using `assignmentId`, `generation:1` and
returned `taskDigest`; the runtime re-verifies the packet and live issue first. A reservation survives a crash before publication; retry
only its original publication, not another assignment. Do not publish if current
holds/head/ownership have changed; retain and reconcile the reservation instead.
The coordinator tracks aggregate progress and handles consumer discoveries.
Changed scope/files or replacement execution requires safe terminal/handoff
reconciliation and a new nonconflicting reservation, never editing a live task.

## Consumer: Assigned Native Work Only

### Validated Specialist Dispatch

`RALPH-ASSIGNED-WORKER-V1` separates the PrintFarmer-owned native **Ralph Worker**
agent from the issue's logical Squad specialist. The consumer does not pass
`Squad`, `Dallas`, `Lambert`, etc. as custom-agent names. Ralph Worker performs
the assigned member's work directly using that member's charter; it does not
invoke Squad or run coordinator fan-out and fallback. Squad's distribution-owned
agent is unchanged and is not a dependency of this bounded entrypoint.
See [assigned-worker.md](assigned-worker.md).

New reservation evidence must contain `scope` (`general`, `mobile`, `mixed` or
`unknown`) and boolean `classificationComplete`. Unknown/incomplete/mixed work
still counts mobile. Capabilities are tooling, not a substitute for classification.
Omitted fields block before capacity is consumed. Never change a live binding's
classification to recover credits.

Both `ready` and kickoff evidence include `nativeCapabilities`, obtained from
the actual exposed app tools: `createSession:true`, `agents:["Ralph Worker"]`, and
`models` mapping advertised model IDs to their supported reasoning-effort values.
This is local-owner capability evidence, not an independent service identity.
Do not invent support.
Read the **create_session tool's kickoff model/effort catalog**, not the current
controller's `model_information` line or its own selected model. The controller
can create workers using other advertised models. Include the assignment's
resolved model/effort when the tool actually advertises it; a self-created
single-model list is not evidence of a platform limitation. Before reporting
`capability-unavailable`, re-read the tool catalog and state the missing pair.

`report-blocker` requires `data.assignmentId`, `data.generation`,
`data.taskDigest` and a supported `data.reasonCode` from the original assignment,
plus `evidence.source` and fresh `evidence.observedAt`. Never omit the task digest
or translate request-shape validation errors into an assignment/platform blocker.
Correct malformed observations while holding the same gate; do not keep retrying
invented reason codes or silently exit claiming that a rejected blocker was saved.

Under the consumer round token, `dispatch-plan` takes the exact assignment
binding plus proposed opaque `correlation` and fresh capability evidence. Task
facts come from the published packet (see "Published Task Packets"). It validates the owner and bounded entrypoint, loads the owner's
charter, resolves model/effort and returns a **non-authorizing** plan.
The macOS Dallas host override remains Astra/xhigh; otherwise explicit Squad
overrides win, a configured model effort suffix is normalized, and unspecified
effort is explicitly medium. Unsupported/conflicting values block, never fall back.
Charters and the Ralph Worker entrypoint are approved controlled policy paths.

`dispatch-plan.inventoryFreshness` reports the **saved ready inventory's** original
observation time, age, 60-second maximum and `refreshRequired`. This diagnostic is
not admission: all current-round, policy, inventory, capability and quota checks
still apply. Finish task/capability preparation first, then obtain actual fresh
native inventory, publish `ready`, and immediately submit the starting receipt.
If kickoff inventory expires, perform one bounded fresh native readback/`ready`
before retrying. Changing the receipt's task-evidence timestamp cannot refresh
saved readiness; never adjust clocks or re-stamp stale observations. If the bounded
refresh fails, retain the exact inventory-age error and report blocked.

The successful starting receipt recomputes and privately persists that plan.
Only its `nativeCreateAllowed:true` response permits one call to the returned
`dispatchPlan.nativeTool` using **exactly** `dispatchPlan.nativeArguments`.
New PR recovery uses `create_session` at the verified existing PR head ref in a
new isolated worktree; **never the native PR-opening tool**, whose implicit reuse can adopt
or mutate an unrelated native session. Fresh evidence includes `prState:"open"`,
`prHeadRepository:"OlyForge3D/PrintFarmer"`, `prHeadRef`, `prHeadSha` matching the
task, and `prWorkerPolicyDigest`. The consumer does not supply that digest: the
runtime reads it from the immutable PR head through the GitHub contents API.
It hashes `.github/agents/ralph-worker.agent.md`, the owner member's charter
and `.copilot/skills/ralph-loop/assigned-worker.md` (CRLF normalized to LF),
serialized as `{agentSha256,contractSha256,charterSha256}` in that order using
mailbox `digest`. An unreadable file fails `github-readback-invalid`; a supplied
value that differs fails `github-readback-mismatch`.
It must match the approved local worker policy. An older branch without this
bounded entrypoint needs policy reconciliation by its existing owner, not a
blind kickoff using its old coordinator instructions. Forks and unverifiable
head policy block new admission.

The packet binds the original PR branch and head. Startup ACK must report the
actual initial Git HEAD and the actual branch matching native readback. Issue
work starts from `development`, which may have advanced since reservation: a
different `initialHeadSha` is accepted only when GitHub compare proves it is a
descendant of the reserved head and still on `development`. PR work never
accepts a different head. Head
movement blocks before substantive work; `startup-check` also requires
`currentPrHeadSha` from fresh GitHub readback equal to the assigned head.
The new worktree is only a repair
workspace: preserve the published PR, use explicit normal fast-forward
`git push origin HEAD:refs/heads/<verified-pr-head-ref>` after fresh ownership/head
checks, and never force-push or open another PR. Existing mapped workers continue
in place; they do not need a new reservation or session.
The packet, prompt, model settings and native IDs stay in the private journal;
only digests reach the mailbox.

Immediately persist the returned handle with `record-creation` (exact binding
and correlation). Evidence includes `creationHandle`, `creationOutcome`
(`succeeded` or `partial`), and `dispatchPlanDigest`. A successful call also
requires `createRequestDigest` of the exact native arguments and
`kickoffAccepted:true`. If readback is unavailable, omit `session`: the handle
is retained and `reconciliationRequired:true` returned, never new-create permission.
Then record actual normalized native readback (`session`, `repository`,
`nativeReadbackVerified:true`). A runtime-ID alias change must resolve the retained
`creationHandle`, identify it as `resolvedCreationHandle` and preserve the same
project/worktree; it is not permission to replace the workspace.

The worker initially returns startup-only ACK and stops. Submit `startup-check`
with the exact binding, same session, plan digest and `startupAck`.
`evidence.session` must include actual native-readback `id`, normalized
`projectId`, `worktreePath`, and **`branch`**. Preserve the branch returned by
`get_session`; the create result's handle/path alone is not a complete startup
readback. A missing branch is malformed evidence, not proof of worker movement:
correct the observation on the same child, never recreate it.
The `startupAck` must contain packet
`assignmentId`, `generation`, `taskDigest`, `correlation`, `member`,
`charterSha256` and **every other packet field unchanged**, including policy,
repository/head/ref, purpose/category and configured model/effort. Also require
`initialHeadSha`, `actualBranch`, `substantiveWorkStarted:false`, `noChildren:true`, plus actual
model/effort **only if exposed**. Include `configuration` with exact `model`,
`reasoningEffort` and source `successful-native-create`, `native-readback`, or
`owner-attestation`. Success of the exact native creation request establishes
accepted settings, not independently observed runtime settings. A partial/failed
kickoff cannot use that source; it requires actual readback or explicit attestation.
If the app cannot expose/change lost configuration, report that platform limitation;
never manufacture proof or silently use default settings.

`startup-check` durably records a continuation intent before returning
`continuationAllowed:true` and the substantive message. Send it once to that
same session. Repeated checks return false; lost delivery is reconciled from
actual history/ACK, never blindly resent. The first running receipt requires
`continuationAck` with the complete packet and `substantiveWorkStarted:true`.
Subsequent status receipts retain the established mapping.
Completed terminal work requires that acknowledged startup and substantive start,
plus a final ACK echoing the complete packet and cessation commitments. Neither
`starting -> terminal-reported` nor `starting -> uncertain -> terminal-reported`
can masquerade as completed work. Uncertain pre-start work remains owned for
recovery, not released by a success-shaped terminal report.

For research/analysis completion, the consumer writes findings once to the issue
and reads back the existing comment (recover an uncertain write by correlation,
not another comment). Use gated consumer `type:"artifact-readback"` with
`data.assignmentId`, `generation`, `taskDigest`, `artifactUrl`, and fresh
`evidence.source`/`observedAt`. The runtime retrieves that issue's comment through
GitHub, hashes the exact parsed JSON `body` UTF-8 bytes, and returns `artifact`,
`artifactReadbackVerified:true` and a deterministic, valid-length
`finalDeliveryCorrelation` without a mailbox write. Use that correlation
unchanged in the same-child ACK request and terminal evidence; do not concatenate
assignment IDs or suffixes. It binds the assignment generation/task and exact
artifact, remains stable on repeated readback, and changes if the artifact changes.
Never rewrite a returned worker ACK to repair an invalid correlation; obtain an
actual same-child artifact-only correction. Do not hash
`gh --jq .body` or `jq -r` output: their formatter adds a newline.
Then obtain the same child's final ACK. New bounded-worker
terminal evidence includes `artifactReadbackVerified:true`, `artifact` with
`kind:"issue-comment"`, exact `url` and SHA-256 `bodyDigest`, and `finalAck` with
packet identities/member, that `artifactUrl`, matching `artifactBodyDigest`,
`artifactReadbackVerified:true`, the `finalDeliveryCorrelation`,
`noChildren:true`, `noPendingContinuation:true` and `noFutureDelivery:true`.
Chat-only findings are not a durable research deliverable. Keep existing
implementation/review gates and never close an implementation issue after research.
Terminal submission independently fetches and hashes the live comment again;
changed bytes or a missing/mismatched worker artifact ACK block the receipt.

Every packet-bound terminal receipt publishes its terminal artifact. Implementation
work ending in a PR uses the same `artifact-readback` with the PR URL. The runtime
reads the same-repository PR and returns `kind:"pull-request"`, `url`, `number`
and `headSha`; the final ACK echoes `artifactUrl`, `artifactHeadSha` and
`artifactReadbackVerified:true`. Implementation work with no PR (for example a
duplicate) uses an issue-comment artifact. The receipt stores only this public
artifact in the mailbox, so the coordinator settles from GitHub, not a relay.

### Ordinary Local Lifecycle

Consumers do not globally triage, choose new issues, change worker assignment,
grant replacements or independently claim the backlog. Inspect only assignments
for their own worker ID and their Ralph-owned session lineage/history.
Publish `ready` only after complete fresh owned native/queued/history inventory
and verified tooling. Every nonterminal owned task or descendant must map to an
admitted assignment, except explicitly verified role automations. Unmapped or
uncertain **owned** work blocks readiness and new admission, never causes deletion.
Missing live mapped workers, unacknowledged creation intents and resumed terminal
workers remain blockers across invocations. Do not filter them out as unrelated.
Independent manual/acceptance/other-automation sessions outside this lineage do
not block readiness. This changes neither shared mailbox quotas nor issue/PR/file
overlap and live ownership checks before reservation, publication and kickoff.
If inventory/tooling cannot be reconciled, send `unavailable` with fresh retained
evidence and `data.reasonCode` of `inventory-unreconciled`,
`capability-unavailable` or `owner-paused`. This revokes remaining offer credits,
not reservations. Report a binding-specific blocker for any affected terminal
assignment before a coordinator can settle it. Do not leave known resumption or
uncertain delivery represented by an unchallenged terminal commitment.

Read assignment task requirements from the actual issue/PR and approved policy
as data. Refresh holds/ownership/head/files and recompute task identity before
kickoff. In this consumer round publish `ready` against the CURRENT ledger/local
inventory, even when the assignment was reserved under an older offer. This
refresh preserves outstanding assignments and excludes their occupied slots
from the replacement offer. Then submit `receipt` with the exact
`assignmentId`, `generation`, `taskDigest`, `status:"starting"` and new opaque
`correlation`, plus complete current task evidence, `holdsChecked:true` and
`ownershipReconciled:true`. This persists local delivery intent and queue receipt
BEFORE a native call. Admission requires that current-round observation to be
at most 60 seconds old, the complete assignment/receipt/blocker inventory to
still match, all required local capabilities to be present, and the reservation
and consumer to have the same policy pin. A newer offer does not invalidate an
older reservation under that pin. If a policy renewal changed the pin, reconcile
and withdraw only proven never-delivered work before reserving it under the new
policy; running/uncertain mappings remain owned, never restarted.
Publication and kickoff both recheck that the issue is open, unassigned to a
person, and retains valid triage labels. `status:blocked` and `blocked` are holds,
not permission to proceed when cached eligibility says ready.

Only a response with `nativeCreateAllowed:true` permits one local supported
call using exactly `dispatchPlan.nativeTool` and `dispatchPlan.nativeArguments`.
New assignments, including PR repair, use guaranteed-new `create_session`
worktrees; preserve the existing published PR branch as specified by the plan,
never adopt another native PR session. A lost response or `false` never permits
another creation. Persist the handle with `record-creation` before readback.
Follow the validated specialist startup-only ACK, `startup-check` and single
continuation sequence above; a workspace handle does not authorize direct work.
Only after the complete substantive continuation ACK submit `receipt` status
`running`, including `continuationAck`, fresh `evidence.session.id`,
`assignmentCorrelation`, `repository`, `nativeReadbackVerified:true` and
`kickoffDeliveryVerified:true`.
Normalize actual `get_session.project_id` to `session.projectId` and actual
`get_session.path` to `session.worktreePath`; both must match the configured
project and isolated-worktree parent. Normalize `project_repo` to `repository`.
Retain the returned creation handle and readback identity together; do not assume
all app session IDs and underlying CLI history IDs are interchangeable.
The actual ID is retained locally, never serialized into the queue.

If kickoff/ack is lost, inspect native inventory, queued delivery and history for
genuinely correlated evidence. Issue text, a matching name or an idle/missing
session alone is not proof. If proof is unavailable retain the starting/uncertain
slot and report the blocker; there is no native creation idempotency token.
Resume/follow only the exact locally mapped session. Do not resend unchanged
findings or start a replacement as a shortcut.

`running`, `review`, `recovery`, `uncertain` and `terminal-reported` receipts bind
the same correlation/native session. Terminal reporting additionally requires
verified cessation, queue/history and task artifact evidence,
`noPendingContinuation:true`, `noFutureDelivery:true`, and
`finalDeliveryCorrelation` identifying the worker's final acknowledged delivery.
These summarize retained real ACKs and the sole sender's commitment to send
**nothing further** to that mapped execution. They are not app API fields or
proof of the app's global queues. Consumer reports never release capacity.
The durable commitment is one-way: no starting/running transition may resume it.
Coordinator
`release` requires the exact consumer receipt digest, task digest, verified
ownership/artifacts and no pending continuation. For packet-bound work the
runtime re-reads the published terminal artifact itself:

- An issue comment must be byte-identical (`artifact-changed`).
- An open PR retains the assignment (`artifact-pending`).
- A closed unmerged PR needs an explicit `closureReason`.
- A merged PR must keep the published head (`artifact-changed`) and contain
  `Closes #issue` (`artifact-unlinked`). Its `squad/pre-pr-verdict` status at
  that exact head must classify as `REVIEWED` or `APPROVED`
  (`artifact-unreviewed`). The runtime uses `loadSquadVerdict` from
  `verify-squad-verdict.mjs`, so the status must come from a trusted
  `squad-review-verdict.yml` run for that PR on the default branch, not merely
  carry a matching description.

The settle digest binds that readback. For packet-bound work, `settle` also
carries `terminalReceiptDigest` and `terminalArtifactDigest`. The reducer
rejects the settlement (`artifact-changed`) if a replacement terminal receipt
lands between the coordinator's verification and publication. Until then all slots remain
reserved. Discoveries, new prerequisites and expanded scope go back to the
coordinator before work expands.
A later coordinator may settle that commitment hours/days later without consumer
readiness or another terminal timestamp. Its own reconciliation evidence is
fresh; any observed resumed/reassigned session or unaccounted delivery blocks
release. A reported blocker revokes settlement until the consumer genuinely
reconciles and issues a new complete terminal commitment. Legacy terminal
receipts without this commitment need that one-time reconciliation; never promote
an old idle/history observation automatically.

The mailbox uses additive `offer-capacity`, `deliver`, `accept`,
`terminal-receipt` and `settle` events internally. Three optional, versioned
payloads carry cross-role facts: `reserve.taskPacket`
(`ralph-task-packet-v1`), `report-blocker.prestartProof`
(`native-runtime-prestart-proof-v1`) and `terminal-receipt.artifact`. Keep using the public runtime
requests `ready`, `publish`, `receipt` and `release`; internal event names are
rejected as requests. Historical events retain their original reducer and
hashes. No genesis replacement or history rewrite is needed.

For a published assignment blocked before kickoff, use `report-blocker` with
the exact binding, fresh evidence and one nonsecret `reasonCode`:
`task-changed`, `held`, `capability-unavailable`, `native-evidence-missing`,
`delivery-uncertain`, `scope-expanded` or `dependency-blocked`. Blocker reports
retain capacity and never contain raw prompts/paths. Coordinator `withdraw`
requires fresh `claimsReconciled` and `noNativeDeliveryVerified` evidence and
works **only** before the atomic starting receipt. If starting won the race,
withdrawal is rejected and ownership remains. If withdrawal won, the consumer
cannot obtain creation permission. Starting/running/uncertain work never uses
this shortcut.

### Owning-Consumer Worker Cleanup

`RALPH-WORKER-CLEANUP-V1`. Only the macOS consumer deletes, and only its own mapped
workers whose assignments the coordinator already settled. The runtime rejects all
three cleanup requests from the coordinator and from Windows hosts. This is the
standing, narrowly scoped maintainer authorization for `delete_item` delivered by
the personally approved package; it does not extend to role sessions, unrelated
maintainer sessions, other workers or other hosts. Never call `archive_session`:
it only works for sessions the calling run created, and that creator run is gone.
`cleanup.md` gives the eligibility rules in prose.

All three requests run under the consumer's acquired round token and fresh
`evidence.source`/`observedAt`. At the start of each consumer round, before `ready`,
read `deletions.pending` from `type:"inspect"` (it lists every intent retained from
any earlier round) and inspect each one with `record-deletion-result`. Any pending
intent blocks `ready` until it is confirmed, even when valid live evidence for that
worker is supplied. If it stays unconfirmed, stop and report it for owner
reconciliation; never retry `delete_item` or edit the journal.
After the round's admission/kickoff work and before `end-round`, submit
`type:"cleanup-plan"` (read-only) with evidence:

- `callingSessionId`: this run's own session ID; `mainCheckoutPath`: the main
  checkout path; optional `maxDeletions` 1-5 (default 5).
- `candidates`: one entry per mapped worker to consider, keyed by the exact
  recorded `sessionId`. A known alternate identifier such as `project_session_id`
  goes in `aliases`, never in `sessionId`; an alias must not match another mapping.
- `live` from `get_session`: `found`, `name`, `projectId`, `worktreePath` (normalized
  `path`), `branch`, and explicit booleans `busy`, `pendingInput`, `agentMerge`,
  `automation`.
- `closureReason` for a closed-unmerged PR, and `artifactUrl` for no-PR
  research/analysis; the runtime re-reads the artifact and compares it with the
  retained terminal artifact when one exists.

The runtime, not the caller, reads the git and GitHub facts. It canonicalizes the
recorded worktree path (`realpath`, case-folded) and requires it to stay inside the
configured worktree root with no symlink redirection and no overlap with the main
checkout. It then reads `HEAD`, the branch and `git status --porcelain` (tracked and
untracked) with hooks and fsmonitor disabled. From GitHub it reads every PR for that
exact head branch and repository, the remote branch ref, and the head's commit
comparison with `development`. Pushed means the remote ref equals the local `HEAD`,
or, without a remote branch, that GitHub knows the local `HEAD`. Any caller-supplied
`worktree`, `prs` or `prsChecked` field is ignored, so an omitted open PR or a false
clean/pushed claim cannot make a worker eligible. A failed lookup retains the worker.

The delete target is anchored to the settled terminal commitment. At the
`terminal-reported` receipt the runtime retains the full terminal evidence in the
mapping, and cleanup requires its digest to equal `assignment.terminalCommitment`
and its session ID, worktree path and correlation to equal the mapping's. The
committed evidence also carries a runtime-added `runtimeIdentity` (session ID,
creation handle and every recorded alias), so removing or replacing a recorded alias
before intent retains the worker with a manual-cleanup reason. A mapping
recorded before #2954 has no retained terminal evidence, so it is retained with a
"clean up manually" reason and is never auto-deleted.

The plan returns `eligible` (bounded, oldest settled first, `deleteAllowed:false`),
`retained` and `pending` with reasons, and `deleted`. Every mapped worker without
candidate evidence is retained. Unmapped, role, Ralph/Reaper-named, calling and
main-checkout sessions are never eligible.

For each eligible item in order, submit `type:"record-deletion-intent"` with
`data.sessionId` and the same kind of fresh evidence. The runtime re-plans, journals
the intent (session ID, aliases, assignment, correlation, terminal digest and
worktree) with fsync, and only then returns `deleteAllowed:true`, `nativeTool` and
`nativeArguments`. Make exactly that one `delete_item` call. A lost intent response,
a second intent for the same session, or a failed call never authorizes another.
Then call `get_session` for the session ID and every returned alias, check whether
the worktree directory exists, and submit `type:"record-deletion-result"` with
`data.sessionId`, `lookups:[{id,notFound,archived,path,resolvedId}]`,
`worktree:{path,absent}` and the observed `deleteOutcome`. Report each lookup
exactly as observed: `notFound`, and, when found, the boolean `archived`, the
normalized `path`, and `resolvedId`, the session ID the lookup resolved to. Native
`delete_item` only archives worktree sessions: afterwards `get_session` still
resolves the session ID and its `project_session_id` alias with `archived:true` and
`path:""`, and this persists. Each identifier is therefore retired when it is
`deleted` (not found, with no contradicting archive, path or resolved-ID facts) or
`archived` (found, `archived:true`, empty or absent path, and `resolvedId` equal to
the recorded session ID). A live, path-bearing, differently resolved, unknown or
contradictory lookup is unconfirmed. The runtime records the retirement only when
every identifier is retired, the caller reports the recorded worktree path absent,
and its own `lstat` of that path also finds nothing. The confirmation stores each
lookup's facts and outcome plus the overall `outcome` (`deleted` only when every
identifier was not found, otherwise `archived`) with a digest. Every later read
recomputes it, so a record flipped to `deleted` without matching per-identifier
proof fails closed. Anything else stays pending with its inspection retained;
re-inspect it on later rounds and report it. Never retry `delete_item` or edit the
journal to clear a pending intent.

A recorded retirement keeps the mapping, its terminal proof and retained ancestry;
an `archived` outcome is treated exactly like `deleted`. Later `ready` calls accept
that mapping without live evidence, and `inspect`'s `deletions.deleted` and the
plan's `deleted` list report the outcome. Omit retired workers from inventories and
`cleanup-plan` candidates. If the session ID or a known alias reappears in any
inventory or candidate list, for example unarchived or with a path, or a descendant
names it as creator, readiness fails closed until the owner reconciles it. Report `Sessions retained`
and `🧹 Ready to reap` every round, including empty headings, with each reason and
deletion result.

### Renewal And Never-Delivered Reconciliation

Before publishing ready after renewal, inspect reserved/published old-policy
assignments; do not attempt starting with a mismatched policy and repeatedly
revoke otherwise usable capacity. Starting/running/uncertain workers retain
their original ownership and mappings; do not recreate them.

The owning consumer's `prestart-proof` request takes the exact binding and fresh
`authoritativeJournalRetained:true`, `protocolOnlyDeliveryAttested:true` evidence.
Runtime verifies mailbox continuity, reserved/published state, zero receipts and
no retained delivery intent for that assignment. It returns an opaque `proof`
without creating work or mutating the mailbox. This proves no protocol-authorized
delivery under the accepted local-owner trust, not absence of arbitrary activity
outside that trust boundary. Idle/missing sessions or an empty coordinator journal
are not substitutes.

Commit the exact returned proof as `report-blocker` evidence (for example
`task-changed` on old-policy work). The runtime accepts it only if it equals the
proof retained in this consumer's journal, and publishes it as the blocker's
`prestartProof`. Never send the proof through session messaging or the owner.
Later `prestart-proof` calls return the retained proof with
`alreadyReported:true` when that digest is already committed; do not report the
blocker again. Coordinator `withdraw` evidence needs only fresh `claimsReconciled`
and `noNativeDeliveryVerified`: the runtime reads the published proof from the
mailbox, verifies its digest equals the committed blocker, rechecks binding and
state, and binds it into the withdrawal digest.
Proof can cross hourly rounds: fresh coordinator observation still must find
the same never-started binding and consumer proof. A racing start prevents
withdrawal; a racing withdrawal prevents start. The only format change is the
optional versioned `prestartProof` field; no history rewrite, Windows journal
access from the mini or quota refund is needed.

Publish fresh ready **after** recovery reports. Do not re-report an unchanged
already-committed blocker on every round and revoke the replacement offer again.
Settlement/withdrawal does not refund credits; the fresh inventory-derived offer
does. Generate evidence timestamps with `new Date().toISOString()`, not GNU
`date` formatting on macOS.

## Checks

Use the focused Node fixtures for mailbox transitions, private-repository
identity, sibling conflicts, lost acknowledgments, quotas, local identity
mapping and setup staging. They do not initialize a live queue, create native
sessions, run Xcode, enable schedules or manufacture production attestation.
`scripts/ci/tests/test-ralph-e2e-roles.mjs` runs the coordinator, mini and
Windows consumers as separate runtime invocations with separate journals and host
configs, sharing only a simulated control repository and GitHub fixtures. It
drives research, implementation, stale-withdrawal and Windows assignments to
settlement with no owner relay, plus the fail-closed negative cases.
