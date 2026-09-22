---
name: "ralph-automation-bootstrap"
description: "Deployment contract for two host-bound instances of the shared Ralph policy."
---

## Saved Prompt

Deploy only after the reviewed policy is merged into `development`. Save the same
short prompt below in each automation, changing only the four bindings.
Do not enable either schedule as part of this migration. Keep cron, workspace
type, model and all other workflow settings unchanged. Windows has been exported
and verified against its native automation identity, admission ledger and paths;
its schedule stays disabled until the owner separately authorizes enabling it.

```text
Run one PrintFarmer Ralph round, then report and exit. No polling or sleep.
Bindings: HOST=<profile-id>; WORKFLOW=<exact-workflow-id>;
HOST_CONFIG=<approved-absolute-private-json-path>; POLICY_COMMIT=<approved-full-SHA>.
Use only this automation's isolated worktree, never the main checkout.
Before executing repository code, verify origin is OlyForge3D/PrintFarmer on
github.com, fetch origin development, verify POLICY_COMMIT is an ancestor of
the exact fetched FETCH_HEAD SHA, and verify both current and that fetched SHA's versions of
.copilot/skills/ralph-loop, .github/copilot-instructions.md, .squad/config.json,
 .github/agents/squad.agent.md and '.squad/agents/*/charter.md',
and scripts/ci/ralph-*.mjs plus scripts/ci/verify-squad-verdict.mjs are identical
to POLICY_COMMIT (git diff --exit-code; quote 'scripts/ci/ralph-*.mjs' as a Git
pathspec). Reject all untracked files in those paths, including ignored files,
and reject assume-unchanged/skip-worktree index flags on controlled files.
On any missing/stale/changed policy or failed verification, report and exit;
do not install, rewrite or fall back to a machine-local policy.
Run node scripts/ci/ralph-automation.mjs preflight --host HOST --workflow WORKFLOW
--host-config HOST_CONFIG --approved-policy POLICY_COMMIT.
Read .copilot/skills/ralph-loop/automation.md and the returned host profile.
Preflight is filesystem/configuration validation only, never dispatch authority.
Workflow/project/environment IDs are owner-configured deployment assertions.
Copilot does not expose a supported in-session current-automation identity API.
Never invent native.actual or require undocumented metadata. Native roles use
explicit local-owner-v1 acceptance and atomic round acquisition described below.
Follow that common policy and its conditional references for exactly one round.
No schedules, saved prompts, host config or deployed workers may be changed here.
```

Use actual argument values, not shell environment interpolation assembled from
PR or issue text. The saved approved SHA is a deployment pin. A policy-path change
on development intentionally fails closed until the owner reviews and updates
both saved pins. Unrelated application commits do not require a policy update.
The checks are consistency controls under the owner's OS/GitHub authority, not
cryptographic isolation from another process with that same authority.

## Private Host Configuration

Create outside all checkouts during an explicitly authorized deployment. Use
live exported workflow/project identity and actual host paths; never copy one
machine's paths to the other or put SSH addresses, credentials or private paths
in shared policy. The common host profile limits behavior; this file binds it.

```json
{
  "host": "macos-mobile",
  "workflowId": "<exact-existing-workflow-id>",
  "appHostId": "<native-host-id>",
  "projectId": "<native-project-id>",
  "worktreeRoot": "<absolute-existing-session-worktree-parent>",
  "cacheDirectory": "<absolute-existing-repository-cache-directory>",
  "verified": true
}
```

`verified` means a maintainer checked those facts, not that a historical prompt
was found. Shared `hosts.json` contains host roles, capabilities and limits only;
workflow/project/app-host IDs and paths belong exclusively in private deployment
configuration. A new destination workflow UUID does not require a shared-policy
edit. Export the actual workflow on its owning host,
preserve its paths, explicit model/effort and kickoff requirements, confirm the
existing Windows-owned admission ledger and its remote records, and reconcile
the old mobile-dispatch overlap before activating its profile. Preserve its
process-only SSH configuration; never install/restart the worker from a round.

## Local Owner Execution Contract

`preflight` validates local config shape, its CLI workflow binding and approved
Git content. It always returns `dispatchAuthorized:false`,
`nativeIdentityVerified:false`, `localContext` and `deploymentAssertions`. Neither matching
supplied strings nor `verified:true` proves app identity.

The old `native.actual` / `identity-check` contract required an API the supported
app does not expose. It is retired, not emulated. A workflow lookup proves saved
configuration, not which workflow launched this session. No environment variable,
private app database, CLI RPC or external per-round babysitter replaces it.

For native roles, the maintainer explicitly accepts `executionTrust:"local-owner-v1"`
and verifies the package before setting `verified:true`. Approved local code,
private owner-controlled configuration and accepted GitHub writers are the trust
boundary. The bootstrap validates the actual origin, approved code, host platform
and canonical **registered linked Git worktree**, beneath the configured parent.
`localContext` is filesystem context/isolation evidence, never invocation proof.

Before **any round mutation**, the runtime atomically acquires the persistent
mailbox role gate and returns a random `roundToken` exactly once. Preserve it
privately for that round. A second execution in the same worktree cannot acquire
the occupied gate or recover a token by replaying `begin-round`. All transitions
need the token, matching filesystem context and mailbox ownership. Event IDs
deduplicate lost acknowledgments; replay never reauthorizes native creation.
Different sequential rounds acquire different tokens. Lost acquisition responses
require proven cessation and explicit recovery, never age-based lock stealing.
Tokens are capabilities within the trusted local account, not protection against
another process already holding that account's files and GitHub credentials.

Supported `list_projects`, `get_session` and session inventory remain useful for
setup and work-session correlation. `run_workflow` can externally return run IDs,
but is not required by an ordinary round. Their outputs must be recorded as
observed, not expanded into fields they do not return. See
[native-roles.md](native-roles.md) for bootstrap, recovery and evidence limitations.

The new policy commit must retain the existing admission repairs. The historical
Windows prerequisite commits are `ee1d6b1341c7ce3e99f9f9338c276a084a095194`,
`837e362e379d92826e8c5549d889c60fbffef30c`,
`f2e428af37a28206dc5e9b3543bca896ae348c72`,
`9c642f7fd350c34dfd350fa1f8855bde38b169d0` and
`69311cabbd8e6aee184d28285944e3dc4641dda2`.
Do not blindly retain an ancestry-only check across rewritten history: the
`69311cab...` merge exists (PR #2726), but is not an ancestor of the inspected
development snapshot `8da8a879cea682fff5ee034521151ac9b355218a`. The following
paths compare **byte-for-byte equal** between those two commits:

- `scripts/ci/ralph-admission.mjs`, `ralph-macos-ssh.mjs` and `ralph-macos-worker.mjs`
- `scripts/ci/tests/test-ralph-macos-ssh.mjs`, `test-ralph-macos-worker.mjs`
  and `test-ralph-local-session-completion.mjs`
- `.copilot/skills/ralph-loop/operations.md` and `session-terminal-contract.md`

The #2923 change adds separate PR admission to that preserved baseline; it does
not replace terminal completion, remote attestation or atomic handoff. Its
required source reviewers must inspect this compatibility transition and the
unchanged lifecycle regression evidence before a maintainer approves the new
merged policy pin. **Until that approval, keep the legacy workflow disabled;
do not simply delete its prerequisites or substitute a random current SHA.**
The approved replacement pin plus per-round ancestor/content guard then replaces
the historical duplicated prompt/commit list, not any of its safety contracts.

## Category Capacity Gate

Before new issue, analysis or replacement PR-session admission, run
`node scripts/ci/ralph-automation.mjs capacity-check < capacity-observations.json`.
The helper is observation-only, never a reservation/dispatch authorization.
Both new-issue admission and PR planning use the same hard limits from
`hosts.json`: macOS 1 mobile + 4 general, Windows 0 mobile + 5 general, each
5 total. Unused category slots cannot be borrowed. Mobile is a work/session
category, not merely a concurrent Xcode invocation; one Xcode job is additional.

Input is `{"host":"macos-mobile","candidate":...,"inventory":...}`.
`candidate` supplies scope and evidence (`files`, `labels`, `acceptanceCriteria`);
general classification requires `scope:"general",classificationComplete:true`
and no mobile signals. Mixed/unknown/incomplete classification reserves mobile.
Inventory requires fresh `observedAt`, genuine `source`, `complete:true`,
`historyChecked:true`, `queueChecked:true`, `reservationsChecked:true`,
`remoteOwnershipChecked:true`, and `work` records. Each record has actual
`jobId` and/or `sessionId`, `executionHost`, `state` (reserved/queued/active/
uncertain/terminal), and the same classification fields. Include legacy
Windows-owned remote mobile workers on their actual execution host; unknown
execution placement blocks proof of free capacity. Duplicate job/session
aliases are one slot; conflicting aliases/hosts fail closed. Only verified
terminal records (`terminalVerified:true` with real underlying evidence) leave
the count. Idle, absent or elapsed time is not termination evidence.

PR planner input below additionally requires `capacity` containing that same
fresh inventory. It reserves candidate slots within the plan and retains actual
live-owner slots. Re-check immediately before real admission. Mac general PR
replacement/new admission is explicitly blocked pending a shared atomic
Windows-authority transport; category availability is not cross-host safety.

## Recovery Planner Input

Use the existing snapshot/pagination helpers and live App observations; do not
rerun complete scans for each PR. The planner consumes compact JSON on stdin and
does not fetch, dispatch, claim or merge. It trusts the controller to obtain the
described fresh evidence. Keep scratch input in session artifacts, not the repo.

```json
{
  "host": "macos-mobile",
  "scope": "mixed",
  "capacity": {
    "observedAt": "<fresh-ISO-timestamp>",
    "source": "<native-inventory-queue-history-and-ledger-evidence>",
    "complete": true,
    "historyChecked": true,
    "queueChecked": true,
    "reservationsChecked": true,
    "remoteOwnershipChecked": true,
    "work": []
  },
  "pulls": [{
    "number": 123,
    "state": "open",
    "draft": true,
    "sameRepository": true,
    "scope": "mobile",
    "headSha": "<40-character-live-head>",
    "labels": ["squad"],
    "files": ["mobile/path.swift"],
    "filesComplete": true,
    "verdict": {
      "headSha": "<same-head-verified-by-the-verifier>",
      "classification": "CHANGES_REQUESTED",
      "reason": "<verifier-output-and-accepted-finding-references>"
    },
    "failedChecks": []
  }],
  "ownership": {
    "123": {
      "host": "macos-mobile",
      "state": "inactive",
      "observedAt": "<fresh-ISO-timestamp>",
      "source": "<actual-inventory-history-queue-and-admission-observation-reference>",
      "inventoryChecked": true,
      "historyChecked": true,
      "queueChecked": true,
      "noPendingDelivery": true,
      "admissionReconciled": true
    }
  }
}
```

For live ownership use `state:"live"` and the real `sessionId`/host; do not infer
inactive from idle or timeout. Unknown/missing observations remain held. An
inactive observation is usable only on its owning host and within 60 seconds;
that freshness bound **never declares anyone dead**. Include other-host PRs and
all renamed file paths so conflicts cannot disappear through scope filtering.
Bind the actual verifier output to the head it verified; never copy its
classification onto another SHA. A pending/invalid verdict is not approval.
The empty capacity `work` example is valid only after actual complete inventory
proves it empty; never copy it as a default. Populate existing/queued/uncertain
work and actual execution hosts, including legacy Windows-owned remote jobs.

Process `ready` before new slices; `ready` means **candidate**, not dispatched.
`inFlight` identifies real existing ownership; `blocked` and `deferred` retain
the exact reason. Apply the existing capacity/admission protocol and refresh
facts again before delivery. Independent new-issue work may proceed only after
recovery dispositions and intended-file conflicts are checked; no global
"all PRs must finish" requirement is implied.

On the verified Windows authority, reserve eligible general PR candidates with
[`reserve-local-pr`](pr-recovery-admission.md). Its explicit PR/head/work binding
is distinct from new-issue admission. Only a newly created reservation permits
first delivery; exact replay authorizes reconciliation, never a second kickoff.

## Rollout And Verification

Run focused validation from the repository root:

```bash
node --test scripts/ci/tests/test-ralph-pr-recovery.mjs \
  scripts/ci/tests/test-ralph-automation.mjs \
  scripts/ci/tests/test-ralph-round-cache.mjs \
  scripts/ci/tests/test-ralph-pr-admission.mjs \
  scripts/ci/tests/test-ralph-macos-ssh.mjs \
  scripts/ci/tests/test-ralph-local-session-completion.mjs \
  scripts/ci/tests/test-ralph-macos-worker.mjs
```

Update the existing disabled macOS workflow only after publication/approval and
live binding verification. Do not claim Windows updated until that exact saved
workflow is read back on its host. Verify one manually invoked round's actual
handoff receipts before enabling schedules, separately authorized by the owner.
Do not invoke a round merely to prove the scanner works while repair owners are
active. Test fixtures prove selection/guards, not production worker delivery.

`squad watch --execute` is **not this entrypoint**. Installed Squad 0.11.0 filters
blocked/assigned issues before reading `.squad/ralph-instructions.md`, and does
not dispatch from its PR report. No upstream patch, fork or terminal watcher is
part of this change. Keep that watcher stopped during coordinated automation
rollout; stopping it does not prove its children stopped.
