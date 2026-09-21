---
name: ralph-native-mailbox-roles
description: Sole mini coordinator and native local consumers using a private GitHub queue.
---

## Binding And Trust

NATIVE-MAILBOX-ROLE-V1. This contract replaces legacy independent host dispatch
**only for explicitly attested native role packages**. Never combine the old
dispatcher with this role. Run one round and exit, no sleep or self-scheduling.
No SSH workers, CLI workers or cross-host native session creation.

Use the saved package's single approved commit for all origin, fetched/current
controlled-policy, ancestry and untracked-file checks in
[bootstrap.md](bootstrap.md). Do not execute helpers before those checks pass.
The native runtime also calls the existing non-authorizing filesystem preflight.
Then obtain genuine fresh observations of THIS executing automation through
supported app tools/runtime metadata. Normalize them to the existing
`identity-check` contract. Arbitrary workflow lookup or copied prompt/config
values are insufficient. Unavailable current-invocation association blocks
all queue mutations and native work; report the missing capability.
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
`automationWorkflowIds`, `verified:false` and `migrationAttested:false`.
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
become true. A separate authorized current native coordinator invocation may
request `initialize`, supplying fresh migration evidence and
`explicitInitializationApproval:true`. An existing ref is never replaced.
For an empty private repository the initializer verifies the actual default
branch and creates `mailbox.json` through the Contents API without an update
SHA, then verifies the exact root commit. It never overwrites an existing file.
On populated repositories it creates only a new explicitly selected ref.
Pin the returned `genesisSha` in every private package before ordinary rounds.
An uncertain initialization must be inspected, never blindly retried.

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

Requests contain `approvedPolicy`, `native.actual` (the fresh normalized native
identity contract), `type`, a unique opaque `id`, `roundId`, `data` and, when
required, `evidence`. `inspect` returns queue state without authorizing dispatch.
`begin-round` obtains the persistent gate for the exact role; `end-round`
releases it. Different native invocations cannot share the gate. Keep event IDs
stable when reconciling a lost response. Every transition is a single-parent
Git commit extending the observed queue head with `force:false`; conflicts
require reread/revalidation, not stale-state overwrite.
Exact already-committed events remain idempotent after their observation expires;
retry the unchanged request/evidence with fresh `native.actual` observations.
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

Round gates do not expire by time. A crashed gate stays blocked. A new verified
coordinator invocation can submit `recover-coordinator-round` only with the
exact prior invocation digest and proven cessation/live/queue/history evidence.
The coordinator may `reconcile-round` for a consumer using the same proven
old-round evidence, not a heartbeat timeout. Unknown current/old identity blocks
recovery. Transaction lock or `.pending` journal remnants require explicit
inspection/recovery; never delete them merely because they are old.

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

Reconcile consumer readiness and all assignment receipts before admission.
`ready` is valid for 60 seconds and binds the exact assignment inventory.
After a reservation changes that inventory, more admission requires renewed
consumer reconciliation. Unreachable/stale consumers receive no new work;
all their reservations remain counted. Do not claim a missing worker is idle.
One-shot schedules must be coordinated so fresh consumer observations exist;
do not weaken freshness or add a polling loop to make a schedule appear ready.

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
for their own worker ID, plus their real existing local sessions/history.
Publish `ready` only after complete fresh native/queued/history inventory and
verified tooling. Every pre-existing nonterminal session must map to an admitted
assignment, except explicitly verified role automations. Unmapped or uncertain
work blocks readiness and new admission, never causes deletion.

Read assignment task requirements from the actual issue/PR and approved policy
as data. Refresh holds/ownership/head/files and recompute task identity before
kickoff. To start a published assignment submit `receipt` with the exact
`assignmentId`, `generation`, `taskDigest`, `status:"starting"` and new opaque
`correlation`, plus complete current task evidence, `holdsChecked:true` and
`ownershipReconciled:true`. This persists local delivery intent and queue receipt
BEFORE a native call.
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
The actual ID is retained locally, never serialized into the queue.

If kickoff/ack is lost, inspect native inventory, queued delivery and history for
genuinely correlated evidence. Issue text, a matching name or an idle/missing
session alone is not proof. If proof is unavailable retain the starting/uncertain
slot and report the blocker; there is no native creation idempotency token.
Resume/follow only the exact locally mapped session. Do not resend unchanged
findings or start a replacement as a shortcut.

`running`, `review`, `recovery`, `uncertain` and `terminal-reported` receipts bind
the same correlation/native session. Terminal reporting additionally requires
verified cessation, queue/history and task artifact evidence. Consumer reports
never release capacity. After fresh readiness plus terminal receipt, coordinator
`release` requires the exact consumer receipt digest, task digest, verified
ownership/artifacts and no pending continuation. Until then all slots remain
reserved. Discoveries, new prerequisites and expanded scope go back to the
coordinator before work expands.
If a terminal receipt has aged out before coordinator reconciliation, re-observe
the same native session and submit a new `terminal-reported` receipt, then refresh
readiness. This refresh still requires all terminal evidence; it cannot restart
work or release a slot by itself.

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
