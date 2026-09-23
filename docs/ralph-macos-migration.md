---
post_title: Ralph macOS destination setup and cutover
author1: PrintFarmer
post_slug: ralph-macos-migration
microsoft_alias: n-a
featured_image: ""
categories: []
tags: [ralph, macos, automation]
ai_note: true
summary: Stage an unverified Mac mini deployment without changing the source host.
post_date: 2026-09-21
---

## What the helper does

The helper stages **one mini coordinator and native device consumers** using
`--role`. Run it once for each role: coordinator and consumer on the mini, consumer
on Windows. Each gets its own disabled workflow and private configuration.
The private GitHub queue helpers implement durable reservations, round gates,
opaque receipts and native-session correlation. These are implemented packages,
**not an activated deployment**: policy review, native attestation, legacy
authority reconciliation and explicit queue initialization are still required.
V2 corrects V1's requirement for a nonexistent current-automation identity API.
It uses explicit local-owner/shared-writer trust, real isolated-worktree checks
and atomic persistent round ownership, not fabricated app metadata.

Run [setup-ralph-macos.mjs](../scripts/setup-ralph-macos.mjs) **on the destination
device**, not against source-host deployments. The historical filename is retained
and supports Windows with `--role consumer`. It is a standalone Node script;
copy that one file if the repository is not available yet. No npm install is
needed. The destination must ultimately have the complete approved repository,
including the Ralph policy, simulator resolver and its common shell library.

Validation is the default. `--apply` only stages private files and missing
directories; it does not create an app project/workflow, authenticate, install,
accept licenses, select Xcode, boot/erase simulators, run a Ralph round or enable
anything. Do not use `bootstrap-macos.sh` for this migration: that general
development helper installs/upgrades software, including when asked to verify.

| Result | Meaning |
| --- | --- |
| `clone-pending` | Read-only preliminary checks passed; no clone, policy or simulator validation yet. |
| `approval-required` | Exact candidate and controlled scope/changes displayed; no prompts/writes or repository simulator execution. |
| `validated-not-attested` | Local prerequisites and policy validated; nothing written. Deployment acceptance remains pending. |
| `staged-unverified` | Private candidate files staged. **Migration is incomplete.** No native attestation, preflight, handoff or activation performed. |
| Nonzero exit | A prerequisite, trust, path or overwrite check failed. Read the remediation; do not weaken it. |

## Start here: Copilot coordinates each device's local setup

### Bounded Specialist Dispatch And Renewal

The native agent is the PrintFarmer-owned `Ralph Worker`, defined in
`.github/agents/ralph-worker.agent.md`; the assigned Squad team member is
separately resolved from the issue's canonical owner label and loaded charter. The
`RALPH-ASSIGNED-WORKER-V1` entrypoint does that member's work in the reserved
session, without coordinator fan-out, fallback workers or hidden research agents.
The runtime generates exact native arguments and explicit model/effort before
authorizing creation. macOS Dallas remains GPT-6 Astra/xhigh.
Squad's distribution-owned agent is unchanged and is not invoked by the worker.
Native capability readback must advertise `Ralph Worker` before dispatch;
its file's presence alone does not prove registration. If unavailable, report
blocked rather than substituting Squad or the default agent.

The approval scope now includes the Ralph Worker entrypoint, member charters and
issue-lifecycle contract.
Older receipts remain valid renewal baselines, but cannot silently approve the
expanded scope. Review the displayed `newlyControlledPaths` and approve the
candidate interactively in a new private package; preserve all original bindings,
genesis and journals. Do not change live prompts as a shortcut.

Re-run preflight **and runtime inspect after attestation edits**. Full registry
validation rejects misplaced `ownerAttestation` metadata; it belongs at the host
root. Before activating renewed roles, reconcile never-started old-policy work
using the owning consumer's runtime-generated proof and coordinator withdrawal.
Retain starting/running/uncertain workers; no replacement creation is implied.

Partial startup must retain the returned handle before attempting readback.
Recover only that workspace. Requested settings on a failed kickoff are not
configuration proof. If supported native tools cannot read or restore a lost
reasoning setting, explicit owner confirmation is still necessary; no helper
can truthfully manufacture that platform capability.

See [the native role contract](../.copilot/skills/ralph-loop/native-roles.md#validated-specialist-dispatch)
for the exact requests and ACK fields. Research completion requires findings
persisted once on the issue, readback, the worker's final ACK, terminal receipt
and coordinator settlement. Only a fresh consumer offer replenishes capacity.
The coordinator appends a summary and implementation plan to the original issue
description and owns label transitions. Investigation-only research needs no
merged research PR: resolved research clears `go:needs-research`; `go:yes` is a
separate implementation-readiness decision. Research completion does not mean
the underlying bug is already fixed. Repository-changing research still needs
its reviewed, merged PR.
Local fixture results do not establish production reliability: retain evidence
of two unassisted scheduled lifecycles before calling a deployment operational.

Install and sign in to GitHub CLI and the supported Copilot app/CLI yourself.
The script checks `copilot --version`, but cannot prove app authentication,
entitlement, model availability or native execution identity. Missing tools
produce remediation instead of an installation attempt. Apple Silicon Homebrew
and user-local binary directories are searched as PATH fallbacks.

Copy the helper and guide to the mini, then paste this **one entry prompt into
Copilot on the mini**. The agent discovers and passes IDs; you do not transcribe
UUIDs or find the policy commit. The helper is not an app API client.

```text
Complete DISABLED PrintFarmer Ralph native-role setup on THIS device only, using
setup-ralph-macos.mjs and ralph-macos-migration.md.
Do not run Ralph/Reaper, migrate data, modify source-host settings or enable a schedule.
On the mini stage two distinct roles: coordinator and local consumer.
On Windows stage only its consumer. Do not attempt cross-host app operations.
Use OlyForge3D/PrintFarmer-Ralph-Control as the existing PRIVATE control repository;
discover actual numeric identity, visibility and write permission. Do not create it,
initialize content, change access or infer an ID. The owner accepts trusted approved
writers; GitHub permissions do not independently authenticate coordinator/consumer roles.
Prepare a private version-1 registry with one mini worker (macos-mobile), one Windows
worker (windows-general), actual approved GitHub writer logins, opaque stable worker
IDs and verified capability tags. Do not put paths, credentials or native IDs in it.
Use heads/main for the selected empty control repository's actual default branch,
after verifying it. Queue initialization remains separately authorized.
Use list_projects and list_workflows to discover the actual destination bindings.
Register the destination OlyForge3D/PrintFarmer repository as an app project if
needed, using supported create_project/native UI after my approval. Never copy
the source project's UUID, workflow UUID, "local" host ID or paths as proof.
Use the native automation editor/environment picker to identify this machine's
actual app environment ID if the tools cannot discover it. Do not guess.
For each role create exactly one disabled workflow only if it does not exist:
name Ralph <role> - <worker-id> - PrintFarmer; model gpt-5.6-luna; effort medium;
mode autopilot; workspace worktree; cron 40 * * * *; enabled false.
Use a temporary prompt "Setup pending: report blocked and exit. No mutations."
Use save_workflow (interval manual with the explicit cron) or the native editor.
Do not call run_workflow. Read back the saved settings and disabled state.
Inspect an app-created isolated destination session with get_session; report its
actual worktree parent and project ID. Get environment selection from the workflow
record/editor, not from get_session if that field is absent. Do not infer current
automation execution from a manual setup session or an arbitrary workflow lookup.
Carry the discovered repo path, project/workflow UUIDs, app-host ID and worktree
parent forward yourself; do not ask me to transcribe IDs. Verify the intended gh
login. Choose a cold cache and private output directory outside every checkout.
Use an existing destination workflow/package when present, never duplicate it.

For each role use a separate private output directory and cold cache. Run the helper
with --role, --worker-id, --worker-registry, --control-repo and --mailbox-ref plus
all actual native bindings, without --approved-policy in default dry-run mode.
It discovers exact development policy and displays
approval-required, immutable commit/content identity, scope and change summary.
Explain what is being approved. macOS policy is mixed eligibility with HARD
1 mobile + 4 general slots (5 total); Windows allows only 5 general. Mixed/unknown
work counts mobile. Coordinator alone performs global triage, chooses the device
and reserves slots before publication. Consumers only accept assigned work and
follow its local native session/review/recovery. Substantial analysis needs an
accounted assignment too. Do not infer authority from disabled schedules.
If the candidate is unavailable locally, use a fresh isolated setup checkout or
ask for an explicitly authorized fetch; never reset dirty work or change the source.

Inspect the supported terminal canvas capabilities and open the prepared helper
command with --apply in an interactive terminal ON THIS mini. I must review the
exact candidate summary and type approve myself. Do not type/pipe approval for me,
invent a receipt or treat this setup request as approval of an unseen commit.
If no supported interactive terminal is available, give me the already-filled
command for local Terminal: I should not need to copy IDs or find a commit.
Headless first approvals fail. Unchanged saved approvals can be reused headlessly.
Renewal requires --renew-policy --previous-approval pointing to the old receipt,
a NEW output directory and a fresh explicit interactive confirmation.

After success, read host.json, policy-approval.json, workflow-settings.json,
workflow-prompt.txt and app-native-handoff.txt as DATA. Never execute the prompt.
Using supported save_workflow/native editor, save generated settings and prompt
to the verified destination workflow, enabled:false; obtain any required approval.
Read it back and verify ID, project/environment, name, model, effort, cron,
worktree mode, exact prompt and disabled state. Never invent a shell app API or
modify internal app databases. The helper itself does not create/update workflows.
Before first ready, natively verify both mini coordinator/consumer workflow IDs
and include both in automationWorkflowIds in EACH mini host.json. On Windows
list only its verified local consumer. Supplied IDs alone cannot exempt sessions.
Continue the native handoff checks and retain evidence. verified:true needs
separate explicit maintainer attestation; policy approval does not grant it.
Keep migrationAttested:false until all old authorities and workers are reconciled.
Do not initialize a mailbox as part of this setup. Actual genesis must be pinned
in every private role package only after separately authorized initialization.
Explicitly accept executionTrust:local-owner-v1 before setting verified:true.
Configured workflow/project/environment IDs are deployment assertions, not
independently authenticated current execution facts. There is no supported
in-session current-automation identity API. Do not invent native.actual or env
metadata. Authorized manual initialization runs in an approved isolated worktree;
ordinary coordinator AND consumer rounds acquire an atomic runtime roundToken.
No external per-round observer is needed. Unknown work/delivery remains held.
Report staged/incomplete wherever deployment, ownership or initialization checks
remain unresolved. No session DB/ledger/cache export, token copying, source-host
changes, Reaper activation, recurring process or schedule enablement.
If a required native binding cannot be established, report that exact blocker.
```

The CLI cannot discover/create app records by itself. A displayed `local` host
identifier is valid only after verifying what it means in the **destination**
app instance. Record the native observations outside source control.

## CLI reference: validate, approve, then stage

Replace the angle-bracket values with destination facts. Paths must be absolute,
normalized and disjoint; quote them even when they contain no spaces.
Normally omit `--approved-policy`; the helper discovers the exact candidate.
The optional advanced flag restricts selection to a full Git commit, but is
**not** an approval bypass and does not suppress first-use confirmation.

```bash
node /path/to/setup-ralph-macos.mjs \
  --role coordinator \
  --worker-id mini \
  --worker-registry "/absolute/private/worker-registry.json" \
  --control-repo OlyForge3D/PrintFarmer-Ralph-Control \
  --mailbox-ref heads/main \
  --repo "/absolute/destination/PrintFarmer" \
  --host-config "/absolute/private/ralph-coordinator/host.json" \
  --project-id "<destination-project-uuid>" \
  --workflow-id "<destination-workflow-uuid>" \
  --app-host-id "<destination-native-environment-id>" \
  --worktree-root "/absolute/app/worktree-parent" \
  --cache-dir "/absolute/private/cold-ralph-cache" \
  --github-login "<intended-github-login>"
```

Repeat for the mini consumer using `--role consumer`, its distinct native workflow,
output directory and cache. On Windows use the same standalone helper with native
paths and Windows consumer bindings:

```powershell
node .\setup-ralph-macos.mjs `
  --role consumer --worker-id windows `
  --worker-registry 'C:\private\worker-registry.json' `
  --control-repo OlyForge3D/PrintFarmer-Ralph-Control --mailbox-ref heads/main `
  --repo 'C:\repos\PrintFarmer' --host-config 'C:\private\ralph-consumer\host.json' `
  --project-id '<actual-project-uuid>' --workflow-id '<actual-consumer-workflow-uuid>' `
  --app-host-id '<actual-native-environment>' --worktree-root 'C:\app-worktrees' `
  --cache-dir 'C:\private\cold-consumer-cache' --github-login '<approved-login>'
```

Windows requires an existing checkout and a private parent directory whose ACL
allows only the current account, SYSTEM and administrators. Setup checks ACLs
read-only and never changes them. Windows does not run Xcode/simulator probes or
clone automatically. See the protected
[native role contract](../.copilot/skills/ralph-loop/native-roles.md)
for the exact registry schema and runtime requests.

Repeat with `--apply` in an interactive terminal, review the exact commit and
controlled file identities/scope, host settings and change summary, then type
`approve` to approve **only that policy**. Refusal or no interactive terminal
writes no approval/package. There is no `--yes` or piped-input shortcut.
The candidate and local content are checked again after consent and before
writing; movement cannot substitute a different commit under the same approval.
For a fresh clone,
also supply `--clone`, selecting a **nonexistent** repo destination. Clone always
uses `https://github.com/OlyForge3D/PrintFarmer.git`; existing checkouts are never
reset, checked out, pulled or fetched. A dry-run with `--clone` reports pending
validation without cloning. If an apply-time clone fails or later validation
blocks, its partial/new directory is retained for inspection, never deleted.

Output beside `host.json`:

- `host.json`: destination role/bindings, observed numeric private-repo identity,
  registry, `executionTrust:"local-owner-v1"`, `verified:false`,
  `migrationAttested:false`; no invented genesis.
- `workflow-settings.json`: supported `save_workflow` input values, `enabled:false`.
- `workflow-prompt.txt`: approved template with destination bindings and one
  identical commit pin for the outer guard and `--approved-policy`.
- `app-native-handoff.txt`: exact destination-specific prompt for native
  attestation, read-only preflight and safe cutover.
- `policy-approval.json`: immutable pin, controlled-content digest/scope,
  confirmation timestamp and checked GitHub login; not native attestation.

Later runs reuse the saved pin without a prompt while controlled content
matches, even when unrelated development commits advance. The bootstrap pin
does not advance silently. Missing/truncated remote tree evidence fails closed.

### Renew changed policy without overwriting evidence

Changed controlled policy blocks automatic reuse. Have Copilot review the
revision, then repeat the command with `--host-config` replaced by a **new,
nonexistent** private output directory and add:

```text
--renew-policy
--previous-approval "/absolute/private/ralph-mini/policy-approval.json"
--previous-host-config "/absolute/private/ralph-mini/host.json"
--apply
```

The review shows old/new immutable commits and a controlled diff summary.
Approve interactively again. Old receipts, attested config and handoff files
remain untouched. Copilot must save the new package to the **same disabled**
destination workflow and read it back; new host config stays `verified:false`
pending separate attestation. No policy renewal activates anything.
For **existing native packages**, always include `--previous-host-config`.
Read its actual saved bindings into the filled command rather than rediscovering
new IDs. Choose a new empty observation cache and new package directory only.
The helper preserves existing workflow/project/environment/worker/role bindings,
control repository/ref/registry/genesis, migration attestation and original
`stateDirectory`; it rejects a changed authority or deployment identity.
Old approval, config, history, session mappings and claims remain untouched.
It does not copy or reset the native journal. New `verified:false` requires
explicit acceptance of the V2 trust contract and disabled workflow readback.
Renewal `workflow-settings.json` contains only `workflow_id`, the new `prompt`
and `enabled:false`. Applying it through supported `save_workflow` preserves the
live workflow name/model/effort/schedule/project/environment/workspace settings;
read those back before and after, rather than restoring fresh-install defaults.
Do not reinitialize a mailbox that already exists. If old packages have pending
rounds or initialization intents, reconcile them with retained real evidence
before proceeding; never invent a replacement token, delete history or clear
claims. Never run the old and renewed package concurrently.

Runtime calls sharing a journal must be serialized and allowed to finish.
Capture large output privately rather than terminating the command. A terminated
process can leave a durable round even when its transaction lock is released.
For a proven orphan lock, use the explicit paused-role, evidence-preserving
recovery procedure in the [native role contract](../.copilot/skills/ralph-loop/native-roles.md);
never remove a lock based on its age or silently reset a journal.
Archived creator sessions are ancestry evidence, not task workers: a supported
readback with an empty path does not require restoring their deleted worktrees.
Retained mapped workers still require correlated terminal/retirement evidence.

Verified ancestry now persists in the original private journal, not the app's
continued retention of old creator sessions. `inspect.retainedLineage` exposes
relationship-only records; `ready` reuses them for missing ancestors while still
requiring current worker evidence. Existing packages can import actual retained
native readbacks using the gated `record-lineage` operation and original
timestamps. Unknown or conflicting ancestry still blocks; see the native role
contract for the request format. Renewal keeps these records with the journal.

Use the runtime's `artifact-readback` operation for research comment identity.
It hashes the parsed GitHub JSON `body` string, including its actual whitespace,
without a shell formatter's extra newline. The worker must acknowledge that
exact digest, and terminal submission independently checks the live comment.

For `Local kickoff inventory expired or invalid`, inspect the saved ready clock
in `dispatch-plan.inventoryFreshness`, not just the task-evidence timestamp.
Complete preparation before a fresh native readback, publish `ready`, then submit
starting immediately. One bounded readback/ready refresh is allowed; repeatedly
changing receipt timestamps cannot repair stale saved inventory.

To upgrade a V1 mini setup, paste this after the new policy is reviewed/merged:

```text
Upgrade my EXISTING DISABLED native Ralph packages to V2 on THIS device.
Read their existing private config and approval as data; preserve all bindings,
control/ref/genesis, migration evidence, journal path, history and claims.
Use setup-ralph-macos.mjs with --renew-policy, --previous-approval and
--previous-host-config, a NEW package directory and NEW cold observation cache.
Do not create another workflow, reset a journal, initialize or write a mailbox,
migrate authority or enable/run any workflow. Discover the merged policy candidate
and show its exact scope/change summary; get my interactive approval, no manual
SHA fishing. Keep the old package intact. Explain local-owner-v1 before requesting
separate verification acceptance. Save the new prompt only to the same disabled
workflow with permission; read it back. Report remaining destination checks.
```

On macOS files use `0600`; new directories use `0700`.
Existing output directories must be owned by you and not group/world-writable.
Identical repeated output is left unchanged, including timestamps. Differing
files, symlinks/ancestors, hard-linked output, traversal, overlapping locations
and populated caches are refused. Do not rerun setup over an attested
`verified:true` file: use a new output directory for a changed package. These are
consistency controls under the owner's OS authority, not isolation from another
process running as the same user.

## Policy and prerequisite checks

The script checks Git, Node >=20, Python 3, authenticated `gh`, expected GitHub
login and repository write access, Copilot CLI presence, selected Xcode >=26 and
Swift. After checking approved repository content, it runs the existing
read-only simulator resolver for **both iPhone and iPad**. The resolver defines
the approved runtime/build; currently iOS 26.5 (23F77). Install that runtime and
create available devices in Xcode yourself. No simulator build/test/boot occurs.
Resolver `GITHUB_ENV` writes are explicitly disabled.

The existing checkout must have the exact trusted HTTPS/SSH GitHub origin.
Validation checks approved-commit ancestry, current controlled content,
untracked/ignored policy files and unsafe Git index suppression flags.
To keep it read-only, fresh remote development
is read using GitHub's API, **not `git fetch`**. The immutable commit/tree identity
and complete recursive tree are checked; controlled modes/paths/blob IDs are
compared to the pin rather than relying on a capped compare-file list.
Malformed/truncated tree data fails closed. Unrelated application changes do
not require reapproval. Unrelated dirty files are preserved. The resolver and
common shell library are also compared before being executed. No current-Mac
private files are needed.

Two historical source bootstrap fields had different values: outer
`POLICY_COMMIT` and preflight `--approved-policy`. Both mean an approved **Git
commit identity**, not different hash types. Their policy contents differed,
so that deployment failed closed. The helper accepts only **one** approved pin
and uses it in both places. Do not propagate the inconsistent historical prompt.

Shared `hosts.json` now contains roles, scope, capacities and model overrides,
not deployment workflow/project/app-host IDs or paths. A new destination
workflow UUID requires **no repository policy edit**. This role-only policy
change must itself be reviewed, merged into development and owner-approved
before use; an old source-host policy pin is deliberately rejected. Subsequent
policy edits require renewed pin approval, never bypassing content checks.
The helper does not authorize or update source deployments.

## Native attestation and cutover

Paste `app-native-handoff.txt` into Copilot on the mini and retain its evidence.
The maintainer must verify the actual app records, disabled workflow readback,
model availability and app-created worktree parent, then explicitly authorize
changing private `verified:false` to `verified:true`. The script has no flag
that performs this attestation.

Filesystem `preflight` can then run in an app-created isolated verification
worktree, after the saved bootstrap's origin/fetch/policy guards. It returns
`dispatchAuthorized:false` and `nativeIdentityVerified:false`. This is expected:
successful file checks are not independent native app identity verification.

Every authorized role execution uses the
[local-owner contract](../.copilot/skills/ralph-loop/bootstrap.md#local-owner-execution-contract).
Deployment IDs are explicitly accepted assertions. The runtime checks actual
filesystem/Git isolation and approved content, then atomically obtains a random
round token plus persistent mailbox role gate. The path alone is not exclusion;
two callers in the same worktree cannot independently acquire the same role.
The token is returned once, remains private, and is required for subsequent
transitions. A lost acquisition response requires reconciliation, not another
token. Native observations still establish actual work-session correlation and
terminal evidence, not unexposed current workflow identity.
If independent role packages race the mailbox head, the loser retains its
acquisition intent. Follow the native contract's `abandon-acquisition` procedure:
it records candidate/base before ref publication and permits non-authorizing
local reconciliation only when a ref write was never attempted or verified
sibling advancement makes the exact delayed candidate unable to fast-forward.
A missing event on an unchanged base remains uncertain, not safe to discard.
No journal deletion or token reissue is needed for a proven losing candidate.

Before any cutover:

1. Keep source Ralph disabled. Disabled controllers do not prove workers stopped.
2. Reconcile **all** source local claims and Windows-owned remote jobs through
   their original authorities, job IDs, digests and fences. Require proven
   terminal state or an explicitly authorized sole-owner handoff. Idle/missing
   sessions, expired time or an unreachable host are not cessation evidence.
3. Retain historical state and uncertain claims. Do not copy session databases,
   worktrees, ledgers, caches or credentials; use a cold observation cache.
4. Preserve macOS 1 mobile + 4 general and Windows 0 mobile + 5 general quotas
   without borrowing; count queued, reserved, recovery and uncertain sessions.
   Preserve the Dallas Astra/xhigh override, one Xcode job,
   source-only required reviewer panels, live claim/merge checks, and one-round
   exit. No additional controller authority or Windows mobile dispatch is added.
5. Keep Reaper disabled and separate. No launchd jobs or recurring process setup.
6. Only after native/ownership gates pass may the owner separately authorize a
   controlled round and verify real handoff receipts. Enabling the schedule is a
   **separate explicit decision**, not the result of setup or preflight.

## Coordinator and consumer contracts

The four Mac general slots are enforced by the native mailbox path after
attestation and initialization. Until those gates pass they are **not active
automatic admission**. The architecture replaces independent host
dispatchers with one coordinator on the mini and native scheduled consumers
on each device. The mini has two separate workflow roles. Consumers pull
assignments and create local app sessions; remote session creation, SSH and CLI
workers are not required.

| Role | Responsibility |
| --- | --- |
| Mini coordinator | Global triage, dependency/readiness reconciliation, device selection, durable reservations and aggregate progress. |
| Device consumer | Accept only authenticated assignments for its worker ID; create and follow local native sessions, reviews and recovery; report progress, discoveries and blockers. |

Central triage covers all repository issues, not only mobile-labelled ones.
Classify mobile/general scope and tooling requirements from evidence; validate
type, priority and Squad owner labels without assigning `jpapiez` personally.
Squad ownership identifies the responsible specialist, not the execution device.
Reconcile native dependencies, analysis gates, epic child declarations/readiness,
explicit holds and existing PR/session ownership before choosing work. Preserve
the existing epic, hold and no-duplicate rules; a blocked parent does not erase
permitted repair of an already owned PR.
`go:needs-research` triggers bounded, quota-accounted research rather than
implementation or permanent deferral. Existing research ownership is reused.
Findings, acceptance criteria, blockers and merged linked research evidence are
checked before a readiness proposal; `go:no` and human holds remain blocking.
Research completion never automatically closes the implementation issue.
The runtime's read-only `research-plan` disposition preserves these gates.

The coordinator chooses a compatible device and reserves its exact category
quota before delivery. Substantial analysis is assigned, capacity-accounted work,
not implementation secretly spawned by the coordinator outside the limits.
Consumers do not globally triage, select new issues, change device ownership or
grant replacement assignments. Findings that change scope, files, dependencies
or required capacity return to the coordinator before expanded work proceeds.
Review/recovery follow-up remains bound to the actual assignment and session;
a new execution session cannot silently reuse an occupied slot.

A private worker registry provides stable nonsecret IDs, verified platform and
capability metadata, and native bindings. Issues need no device tag. Optional
future explicit targeting cannot override compatibility, reservations or quotas.
Tags are routing hints, never authentication or ownership locks.

### Private mailbox and serialization

Use a **separate private GitHub control repository** supplied and approved by the
owner, never a branch in public PrintFarmer. The selected repository is
`OlyForge3D/PrintFarmer-Ralph-Control`; setup must not create one implicitly.
Bind its discovered canonical repository ID,
owner/name and approved mailbox ref privately; verify exact identity, private
visibility and authorized access before each queue operation. Public, wrong,
unverifiable or unexpectedly transferred repositories fail closed.

Use supported GitHub Git database APIs for assignment and receipt events.
Each candidate commit has
exactly one parent equal to the observed head; update the ref with `force:false`.
Concurrent sibling updates cannot both fast-forward. After conflict or a lost
response, reread and reconcile the same event ID before any retry. Do not treat
comments, labels or undocumented conditional HTTP behavior as compare-and-swap.
This relies on GitHub's documented
[fast-forward ref update](https://docs.github.com/en/rest/git/refs#update-a-reference)
semantics, not an arbitrary expected-SHA parameter. Focused fixtures cover
sibling conflicts, response loss and idempotent event recovery.

Generated native-role prompts require bounded same-invocation recovery for a
losing `begin-round` acquisition. Reconcile the original intent using
`abandon-acquisition`; only the runtime's `acquisitionAbandoned:true` permits
another acquisition with new round/event IDs and freshly read evidence.
Allow at most three acquisition attempts (initial plus two retries), without
polling or sleeps. Retain all intents. An uncertain or published acquisition,
failed reconciliation, or exhausted budget stops the retry path; do not delete
journals, force-release gates or retry native session creation. See the
[native acquisition contract](../.copilot/skills/ralph-loop/native-roles.md).

The private queue is the sole durable authority ledger. The coordinator persists
a reservation commit before a distinct publication transition. Its private
journal fsyncs event intent before network delivery. Assignment identity binds authority epoch, worker,
generation, task/head/requirements digest and policy pin. Receipts never confer
scheduling authority. Pending, queued, unreachable, uncertain and recovery work
retain their slots; only proven terminal/handoff evidence permits release.
Even in the private repository, exclude native session IDs, private paths,
credentials and prompts. Publish opaque assignment correlations and lifecycle
receipts; the consumer keeps the actual native-session mapping locally.

Each role needs a persistent round gate bound to its atomic runtime acquisition;
native scheduling is not assumed single-instance. Transaction locks alone do
not span native tool calls. A competing round exits, and elapsed time alone
cannot reclaim an uncertain round. Persist consumer delivery intent before
creating a session and correlate the actual native session response afterward.
A lost response blocks redelivery until genuine native evidence reconciles it:
there is no exposed idempotency key for native session creation.

### Asynchronous capacity and completion

Native inventory covers **Ralph-owned lineage across rounds**, not all sessions
in the destination project. Consumers reconcile their private recorded delivery
intents, assignment-backed workers and descendants; the coordinator tracks those
assignments through the shared mailbox. Independent maintainer and separate
automation/acceptance sessions neither consume Ralph credits nor require terminal
attestation. Do not delete or adopt unrelated sessions to unblock admission.

`ready` requires `ownershipScope:"ralph-owned-v1"` and `lineageChecked:true`
backed by actual readback, retained creation/ACK records and ancestry. Normalize
native `creator_session_id` to `session.creatorSessionId`; include intermediate
ancestors. Retain unresolved create intents even without a returned native ID,
and preserve terminal mappings to detect later resumption. Missing live workers,
unknown owned descendants, lost ACKs and resumed terminal workers remain blockers.
This does not relax task ownership, dependency, overlap or category quota checks.
Every retained mapping needs an explicit fresh inventory entry, including terminal
workers. Owned ancestry must be complete and acyclic, without adopting unrelated
ancestors or requiring them to finish. Archived/deleted terminal workers may use
current verified cessation plus retained correlated terminal evidence through
`session.retirementObservation` as specified in the
[native role contract](../.copilot/skills/ralph-loop/native-roles.md).
Omission, idle, archive status or failed lookup alone never proves cessation.

This scope correction changes controlled runtime/policy files. Existing packages
must undergo the documented immutable policy review and explicit interactive
renewal before adopting it. Do not merely filter old-runtime evidence or patch
a live prompt to bypass the prior inventory contract.

The generated coordinator prompt requires completing missing-label triage
before admission, using current issue bodies, comments, linked evidence and the
[canonical label vocabulary](../.copilot/skills/ralph-loop/operations.md#authoritative-label-vocabulary).
Add justified missing type, priority and dispatch-owner labels through supported
GitHub tools; preserve valid classifications, ownership and all holds. Do not
blanket-label research issues as spikes or invent priorities to fill slots.
Record a concise rationale/first step once, then re-read GitHub and evaluate
same-round admission from the actual labels. Ambiguous classifications and failed
writes require a specific blocker, not fabricated evidence. Consumers never
perform this global triage. Substantial investigation still needs a reservation.

Setup and renewal generate these directives; they do not update saved app
workflows automatically. Existing installations need a supported native prompt
update and readback, preserving their bindings, policy pin and schedule. Do not
manually alter approval receipts or run generated prompts as shell commands.

Consumer `ready` is a finite durable offer, not an online heartbeat. It atomically
replaces that worker's remaining mobile/general credits from hard limits minus
all outstanding assignments. Coordinator reservations consume those credits;
one mini offer permits four general assignments without intervening consumer
rounds. Offline workers can receive only unused pre-offered credits. No timer,
withdrawal, release or duplicate replay refunds them. `unavailable` and blockers
revoke unused credits, not ownership. New offers require actual reconciliation.

Offers bind worker, authority/registry, capabilities and approved policy; a changed
pin cannot spend an old offer. Outstanding work remains owned during renewal.
New acceptance requires same-policy, current-round local inventory and capability
revalidation within 60 seconds. Other assignment/receipt/blocker changes require
another real local recheck. This local bound never requires separately scheduled
coordinator/consumer rounds to start within a minute of one another.

For example, mini may offer at 10:45, coordinator reserve at 11:48 and publish at
11:56, and mini accept at 12:49 after fresh local checks. Kickoff/readback may take
minutes; its starting reservation already holds the slot. A subsequent consumer
round follows the same session and records its final ACK plus sole-sender
no-future-delivery commitment. The coordinator may settle days later with fresh
artifact/delivery reconciliation and no consumer readiness refresh. Known
resumption, reassignment or uncertain delivery blocks release; elapsed time never
proves cessation. The consumer needs a later reconciled offer to reuse freed
capacity. No polling, synchronized schedules or longer TTL is required.

Old history hashes and genesis remain unchanged: new semantics use additive
mailbox events behind the existing public requests. Old terminal receipts must
be re-observed and upgraded to an explicit final-delivery commitment before
delayed settlement; old reservations under a different policy need safe
never-delivered withdrawal/re-reservation, never replacement of an active worker.

The explicitly accepted provenance contract trusts authorized writers of the
private control repository as the same
owner's agents. Each publisher verifies its actual authenticated GitHub principal
and live write permission against the approved private registry; consumers verify
repository identity, visibility, linear history and allowed assignment state
transitions. Git author fields and a payload's claimed role are not authenticated
writer identity. Any trusted repository writer can technically forge another
role's event; role separation is enforced by the approved helpers and workflows,
not by GitHub path permissions or a cryptographic boundary.

That accepted shared-principal model needs no new signing infrastructure and
must not be described as independent coordinator authentication.
If mutually untrusted writers must be distinguished, a separate
credential/authorization or signing design is required. Neither signing nor key
provisioning has been approved; no keys, repositories or mailbox refs are created
by current setup.

Before authority migration, reconcile the legacy Windows ledger, source
controllers and all historical workers under their original ownership. Preserve
history and require terminal evidence or explicitly authorized fenced handoff;
disabled schedules alone are insufficient. No new authority runs concurrently
with the old one. The current general-admission blocker remains until this
reviewed native policy and separately authorized migration are ready. The old
planner's general-admission blocker is retained for legacy deployments; native
role packages use the new queue path, never a bypass of the old ledger.

### Explicit initialization of the empty private repository

Repository creation alone does not authorize mailbox writes. Only after legacy
authority reconciliation and native binding verification may the owner attest
`migrationAttested:true` and `verified:true` in the private configuration.
These are prerequisites, not queue-write authorization. With separate explicit
owner approval, use a manual setup session/terminal in an approved isolated
worktree with the coordinator package. Its normalized
private runtime request uses `type:"initialize"`,
`explicitInitializationApproval:true` and the approved policy,
plus fresh `evidence` containing `source`, `observedAt`,
`legacyAuthoritiesReconciled:true` and `cessationOrFencedHandoffProven:true`.
Populate these from retained real evidence, never assertions of convenience.

```bash
node scripts/ci/ralph-native-runtime.mjs \
  --host-config "/absolute/private/ralph-coordinator/host.json" \
  < "/absolute/private/initialize-request.json"
```

For an empty control repository the helper verifies the actual default branch,
then uses GitHub's Contents API to create `mailbox.json` **without an update
SHA**. It reads back and verifies the exact root genesis and single-file tree.
For a populated repository it only creates a new explicitly selected ref.
Existing refs/files are never overwritten; a lost response requires inspection,
not another initializer run. No other content, branch or repository is created
as a fallback. Pin the verified returned `genesisSha` in all private role
configurations. Initialization does not enable any workflow.

This supported manual bootstrap uses the same policy/isolation/private-repository
and migration checks as normal rounds; it does not bypass them or require an
unavailable current-automation API. The runtime persists initialization intent
before network writes. An existing or uncertain queue requires read-only
inspection and explicit reconciliation, not repeated initialization.

## Capability evidence and destination acceptance

The V2 contract deliberately distinguishes observed capabilities from assertions.
The following evidence was obtained on the development Mac using supported tools,
not by accessing internal app databases. It is **not a mini/Windows production
acceptance report**.

| Operation | Evidence | Limit |
| --- | --- | --- |
| Project/worktree discovery | Real `list_projects`, `get_session`, `list_sessions_and_chats` returned repository/project/path/session type. Git verified this session's registered linked worktree. | No current workflow/environment association returned by `get_session`. IDs are deployment assertions. |
| Saved workflow readback | Real `list_workflows` returned projectId, hostId, prompt, model/effort, cron, disabled state and latestRun.sessionId. | Historical/external run correlation, not authenticated current execution. Saving/running workflows was not exercised. |
| Native kickoff | Real harmless `create_session` returned a handle; session readback matched project/repository/isolated path; worker returned the exact kickoff correlation. | No real task or mailbox assignment created. Native creation has no exposed idempotency key. |
| Follow-up | Real `send_session_message` to the same handle returned acceptance; worker acknowledged the new correlation and retained the original. | Acceptance alone is not delivery; retain the worker ACK. |
| Status/history | Real status inventory showed the bounded child idle. Supported local history returned kickoff+reply; cloud history returned no rows and later history lagged. | Idle/history absence is never terminal or global queue-empty proof. |
| Interactive terminal | This child context's output reads failed `Terminal not found or not running`. A separate parent-chat canvas returned actual TTY detection and a harmless non-approval input roundtrip. | Works in some app contexts, not verified on mini/Windows. If actual open/read fails, use the user's local interactive Terminal. Neither probe was human policy consent; never pipe/type approval for them. |
| Init, reservations, two-device lifecycle, round exclusion/recovery | Focused Node tests exercise manual initialization, hours-delayed independent rounds, finite offers, live local acceptance and days-delayed terminal settlement without `native.actual`. Real local files/locks and Git checks are exercised. | GitHub mailbox transport and native lifecycle observations are simulated. No live mailbox write or timer tested; modeled time is not destination evidence. |
| Mini, Windows and scheduled execution | Not run. | **NOT VERIFIED**: destination permissions, ACLs, tooling, saved prompt execution and lifecycle need separately authorized acceptance. |

`queueChecked` and `noPendingContinuation` refer to the **owning consumer's
retained delivery records and worker ACKs**, not an unsupported global pending
queue query. The consumer is the single sender for managed sessions. Known
external intervention or lost delivery evidence holds capacity. Under the
accepted trusted-owner boundary, the owner must not queue unrecorded work into
managed sessions. Completion uses actual correlated terminal ACKs plus current
session/artifact observations; delayed history can be reconciled from retained
ACKs rather than requiring impossible global queue-emptiness proof.

Before calling a destination operational, with separate approval for each live
step:

1. Discover/read back its real project, disabled role workflows, environment and
   isolated worktree. Confirm interactive policy consent in its local terminal.
   Renew existing packages as above, preserving IDs, genesis, state and claims.
2. Run policy/isolation preflight in that destination's isolated worktree.
   Verify platform, private permissions/Windows ACLs, tooling and explicit owner
   acceptance; reconcile existing ownership and migration evidence.
3. Inspect the actual private repository/ref/history read-only. Only if empty
   and separately authorized, initialize once through the manual runtime path;
   pin the verified genesis in all packages. Never assume the repository is empty.
4. Separately authorize a bounded controlled round: each consumer acquires
   `begin-round`, retains its token, inventories known work/deliveries and publishes
   `ready`; coordinator acquires its gate and reserves/publishes one harmless
   eligible assignment. Consumer obtains one creation authorization, records the
   actual native handle/ACK and follows that exact session.
5. End/restart both role rounds with new tokens while preserving assignment
   ownership. Reconcile a correlated terminal ACK, all owned deliveries and
   artifacts and sole-sender no-future-delivery commitment, then let a later
   coordinator release without a synchronized consumer refresh. Demonstrate
   competitor rejection and lost-response holds without repeating native creation.
6. Inspect actual scheduled-context behavior separately before schedule activation.
   No production-ready claim until these destination checks pass. Unavailable
   observations get an exact blocker, never a fabricated successful boolean.

### Next bounded acceptance: mini general only

This is an approval plan, **not execution authorization**. Windows, mobile/Xcode
and timer firing remain separate, explicitly unverified follow-ons, not
prerequisites to demonstrate the core mini general path.

Approved-but-unmerged code cannot pass the unchanged production ancestry/content
guards. Do not forge origin, weaken the pin, mock preflight or enable a bypass.
First finish required source checks and exact-head reviews, then obtain separate
source-merge approval. Merge is not activation or destination acceptance. On mini,
discover the merged candidate and obtain the maintainer's real interactive
consent using the documented setup/renewal commands above.

The production control repository was observed to have no branches during
planning; recheck read-only before any later decision. Its initializer cannot
create a nondefault smoke ref while empty. **Do not initialize production main
for testing.** Approval must instead name a separate private test repository,
its harmless seed commit and a new absent `heads/smoke-native-2939` ref. Verify
canonical numeric identity/private access and absence of the test ref, then
initialize only that ref. If this scope is not approved or prerequisites fail,
stop before initialization, not after changing production state.

Approval must explicitly cover these writes:

- Creating/seeding that private test repository and creating/non-force-updating
  its named smoke ref, with no production repository/ref writes.
- New TEST ONLY private mini package/config/cache/journal/evidence files and two
  disabled manual workflows (coordinator and consumer); existing packages,
  workflow IDs/history and production claims remain untouched.
- One real explicitly designated smoke-only eligible issue, or creation/labels
  for `TEST ONLY: Ralph native lifecycle acceptance`, with bounded read-only
  analysis, no personal assignee and no real issue ownership mutation.
- Manual runs of those disabled workflows, app-created role/task sessions and
  worktrees, one correlated follow-up to the same child, and archival only of
  verified clean terminal smoke sessions.

Prerequisites on mini: actual project/environment selection and saved workflow
readback; private path ownership/permissions; real registered isolated worktrees;
Node/Git/GitHub tooling; complete native inventory and original-owner
reconciliation; exact policy consent and shared-writer/local-owner acceptance.
The registry's required Windows entry is a deployment declaration only: issue
no Windows offer, start no Windows workflow and claim no verified Windows
execution. Permit only the named smoke issue; no global backlog mutations.

Save test workflows with `enabled:false`, `interval:"manual"` and
`clear_cron_expression:true`; read these back. Fresh setup artifacts contain
production cadence defaults: do not apply their full settings to a manual smoke.
Use `run_workflow` on these disabled workflows only after approval. If the app
refuses disabled manual execution, retain the exact error and stop; do not
silently enable a schedule.

1. In the approved isolated mini setup worktree, submit the corrected manual
   `initialize` request above; retain its intent/result and pin the test genesis.
2. Manually run the disabled consumer workflow. Its real app-created session
   acquires a round, inventories local work, publishes `ready` and ends.
3. At a later independent invocation, manually run the coordinator workflow:
   acquire, reconcile, reserve/publish only the designated issue, then end.
4. Run the consumer independently: acquire a new token, recheck local inventory
   and tooling with `ready`, then recheck the issue and submit starting `receipt`.
   Only its first creation authorization may create the bounded child. Retain
   actual returned handle/readback/kickoff ACK, report running and end.
5. A later consumer run follows that same child with one recorded correlation,
   reconciles its final ACK, all owned deliveries and clean artifacts, reports
   terminal with `noPendingContinuation`, `noFutureDelivery` and
   `finalDeliveryCorrelation`, and ends.
6. A later coordinator run reconciles and releases the exact terminal binding.
   Do not refresh consumer readiness to satisfy release. Read back ended gates,
   terminal assignment, unchanged production refs and disabled manual workflows.

External `run_workflow` results plus `get_session` establish actual run-session,
project and worktree correlation; filesystem/Git and saved workflow readback
establish the remaining configured environment checks. Retain those raw private
responses. They are not in-session authenticated workflow identity. Retain the
policy receipt, test genesis/history, journals and delivery ACKs. Manual runs
demonstrate app-created workflow execution, **not timer firing**.

On uncertainty retain the original gate/claim/intent and report the exact
blocker; never force-release or retry native creation. Keep disabled test
workflows and test ref/packages for audit. Deleting refs/workflows/packages,
closing the smoke issue or enabling any schedule requires separately named
approval. This sequence must run on the destination before any operational
acceptance claim.

## Focused development checks

Use the existing Node test runner from the repository root; no dependency
installation, full app build or simulator tests are needed:

```bash
node --test scripts/ci/tests/test-setup-ralph-macos.mjs \
  scripts/ci/tests/test-ralph-automation.mjs \
  scripts/ci/tests/test-ralph-host-capacity.mjs \
  scripts/ci/tests/test-ralph-mailbox.mjs \
  scripts/ci/tests/test-ralph-github-snapshot.mjs
```
