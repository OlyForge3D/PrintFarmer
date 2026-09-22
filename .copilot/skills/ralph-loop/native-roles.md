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
readiness inventory; omission blocks.
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

For a retained terminal worker archived/deleted after settlement, keep its ID,
`assignmentCorrelation`, retained ancestry and `terminalVerified:true` entry.
When live session readback is unavailable, supply `session.retirementObservation`
with fresh `observedAt`, a supported evidence `source`, `status:"archived"` or
`"deleted"`, `liveChecked:true`, `cessationProven:true`,
`noPendingContinuation:true`, `noFutureDelivery:true` and `terminalEvidenceDigest`.
The digest must match the retained local delivery evidence and the assignment's
final correlated terminal receipt/commitment. Current verified cessation plus
that retained proof substitutes for live readback, never omission, idle, an archive
label, missing history or a failed lookup alone. Do not archive/delete sessions to
manufacture readiness. These are trusted-controller observations, not invented
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

Research reports need concrete findings, linked evidence, acceptance criteria and
remaining blockers. Respect the documented research-PR/implementation-issue
pattern: verify the research PR actually merged and its exact head, and identify
the implementation issue/plan before proposing readiness. `research-plan` retains
the gate for inconclusive findings, open blockers or required approval; active
research also retains ownership. Its completed-research evidence includes
`findings.summary`, `acceptanceCriteria`, `remainingBlockers`, `exitCriteriaMet`,
`approvalRequired`, `implementationPlanVerified`, `implementationIssue`,
`researchPrUrl`, `researchPrMerged` and `researchHeadSha`.

Only a verified terminal research assignment with complete exit evidence can
produce a **proposal** to remove `go:needs-research` and add `go:yes`. Re-read the
target issue's holds, labels and evidence before applying it through supported
GitHub tools. Follow any issue-specific human approval requirement. Never close
an implementation issue because research finished. If a separate implementation
child is required, link it and preserve the original research evidence.
A research-only PR must reference, not `Closes`, the still-unresolved
implementation issue; merging research must not accidentally auto-close it.

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

The coordinator reserves Mac **1 mobile + 4 general, 5 total** and Windows
**0 mobile + 5 general, 5 total**, with no borrowing. Global issue/PR/file
overlap is excluded atomically. Substantial analysis is assigned and accounted
like implementation; do not spawn an uncounted analyst. Reviews run source-only
within the owning session via the approved reviewer tools. A new native work
session requires another valid reservation; no hidden review/recovery slots.
One Xcode job remains an additional Mac safeguard.

Publish the exact reserved binding using `assignmentId`, `generation:1` and
returned `taskDigest`. A reservation survives a crash before publication; retry
only its original publication, not another assignment. Do not publish if current
holds/head/ownership have changed; retain and reconcile the reservation instead.
The coordinator tracks aggregate progress and handles consumer discoveries.
Changed scope/files or replacement execution requires safe terminal/handoff
reconciliation and a new nonconflicting reservation, never editing a live task.

## Consumer: Assigned Native Work Only

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
`create_session`/`open_pr_session` call for that assignment. Carry the opaque
correlation in its kickoff. Preserve existing PR branches via the PR-session
tool; never substitute a fresh branch for owned recovery. A lost response or
`false` never permits another creation. Read back the returned actual native
session and deliver only once, then submit `receipt` status `running` with fresh
`evidence.session.id`, `assignmentCorrelation`, `repository`,
`nativeReadbackVerified:true` and `kickoffDeliveryVerified:true`.
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
ownership/artifacts and no pending continuation. Until then all slots remain
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
`terminal-receipt` and `settle` events internally. Keep using the public runtime
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

## Checks

Use the focused Node fixtures for mailbox transitions, private-repository
identity, sibling conflicts, lost acknowledgments, quotas, local identity
mapping and setup staging. They do not initialize a live queue, create native
sessions, run Xcode, enable schedules or manufacture production attestation.
