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
| `validated-not-attested` | Local prerequisites and policy validated; nothing written. Native identity remains unverified. |
| `staged-unverified` | Private candidate files staged. **Migration is incomplete.** No native attestation, preflight, handoff or activation performed. |
| Nonzero exit | A prerequisite, trust, path or overwrite check failed. Read the remediation; do not weaken it. |

## Start here: Copilot coordinates each device's local setup

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
actual worktree parent, project ID and native environment ID. Do not infer current
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
A setup chat is not a current automation invocation. When a round is separately
authorized, supported native current-execution identity must be available or it
must stop before mutations. No arbitrary workflow lookup can substitute.
Report staged/incomplete wherever identity, ownership or initialization checks
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
  registry, `verified:false`, `migrationAttested:false`; no invented genesis.
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
--apply
```

The review shows old/new immutable commits and a controlled diff summary.
Approve interactively again. Old receipts, attested config and handoff files
remain untouched. Copilot must save the new package to the **same disabled**
destination workflow and read it back; new host config stays `verified:false`
pending separate attestation. No policy renewal activates anything.

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

Every authorized **automation invocation** must separately establish its
**current executing workflow/project/app-host/session association** through
supported native tools/runtime metadata. A manual chat or lookup of a supplied
workflow ID is insufficient. If the app cannot expose current execution identity,
the round is blocked; do not fabricate it. See the
[native identity gate](../.copilot/skills/ralph-loop/bootstrap.md#native-identity-gate)
and its `identity-check` comparison contract. Caller-supplied JSON is not
authenticated evidence; the controller must acquire genuine fresh native facts.

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

The private queue is the sole durable authority ledger. The coordinator persists
a reservation commit before a distinct publication transition. Its private
journal fsyncs event intent before network delivery. Assignment identity binds authority epoch, worker,
generation, task/head/requirements digest and policy pin. Receipts never confer
scheduling authority. Pending, queued, unreachable, uncertain and recovery work
retain their slots; only proven terminal/handoff evidence permits release.
Even in the private repository, exclude native session IDs, private paths,
credentials and prompts. Publish opaque assignment correlations and lifecycle
receipts; the consumer keeps the actual native-session mapping locally.

Each role needs a persistent round gate bound to its actual native invocation;
native scheduling is not assumed single-instance. Transaction locks alone do
not span native tool calls. A competing round exits, and elapsed time alone
cannot reclaim an uncertain round. Persist consumer delivery intent before
creating a session and correlate the actual native session response afterward.
A lost response blocks redelivery until genuine native evidence reconciles it:
there is no exposed idempotency key for native session creation.

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
owner approval, run one controlled native
coordinator invocation with fresh current-execution evidence. Its normalized
private runtime request uses `type:"initialize"`,
`explicitInitializationApproval:true`, the approved policy and `native.actual`,
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

If supported native tools cannot prove the current executing coordinator,
initialization and all rounds stay blocked. No synthetic native observations or
direct manual shell bypass are provided.

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
