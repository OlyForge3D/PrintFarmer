---
post_title: "Installation update and recovery runbook"
author1: "Parker"
post_slug: "host-update-runbook"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["deployment", "updates", "recovery"]
ai_note: "AI-assisted, code-checked operational documentation; not rollout authorization."
summary: "Operator preparation, current manual and automatic interfaces, and stop conditions for recovery."
post_date: "2026-09-24"
---

## Support boundary

This is the documentation slice of #2664, not proof of completed manual or
offline recovery. **Do not enable managed installation on the strength of this
guide.** The packaged host-local recovery CLI (only a first slice exists; see
[Failure and recovery](#failure-and-recovery)), complete offline bundle and isolated
restore evidence remain delivery gates. See the
[offline recovery requirements](OFFLINE_UPDATE_RECOVERY.md).

The API has constrained executor adapters and a manual authorization path.
The scheduler is registered when protected host state is enabled, but production
registration still supplies unavailable candidate-readiness and admission-fence
adapters. The admin UI's Auto-update/window controls and Later action are
disabled. Update now is separately gated on the reported manual runtime
contract. Configuring a root, saving policy, or verifying a signature does not
make any of these paths safe or available.

This guide describes current interfaces and the procedure an authorized
operator must satisfy once the deployment has proven the required facilities.
It does not grant deployment/pilot permission or prescribe a socket mount to
make a disabled control work. Never expose the host Docker socket or a generic
host-command/container-control proxy to the API.

Publication is separate: use the single **Actions > Consolidated Release**
entry point in the [release guide](RELEASE_GUIDE.md), which owns current
version/source inputs and diagnostics. No manual tags, ledger edits, copied
CI evidence, extra release workflows or additional environments are required
here. A published release does not authorize an installation.

## Prepare one installation

1. Identify the owner and supported deployment: single-host Compose, actual
   monolith/split service set, PostgreSQL or SQL Server, worker locations and
   platforms. Record every writer, both database contexts, external storage,
   queued job pins and remote workers. Native inventory visibility is not
   native managed-update support. Do not infer ARM worker support from the
   other images: the published OrcaSlicer worker is AMD64-only.
2. Verify the installed release and the candidate from original signed
   evidence. Record canonical release ID, version, source SHA, manifest digest
   and every applicable index/platform image digest. Unsigned legacy
   installations remain manual-only; there is no trust-bootstrap or
   skip-verification switch. Manually installing a current signed release
   followed by inventory refresh is the supported transition, not an assertion
   that an unsigned installation is trusted.
3. Review the release notes, features, fixes, breaking changes, client/worker
   compatibility, migrations, expected downtime and recovery choice. A verified
   available release is not **Ready to install**: fresh complete host evidence
   must independently pass compatibility, staging, readiness and admission.
   Missing notes or unknown compatibility are not permission to guess.
4. Record selected, observed/source and target channel separately. Stable is
   the selection default, not fabricated evidence of the installed channel.
   Insider requires explicit administrator acknowledgement:
   **Insider updates may arrive more frequently and have reduced stability
   compared with stable releases.** Check consent for outbound discovery
   separately; selecting a train does not consent to checks or automatic work.
5. Retain the entire prior immutable set and configuration before downtime.
   Establish storage capacity for target and prior images plus a coordinated
   backup and verify its restore procedure on an isolated host. Include both
   contexts, matching models/G-code/profiles/artifacts, data-protection keys,
   certificates and effective configuration. Set retention and access controls
   with the installation owner; a successful dump alone proves no restore.
6. Keep automatic policy off until host setup, minimum API-independent recovery,
   security acceptance and the named rollout are approved. Do not use an
   installer redeploy or image pull as a substitute for these gates.

### Protected state and configuration

These are **two distinct configuration sections**, not interchangeable paths.
Provision directories for the real service identity using OS access controls;
keep them on persistent host storage outside application-DB restore scope.
Back up sensitive state securely without publishing it in tickets or logs.

| Section | Contents and operator checks |
| --- | --- |
| `HostUpdateExecution:RootDirectory` | Absolute persistent executor root, not temp or the working tree. `state/journal.ndjson`, `state/installed-state.json`, `state/execution.lock`, `state/recovery-outcomes` and `backups` are derived beneath it. Preserve the complete root, not just the named examples. |
| `HostUpdates:HostState` | Default `Enabled=false`. `RootPath` must already exist and pass filesystem-security validation. It holds installation identity, replay store/anchor/journal, policy fence, one-time authorizations and automation policy. Windows additionally requires a genuinely provisioned restricted ACL and `WindowsSecurityAttested`; the flag is not an ACL installer. |
| `HostUpdateExecution:HostExecutablePaths` | Explicit absolute paths to audited Docker and database tools in the executor's namespace; no ambient PATH or arbitrary commands. This configuration does not authorize API access to Docker. |
| Executor topology/storage options | Match `ComposeFiles`, `ComposeProjectName`, `ActiveServiceIds`, `ServiceMappings`, `OwnedDirectories` and health endpoint to the actual installation. Defaults are not universal. Missing owned storage is an error, not an empty successful backup. |

For SQL Server, backup files are written in the **database server's**
filesystem. The derived backup directory must be visible and readable at the
same path to the executor; the availability probe tests that round trip.
Its `master` backup probe needs the permissions described in the
[executor reference](HOST_UPDATE_EXECUTOR.md). Do not disable that probe or
grant broad credentials merely to silence it. An externally owned database
currently fails the built-in backup path closed; coordinate supported owner
backup/restore evidence rather than changing its ownership flag.

Never reset a replay store, anchor, policy fence or lock to clear an error.
First-time provisioning is not continuity recovery. Missing, rolled-back or
unproven-current replay state blocks new offers/execution, even after an
application DB restore. Preserve each trust-root/channel high-water mark and
rejected/superseded identity across policy edits, restarts and channel switches.

## Manual Update now

Use the admin update experience in `/admin/updates` with the authenticated
`system_settings` admin permission. Read the displayed readiness and blocking
reason before acting. A disabled Update now is a stop condition, not an
invitation to call the endpoint directly.

The UI requests one-time authorization before execution. Review the immutable
source/target, selected and observed channels, migration/downtime/backup plan
and recovery classification first. Keep the returned release and operation
references in the protected operator record. Fresh plan/evidence/policy drift
requires a fresh review and authorization; never resubmit client-chosen image
digests, commands or release material.

The existing admin routes are a reference for diagnostics and authorized
integrations, **not a host-local CLI**:

| Method and route | Meaning |
| --- | --- |
| `POST /api/admin/host-updates/authorizations` | Request one-time authorization for the server-resolved current verified candidate. |
| `POST /api/admin/host-updates/execute` | Submit that authorization intent. The server resolves and revalidates immutable evidence; do not blindly retry an uncertain response or send an empty body as a retry of a previous operation. |
| `GET /api/admin/host-updates/{releaseId}/status` | Read `releaseId`, `currentState`, `activities` from durable execution history. A `404` is no journal history, not successful completion. |
| `POST /api/admin/host-updates/{releaseId}/recover` | Reconstruct the failed request from the journal. Optional `requestId` must match; clients cannot supply replacement release material. |

`409` can mean refusal, binding conflict or `RecoveryRequired`; retain its
actual response rather than treating all conflicts as retryable. `503` means
unavailable protected facilities. Authentication/permission failures do not
authorize a different account or transport bypass. HTTP `200` from recovery
alone is not success: read its `outcome` and `detail`.

The executor proceeds through `Accepted`, `Preflight`, `Draining`, `Fenced`,
`BackedUp`, `Migrating`, `Applying`, `Verifying`, then `Completed`, or
`RecoveryRequired`. Receipts such as `migration:before` and `migration:after`
distinguish intent from observed completion. Drain waits for active work and
proven writer quiescence; a timeout defers/fails closed, never cancels a physical
print. Do not clear pending commands, leases or job pins to force progress.

The shared engine serializes both contexts' target-image migrations and applies
only the pinned active service set. Verification and durable outcome/fence
handling must finish before resuming production. A disconnected browser or API
restart proves neither success nor failure; reacquire status by the same
release identity before considering another request.

**Later** is intended only to defer a reminder. It does not cancel an operation,
disable checks or opt out of automation; it is currently disabled in the UI.

## Automatic policy and opt out

Manual execution is one-time authorization. Automatic opt-in is bounded
administrator standing permission for eligible releases in the selected train
and window using the **same engine**. Routine automatic runs do not need fresh
human approval per release. Drift, channel transitions and recovery do not
inherit blanket permission from opt-in.

The UI does not yet enable Auto-update or window editing. The existing policy
API is `GET`/`PUT /api/admin/host-updates/automation-policy`, guarded by the
same admin permission. It is useful for inspecting and disabling already
configured intent; do not use it to bypass unavailable UI/runtime gates.

Read the policy first. A replacement carries the current `expectedRevision`
and all policy fields: `enabled`, `killSwitch`, `channel`,
`insiderAcknowledged`, `pollIntervalSeconds`, `insiderPollIntervalSeconds`,
`maintenanceWindowStartHour`, `maintenanceWindowEndHour`. Preserve unrelated
values when disabling with `enabled=false`. On a revision conflict, re-read and
reconcile rather than overwrite another administrator's decision. Read back the
accepted revision. If the store is unavailable, disablement is **unconfirmed**.

Windows are in **UTC**, start inclusive and end exclusive, expressed in whole
hours; start `0`, end `24` is all day, and start `22`, end `6` spans midnight.
The policy API rejects start hours outside `0..23` and end hours outside
`1..24` with `400 policy_invalid`; read back the accepted policy after saving.
Choose a window allowing drain, backups and recovery, not just download time.
Do not assume a saved local-time string changes scheduler policy.

Distinguish `configuredEnabled` from `effectiveEnabled` in scheduling status.
A saved enabled policy does not bypass missing trust, replay, compatibility,
physical admission or executor facilities. Check the selected/effective
channel, policy revision and current reason after any edit.

Opt out rejects new automatic work. An active execution stops only at safe
checkpoints: never kill a migration to make a toggle immediate.
`POST /api/admin/host-updates/automation-policy/cancel` signals safe-checkpoint
cancellation, not rollback or proof of a stopped process. `202` is acceptance;
`409 no_active_automatic_update` is not an execution-history outcome. Preserve
and inspect the active release's status independently. Current UI limitations
and production unavailable adapters remain in force even though these
endpoints exist.

## Failure and recovery

**The packaged host-local status/recovery CLI is only partially delivered.** The
first slice of #2980 (below) adds the API-independent engine entry point and
wrappers, but packaging, host placement, the OS matrix, drift reapproval and
provider/topology coverage are still open. That gap still blocks a claim of
complete recovery support. Retain protected host evidence for the deployment
owner; do not invent a recovery command, edit journal JSON, delete locks, or run
the installer against a possibly migrated database. Directly reading a file is
not journal integrity verification or authorization to release a fence.

### Host-local status and recovery CLI (first slice, #2980)

`Farm.HostUpdate.Cli` (`src/tools/Farm.HostUpdate.Cli`) runs the same
journal, lock and recovery coordinator the API uses, without the API. It is
**not rollout authorization**: it never starts a forward update, and does not
close #2980 or #2664. Run it only through the fixed-operation wrappers, as the
account that owns the protected root, with the same configuration the API host
uses:

```bash
export PRINTFARMER_HOST_UPDATE_CLI_DIR=/opt/printfarmer/host-update-cli  # contains Farm.HostUpdate.Cli.dll
scripts/printfarmer-host-update.sh --config /etc/printfarmer/host-update.json status --json
scripts/printfarmer-host-update.sh --config /etc/printfarmer/host-update.json status --release stable:1.2.3
scripts/printfarmer-host-update.sh --config /etc/printfarmer/host-update.json recover --release stable:1.2.3 --preview
scripts/printfarmer-host-update.sh --config /etc/printfarmer/host-update.json recover --release stable:1.2.3 --confirm stable:1.2.3
```

```powershell
$env:PRINTFARMER_HOST_UPDATE_CLI_DIR = 'C:\PrintFarmer\host-update-cli'
scripts\printfarmer-host-update.ps1 -Config C:\PrintFarmer\host-update.json recover -Release stable:1.2.3 -Preview
```

The config file uses the API's `HostUpdateExecution`, `DB_PROVIDER` and
connection-string keys; environment variables override it. The wrappers accept
only absolute paths and validated release/request identifiers, and refuse
anything else with exit 2 before the CLI runs. `PRINTFARMER_DOTNET` may name an
absolute `dotnet` host. `--request-id` is optional and must match the binding
recorded in the journal.

- `status` reads the lock-held journal. Without a release it lists releases
  with their last state. With `--release` it shows the activity trail and any
  `*:before` phase with no matching completion (uncertain side effects).
- `recover --preview` resolves the recorded request binding and reports the
  plan the coordinator would take (for example `FenceReleaseOnly` or
  `NeedsOperator`) plus any namespace-proof failures. It writes no outcome,
  journal or fence state.
- `recover --confirm <release>` requires the release retyped exactly. It first
  proves that the configured root, compose files, owned directories, database
  and host tools are visible in this namespace (existence only; nothing is
  executed), then runs the shared coordinator under the execution lock.

| Exit | Meaning | Operator response |
| --- | --- | --- |
| 0 | Success (status read, preview plan, or `RolledBack`) | Complete the coordinated-restoration checks below before resuming anything. |
| 2 | Usage error | Correct the command; nothing ran. |
| 3 | Configuration invalid or namespace unproven | Stop. Run on the host/namespace that owns the state; do not create missing paths. |
| 4 | State unreadable (corrupt journal, access denied, durable store unavailable) | Stop. Preserve the state directory for the deployment owner. |
| 5 | No history for the release | Check the release id; do not recover a release that never executed. |
| 6 | Refused (not in recovery, binding missing or mismatched) | Do not force; re-read status. |
| 7 | Execution lock held | Another executor or recovery is active. Wait; never delete the lock. |
| 10 | Needs operator (including no restorable backup, or canceled) | Follow the coordinated restoration procedure below. |
| 11 | Fence release pending | Writers stay fenced. Re-run `recover --confirm` once the fence adapter is reachable. |

Known limits of this slice:

- `state/execution.lock` is a sentinel created by any lock acquisition, even
  `status` and `--preview`. It carries no state; its presence does not mean an
  update ran, and it must not be deleted by hand.
- Recovery reads the journal under the lock, releases it, then the coordinator
  re-acquires it (the API follows the same pattern). A concurrent executor
  could append in that window, so run recovery only while execution is
  otherwise idle.
- .NET configuration binding **appends** configured `ComposeFiles` entries to
  the built-in default rather than replacing it, so the default relative
  compose path must also exist in the CLI's working directory or the namespace
  proof fails. This matches the API's availability probe.
- There is no drift reapproval, downtime preview, physical command
  reconciliation gate, published package or PostgreSQL/SQL Server and
  split-topology proof yet; those remain #2980 follow-up work under #2658.

| Observation | Operator response |
| --- | --- |
| Staging/signature/platform rejection | Keep the old set. Correct evidence through the trusted distribution path; no alias, download or build fallback. |
| Drain/backup failure | Do not migrate or clear the fence. Prove whether any side effect occurred before resuming the old set. |
| `migration:before` or `apply:before` without completion | Treat the side effect as uncertain. Use operation-specific schema/digest reconciliation; never blindly repeat a migration or apply. |
| `RecoveryRequired` / `NeedsOperator` | Preserve diagnostics, prior artifacts and backup references. Keep writers fenced; choose a supported fix-forward or coordinated restore with the deployment owner. |
| API disconnected, stale UI, status unavailable | Outcome unknown. Do not submit a new authorization as a connectivity probe. Read host-local `status`, then escalate to the deployment owner. |
| Missing replay continuity | Stop eligibility and execution. Use an explicitly trusted continuity-recovery procedure; do not recreate the store from the bundle or restored DB. |

For coordinated restoration, the approved recovery procedure must:

1. Identify the failed operation and exact prior/target manifests, platform
   images, channel policy and checkpoint. Verify the backup's consistency point,
   coverage, checksums and restore compatibility before modifying anything.
2. Quiesce all writers, including remote participants. Preserve diagnostics and
   the current protected update/replay state separately from restorable app
   data. Restore both databases and matching blobs/configuration/keyrings as
   one consistency set, with database-owner participation for external servers.
3. Use only the verified compatible prior set or an explicitly supported
   fix-forward target. After schema/storage change, do not assume image-only
   rollback works; no EF down-migrations. An unsupported insider-to-stable
   downgrade stays blocked: wait for a compatible release, fix forward, or use
   a verified full restore.
4. Verify actual running digests, both schema heads, health, routes, auth/key
   continuity, artifact access and worker/job-pin compatibility. A successful
   recovery response must include `RolledBack`; durable journal completion
   uses `Completed` with phase `recovery:rolled_back`. A fence-release-pending
   or `NeedsOperator` result is not permission to resume.
5. Reconcile every affected printer's physical state with queue/start/control
   outcomes before allowing new dispatch. A DB restore cannot undo a physical
   print. Never replay uncertain starts, moves or control commands, clear
   leases blindly, or use real printer commands as recovery smoke tests.
6. Reconcile selected versus observed channel and refresh trusted metadata and
   installed observations on reconnect before new eligibility. Retain both
   channels' replay records and failure history; do not let recovery artifacts
   become fresh update offers.

This checklist is a safety boundary, **not a tested provider-specific restore
script**. Remaining delivery is tracked by #2980 (host-local CLI), #2981
(complete bundles) and #2982 (isolated recovery and authorized rollout evidence).
#2664 remains open. Keep managed update execution disabled until those gates
and the deployment owner's rollout requirements are satisfied.
