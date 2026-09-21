---
name: "ralph-automation-bootstrap"
description: "Deployment contract for two host-bound instances of the shared Ralph policy."
---

## Saved Prompt

Deploy only after the reviewed policy is merged into `development`. Save the same
short prompt below in both existing automations, changing only the four bindings.
Do not enable either schedule as part of this migration. Keep cron, workspace
type, model and all other workflow settings unchanged. Windows is not live-verified
and its checked-in profile intentionally blocks execution until reconciled.

```text
Run one PrintFarmer Ralph round, then report and exit. No polling or sleep.
Bindings: HOST=<profile-id>; WORKFLOW=<exact-workflow-id>;
HOST_CONFIG=<approved-absolute-private-json-path>; POLICY_COMMIT=<approved-full-SHA>.
Use only this automation's isolated worktree, never the main checkout.
Before executing repository code, verify origin is OlyForge3D/PrintFarmer on
github.com, fetch origin development, verify POLICY_COMMIT is an ancestor of
origin/development, and verify both current and origin/development versions of
.copilot/skills/ralph-loop, .github/copilot-instructions.md, .squad/config.json,
and scripts/ci/ralph-*.mjs plus scripts/ci/verify-squad-verdict.mjs are identical
to POLICY_COMMIT (git diff --exit-code). Reject untracked files in those paths.
On any missing/stale/changed policy or failed verification, report and exit;
do not install, rewrite or fall back to a machine-local policy.
Run node scripts/ci/ralph-automation.mjs preflight --host HOST --workflow WORKFLOW
--host-config HOST_CONFIG --approved-policy POLICY_COMMIT.
Read .copilot/skills/ralph-loop/automation.md and the returned host profile.
Compare host/workflow/project bindings to the live native automation identity.
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
was found. The historical Windows workflow ID in `hosts.json` is a discovery
hint, not proof of current configuration. Export that workflow on Windows,
preserve its paths, explicit model/effort and kickoff requirements, confirm the
existing Windows-owned admission ledger and its remote records, and reconcile
the old mobile-dispatch overlap before activating its profile. Preserve its
process-only SSH configuration; never install/restart the worker from a round.

The new policy commit must retain the existing admission repairs. The historical
Windows prerequisite commits are `ee1d6b1341c7ce3e99f9f9338c276a084a095194`,
`837e362e379d92826e8c5549d889c60fbffef30c`,
`f2e428af37a28206dc5e9b3543bca896ae348c72`,
`9c642f7fd350c34dfd350fa1f8855bde38b169d0` and
`69311cabbd8e6aee184d28285944e3dc4641dda2`.
At deployment verify they are ancestors of the approved policy commit. Its
subsequent per-round ancestor/content guard replaces the duplicated long prompt,
not those repairs or their preserved terminal/atomic-handoff safety contracts.

## Recovery Planner Input

Use the existing snapshot/pagination helpers and live App observations; do not
rerun complete scans for each PR. The planner consumes compact JSON on stdin and
does not fetch, dispatch, claim or merge. It trusts the controller to obtain the
described fresh evidence. Keep scratch input in session artifacts, not the repo.

```json
{
  "host": "macos-mobile",
  "scope": "mobile",
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

Process `ready` before new slices; `ready` means **candidate**, not dispatched.
`inFlight` identifies real existing ownership; `blocked` and `deferred` retain
the exact reason. Apply the existing capacity/admission protocol and refresh
facts again before delivery. Independent new-issue work may proceed only after
recovery dispositions and intended-file conflicts are checked; no global
"all PRs must finish" requirement is implied.

## Rollout And Verification

Run focused validation from the repository root:

```bash
node --test scripts/ci/tests/test-ralph-pr-recovery.mjs \
  scripts/ci/tests/test-ralph-automation.mjs \
  scripts/ci/tests/test-ralph-round-cache.mjs
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
