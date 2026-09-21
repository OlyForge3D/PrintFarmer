---
name: "ralph-pr-recovery-admission"
description: "Explicit existing-ledger reservation for Windows general PR recovery."
---

## Scope

`node scripts/ci/ralph-admission.mjs reserve-local-pr` consumes JSON on stdin.
It reserves in the **existing Windows-owned PrintFarmer ledger**, never a new
host authority. The Windows profile must pass bootstrap and live deployment
verification first; implementing this API does not activate that profile.
Mobile/unknown scope is not admitted by this command. Native macOS uses the
existing local claim/session protocol, not a new copy of the Windows ledger.

Keep `reserve-local` unchanged for genuinely new issue work. Its `linkedPr:false`
and unblocked issue contract remains mandatory. PR recovery separately requires
a current open same-repository `squad` PR, no PR-level hold, exact current head
and complete files, and verified inactive ownership. A draft or blocked parent
does not forbid corrections. Genuine prerequisites still must be satisfied.

## Request

Pass the existing canonical local `job` fields: `jobId`, `repository`,
`owner`, `baseSha`, `expectedHost`, explicit supported `model`/`effort`,
`agent:"squad"`, `acceptanceCriteria` and optional `charter`.
`expectedHost` must equal this controller host's actual `os.hostname()`.
Supply `issue` when the PR closes an issue; it must match live GitHub linkage.
When the PR has no closing issue, omit it; never invent one or use the PR number.
All discovered closing issues are retained and fenced against concurrent work.

Additional fields:

```json
{
  "expectedGeneration": 42,
  "controllerPid": 12345,
  "recovery": {
    "pr": 123,
    "headSha": "<exact-full-head>",
    "scope": "general",
    "files": ["src/path.cs", "src/previous-name.cs"],
    "findings": ["R1: accepted finding permalink", "CI: exact failing check permalink"]
  },
  "ownership": {
    "host": "<actual-controller-hostname>",
    "pr": 123,
    "headSha": "<same-exact-head>",
    "state": "inactive",
    "observedAt": "<fresh-ISO-time>",
    "source": "<actual-native/worker/branch-observation-references>",
    "liveInventoryChecked": true,
    "archivedHistoryChecked": true,
    "terminalHistoryChecked": true,
    "queueChecked": true,
    "noPendingDelivery": true,
    "externalClaimsChecked": true,
    "remoteOwnership": "clear",
    "prerequisitesSatisfied": true,
    "activeJobs": [],
    "externalClaims": []
  }
}
```

The example omits `job` for brevity; supply it in the same object. Read current
ledger generation before constructing evidence. Resolve the real owning Copilot
controller PID, never the short-lived admission process's PID.
Findings are the exact correction/CI/review-readiness task and its provenance;
the reservation is not a reviewer verdict or permission to merge.

`activeJobs` must account every other active ledger entry by `jobId`, `fence`,
`host`, actual `sessionId` when known, and its complete current/intended `files`.
`externalClaims` covers live/unresolved work outside those ledger identities:
actual `host`, `sessionId`, `source`, `pr` when present, `issues` and complete
`files`. Unknown identity/file scope must remain a blocker, not an empty array.
Include both hosts and relevant remote branches even without a PR. A missing
local App row is not proof a remote job ended. Do not turn unavailable remote
evidence into `remoteOwnership:"clear"`.

## Validation And Trust Boundary

The command independently reads the live PR/head/holds, fully paginated files
(including rename sources), and closing-issue references; incomplete/oversized
link coverage fails closed. It rechecks head after those reads. It checks exact
generation under the existing ledger lock, active PR/issue ownership, file
conflicts and the union of active reservations plus external live sessions.
Complete independent file sets can proceed within the existing five-slot limit.
No accepted, uncertain or stranded job is cleared by this reservation.

Host liveness, history, queue and external-claim observations are **trusted
coordinator attestations**, not a new App RPC or cryptographic proof. They must
be at most 60 seconds old at reservation. Age never proves inactivity. GitHub
cannot be atomically locked with the local ledger: re-fetch the exact head and
ownership again immediately before delivery; a changed head retains the
reservation pending reconciliation rather than authorizing stale work.

CAS conflict, missing evidence or network error means stop that admission and
report/refresh once; no polling, lock deletion, force writes or guessed proof.
Remote status/termination and explicit abandonment still follow their original
worker-bound contracts. No cleanup or automatic reassignment is authorized.

## Delivery And Recovery

Only `reservationCreated:true` means this call acquired a new reservation.
Even then it is **not a dispatched worker**. Deliver exactly once using the
native PR-session tool on that PR's existing branch, include job/fence/PR/head,
and verify actual kickoff processing before `acknowledge-local`.

An exact replay returns the same job/fence with `reservationCreated:false` and
does not advance generation. It is historical ownership, **not permission to
create a second session**. Inspect the existing reservation/session delivery.
Changed work under an existing job ID is fenced. Use the same real session ID in
the PR handoff record and all later terminal evidence.

If delivery fails or its receipt is lost, retain ownership. Find the stable job
marker in native inventory/history; never immediately create a replacement.
Existing `recover-local`, `fail-local-kickoff`, stranded-session fencing and
supported terminal completion remain the only exits. A dead controller plus a
timeout alone does not establish session absence; an accepted session cannot
be reclaimed through the reservation-only route. Failure/abandonment is not
successful delivery. Do not weaken these rules merely to free capacity.
