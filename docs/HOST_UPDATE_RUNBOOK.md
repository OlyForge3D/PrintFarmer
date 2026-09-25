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
wrappers, and #3041 publishes it as a signed, self-contained package for a
declared host matrix. Automated installer placement and configuration
generation (#3045) and
provider/topology coverage are still open. That gap still blocks a claim of
complete recovery support. Retain protected host evidence for the deployment
owner; do not invent a recovery command, edit journal JSON, delete locks, or run
the installer against a possibly migrated database. Directly reading a file is
not journal integrity verification or authorization to release a fence.

### Install the signed CLI package

Each server release (#3041) attaches one self-contained archive per supported
host runtime, a checksum list that names exactly those archives, and a keyless
Cosign bundle for the checksum list, signed by the same release workflow
identity as `update-manifest.json`:

| Asset | Contents |
| --- | --- |
| `printfarmer-host-update-cli-v<version>-linux-x64.tar.gz` | Linux x64 CLI plus wrappers |
| `printfarmer-host-update-cli-v<version>-linux-arm64.tar.gz` | Linux ARM64 CLI plus wrappers |
| `printfarmer-host-update-cli-v<version>-win-x64.tar.gz` | Windows x64 CLI plus wrappers |
| `printfarmer-host-update-cli-v<version>-SHA256SUMS` | `sha256sum` list of the three archives |
| `printfarmer-host-update-cli-v<version>-SHA256SUMS.sigstore.json` | Cosign bundle for that list |

Supported hosts:

| Runtime | Host requirements |
| --- | --- |
| `linux-x64`, `linux-arm64` | A glibc distribution supported by .NET 10 with `libicu` and OpenSSL installed, and `bash`. musl/Alpine is not supported. |
| `win-x64` | A Windows version supported by .NET 10, with PowerShell 7 (`pwsh`). |

macOS is not packaged: an apphost cross-published from Linux is not code-signed,
and Apple silicon refuses to run it. On macOS, and for source builds, point
`PRINTFARMER_HOST_UPDATE_CLI_DIR` at a `dotnet publish` output of
`src/tools/Farm.HostUpdate.Cli` built from the installed release's tag.

The package needs no source checkout, API or installed .NET runtime. Each
archive contains `cli/` (the self-contained CLI), the wrappers,
`common-utils.sh`, `LICENSE`, `THIRD-PARTY-NOTICES.md` and
`host-update-cli-package.json` (version, tag, channel, source commit, runtime
and `rolloutAuthorization: false`). Members are owned by `0/0`; directories and
launchers are `0755`, everything else `0644`. A packaged wrapper finds `cli/`
beside itself, so `PRINTFARMER_HOST_UPDATE_CLI_DIR` is not needed, and it
refuses `PRINTFARMER_DOTNET` because the launcher carries its own runtime.

Install the CLI of the release installed on the host (the release whose API ran
the update), into a directory named for its version. Keep the previous version
directory until any update or recovery it may need is finished.

**Linux** (stable identity shown; insider releases use
`@refs/heads/development`):

```bash
V=1.2.3; RID=linux-x64; P=printfarmer-host-update-cli-v$V
cosign verify-blob --bundle "$P-SHA256SUMS.sigstore.json" \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main \
  "$P-SHA256SUMS"
grep -E "  $P-$RID\.tar\.gz\$" "$P-SHA256SUMS" | sha256sum --check --strict -
sudo install -d -o root -g root -m 0755 "/opt/printfarmer/host-update-cli/$V"
sudo tar -xzf "$P-$RID.tar.gz" -C "/opt/printfarmer/host-update-cli/$V" --no-same-owner
```

Keep `/opt/printfarmer/host-update-cli` root-owned and not writable by the
service account. Store the configuration at `/etc/printfarmer/host-update.json`,
owned by the account that owns the protected root, mode `0600`; it may contain
connection strings.

**Windows** (elevated PowerShell 7):

```powershell
$V = '1.2.3'; $P = "printfarmer-host-update-cli-v$V"
cosign verify-blob --bundle "$P-SHA256SUMS.sigstore.json" `
  --certificate-oidc-issuer https://token.actions.githubusercontent.com `
  --certificate-identity https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main `
  "$P-SHA256SUMS"
$expected = (Select-String -Path "$P-SHA256SUMS" -Pattern "  $([regex]::Escape("$P-win-x64.tar.gz"))$").Line.Split(' ')[0]
if ((Get-FileHash "$P-win-x64.tar.gz" -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'hash mismatch' }
$dir = "C:\Program Files\PrintFarmer\HostUpdateCli\$V"
New-Item -ItemType Directory -Path $dir -Force | Out-Null
& "$env:SystemRoot\System32\tar.exe" -xzf "$P-win-x64.tar.gz" -C $dir
```

The install directory keeps the inherited `Program Files` ACL. Store the
configuration at `C:\ProgramData\PrintFarmer\host-update.json` with inheritance
removed and access limited to the protected-root account, `Administrators` and
`SYSTEM` (for example `icacls <file> /inheritance:r /grant:r SYSTEM:F
Administrators:F <account>:R`).

**Offline hosts** (#2981): carry the chosen archive, the checksum list and its
bundle unchanged. Verify the bundle on a connected host before transfer (or
with Cosign's offline trusted-root options), and always re-check the archive
SHA-256 against the list on the target host before extracting.

Automated placement by the installer, generated host configuration and a
per-archive SBOM are follow-up work (#3045).

### Host-local status and recovery CLI (first slice, #2980)

`Farm.HostUpdate.Cli` (`src/tools/Farm.HostUpdate.Cli`) runs the same
journal, lock and recovery coordinator the API uses, without the API. It is
**not rollout authorization**: it never starts a forward update, and does not
close #2980 or #2664. Run it only through the fixed-operation wrappers, as the
account that owns the protected root, with the same configuration the API host
uses:

```bash
CLI=/opt/printfarmer/host-update-cli/1.2.3/printfarmer-host-update.sh
"$CLI" --config /etc/printfarmer/host-update.json status --json
"$CLI" --config /etc/printfarmer/host-update.json status --release stable:1.2.3
"$CLI" --config /etc/printfarmer/host-update.json recover --release stable:1.2.3 --preview
"$CLI" --config /etc/printfarmer/host-update.json recover --release stable:1.2.3 --confirm stable:1.2.3
```

```powershell
& 'C:\Program Files\PrintFarmer\HostUpdateCli\1.2.3\printfarmer-host-update.ps1' `
  -Config C:\ProgramData\PrintFarmer\host-update.json recover -Release stable:1.2.3 -Preview
```

The config file uses the API's `HostUpdateExecution`, `DB_PROVIDER` and
connection-string keys; environment variables override it. The wrappers accept
only absolute paths and validated release/request identifiers, and refuse
anything else with exit 2 before the CLI runs. The PowerShell wrapper parses its
own arguments (names case-insensitive, values case-sensitive) rather than using
PowerShell parameter binding, so a usage error never prompts. `help` (or
`--help`) prints usage without a config. A malformed or unreadable config file
is reported by the CLI as exit 3 (`configuration_unreadable`), honouring
`--json`. A missing config file is also exit 3, because the wrappers and CLI
check only that `--config` is absolute and leave existence to the loader (an
existence probe cannot tell an absent file from an access-denied one).
`PRINTFARMER_DOTNET` may name an
absolute `dotnet` host for a framework-dependent `dotnet publish` layout; a
packaged self-contained launcher refuses it. `--request-id` is optional and must match the binding
recorded in the journal.

- `status` reads the lock-held journal. Without a release it lists releases
  with their last state. With `--release` it shows the activity trail and any
  `*:before` phase with no matching completion (uncertain side effects).
- `recover --preview` resolves the recorded request binding and reports the
  plan the coordinator would take (for example `FenceReleaseOnly` or
  `NeedsOperator`) plus any namespace-proof failures. It writes no outcome,
  journal or fence state, and opens the host-state policy root read-only (no
  write probe). It also reports (#2998):
  - `identity`: the prior installed release (digests, platforms, recorded time)
    and the recorded target authorization (release, manifest digest, source
    commit, channel, host platform, trust root, policy revision/fingerprint,
    per-service child digests), plus the current host platform.
  - `downtime`: `impact` (`service_restart`, `restore_and_service_restart`,
    `none` or `operator_required`), affected services, restored backup targets
    and `timeoutBudgetSeconds`: one `ApplyTimeoutSeconds` per image pull plus
    one for `compose up`, `VerifyTimeoutSeconds` plus one
    `VerifyPollIntervalSeconds`, and one `BackupTimeoutSeconds` per restored
    non-directory target (`basis: sum_of_configured_timeouts_not_an_upper_bound`).
    It is an estimate, not an upper bound or a measurement: `unboundedSteps`
    lists the steps with no configured timeout (`backup_checksum_verification`,
    `owned_directory_copy`, `health_check_final_pass`). It is omitted when no
    automatic path exists.
  - `backupEvidence`: the latest backup manifest for the release, its targets,
    file count and bytes, and how many recorded files are present at their
    recorded length. Presence is not a checksum verification.
  - `recoveryEvidence`: any recorded recovery outcome, uncertain phases, and
    whether migration or apply started.
  - `writerFence`: whether admission is closed, the fenceable writers, and what
    recovery does to the fence.
  - `drift`: the drift items since authorization, a configuration fingerprint,
    and, when drift exists, the `reapprovalToken`.
- `recover --confirm <release>` requires the release retyped exactly. It first
  proves that the configured root, compose files, owned directories, database
  and host tools are visible in this namespace (existence only; nothing is
  executed). It then refuses with exit 12 if the host drifted from the recorded
  authorization and `--reapprove-drift <token>` does not carry the token the
  current `--preview` printed. Only then does it run the shared coordinator under
  the execution lock.

Drift reapproval (#2998) compares the journaled authorization with the host
now. Since #3047 the executor also journals an authorization baseline on the
`accepted` activity, captured under the execution lock before any step runs:
a content hash of the prior installed state, the configuration fingerprint
(below) and the fingerprint of the pinned release trust root (Sigstore issuer
and per-channel release-workflow identities). If the baseline cannot be
captured, the update refuses to start (`authorization_baseline_unavailable`).
The baseline is captured only at first acceptance: a resumed release, including
one authorized before #3047, is never re-baselined onto the host state at
restart, and only the journal's hashed payload is trusted when it is read back.
Since #3050 the baseline (schema 2) also records the release's database
manifest binding: the signed manifest digest the API bound to the release ID in
`AppSettingsEntities` (`none` when none was bound). The executor and the CLI read
it with one parameterised `SELECT` over a provider-enforced read-only connection
(SQLite `Mode=ReadOnly`, a PostgreSQL `READ ONLY` transaction, SQL Server
`ApplicationIntent=ReadOnly` with the transaction rolled back); no EF model,
migration or schema check runs, and credentials are never journaled, printed or
fingerprinted. SQL Server's read-only intent is advisory on a primary replica, so
its guarantee is that the reader issues only that `SELECT` and rolls back.

| Drift code | Meaning |
| --- | --- |
| `host_platform_drift` | The current OS/architecture differs from the authorized host platform (or is unsupported). |
| `policy_unverifiable` | The standing policy could not be read (host state disabled, root insecure, missing or corrupt). |
| `policy_revision_drift` / `policy_fingerprint_drift` | The standing automatic-update policy changed since authorization. |
| `channel_drift` | The policy channel no longer matches the authorized channel. |
| `authorization_baseline_unrecorded` | The journal carries no baseline this CLI understands (a journal written before #3047, or an unknown baseline schema). Configuration, trust-root and prior-state content cannot be proven unchanged, so review `identity.prior` and the configuration with the deployment owner before reapproving. |
| `configuration_drift` | The configuration fingerprint differs from the one recorded at authorization (for example a changed compose file, service mapping, owned directory, tool path or database provider). The CLI and the API host must read the same configuration for this to be meaningful. |
| `trust_root_drift` | The authorization named a trust root this build does not pin, or the pinned trust-root fingerprint changed since authorization (for example the CLI came from a different release). |
| `prior_state_changed_since_authorization` | The installed state's content differs from the baseline recorded at authorization, including a deleted or backdated record (`observed` is `none` when it was deleted). For a journal without a baseline this falls back to the older timestamp heuristic and is always accompanied by `authorization_baseline_unrecorded`. |
| `manifest_binding_drift` | The database manifest binding for the release differs from the one recorded at authorization, was deleted (`observed` is `none`), or could not be read or is not a canonical `sha256:<64 lowercase hex>` digest (`observed` is `unreadable:<ExceptionType>`; the message is withheld because provider errors can carry connection details). An unreadable binding is always drift, never `no drift`. A schema-1 baseline, written before #3050, reports `recorded` as `unrecorded`. |
| `prior_state_matches_target` | The installed state already reports the target release or manifest. |

The CLI reads the policy from `HostUpdates:HostState` (`Enabled`, `RootPath`,
`WindowsSecurityAttested`, or `HostUpdates__HostState__*` environment
variables), the same keys the API host uses. The token binds the recorded
request, the drift items, the configuration fingerprint (root, compose files and
their content hashes, service mappings, owned directories, host tool paths,
provider, SQLite path, database server identity and `DatabaseExternallyOwned`,
never connection-string secrets), the installed state and the observed manifest
binding. Any further change invalidates it (`drift_reapproval_mismatch`); a token
supplied when nothing drifted is refused (`drift_reapproval_unexpected`). The
refusal lists the drift codes but never prints the token, so reapproval
requires reading `--preview`. A release with a recorded `RolledBack` outcome has
nothing to reapprove: `--confirm` stays a durable no-op, but `--preview` still
reports `manifest_binding_drift` so an unreadable or changed binding is never hidden.

| Exit | Meaning | Operator response |
| --- | --- | --- |
| 0 | Success (status read, preview plan, or `RolledBack`) | Complete the coordinated-restoration checks below before resuming anything. |
| 2 | Usage error | Correct the command; nothing ran. |
| 3 | Configuration invalid or namespace unproven | Stop. Run on the host/namespace that owns the state; do not create missing paths. |
| 4 | State unreadable (corrupt journal or installed state, access denied, durable store unavailable) | Stop. Preserve the state directory for the deployment owner. |
| 5 | No history for the release | Check the release id; do not recover a release that never executed. |
| 6 | Refused (not in recovery, binding missing or mismatched) | Do not force; re-read status. |
| 7 | Execution lock held | Another executor or recovery is active. Wait; never delete the lock. |
| 10 | Needs operator (including no restorable backup, or canceled) | Follow the coordinated restoration procedure below. |
| 11 | Fence release pending | Writers stay fenced. Re-run `recover --confirm` once the fence adapter is reachable. |
| 12 | Drift not reapproved, or the installed state changed after evaluation (`drift_reapproval_stale`) | Run `--preview`, review every drift item with the deployment owner, then re-run `--confirm` with `--reapprove-drift <token>` only if recovery toward the recorded prior state is still correct. |

Known limits of this slice:

- `status`, `--preview` and the CLI's own reads before `--confirm` take the
  execution lock without rewriting an existing `state/execution.lock`. Only a
  host that has never taken the lock gets the sentinel created. It carries no
  state, and it must not be deleted by hand. The coordinator run by `--confirm`
  rewrites it as any executor does.
- Recovery reads the journal under the lock, releases it, then the coordinator
  re-acquires it (the API follows the same pattern). A concurrent executor
  could append in that window, so run recovery only while execution is
  otherwise idle. The installed state is bound across that window: if the
  coordinator's decision read (under its lock, before any restore or apply)
  does not match the state the CLI evaluated, recovery fails closed with exit
  12 `drift_reapproval_stale` and a durable `NeedsOperator` outcome; re-run
  `--preview`. Policy and platform are not re-checked in that window because
  the coordinator does not consume them.
- A non-empty configured `ComposeFiles` list replaces the built-in
  `docker-compose.daily-registry.yml` default for both the API and the CLI
  (#2997); list every compose file the installation applies, in `-f` order.
  The built-in default applies only when no `ComposeFiles` entry is configured;
  a blank entry fails startup validation.
- `ActiveServiceIds` follows the same rule (#3042): a configured list replaces
  the built-in split-topology service set for both the API and the CLI, so a
  monolith host lists only its actual services. A blank entry, or an
  explicitly empty list (`[]` or an empty environment variable) for either
  `ActiveServiceIds` or `ComposeFiles`, fails startup validation instead of
  restoring the default. The safety lists `SupportedProviderNames`,
  `RequiredAggregateHealthResultNames` and `RequiredFencedWriterNames` stay
  additive: configured entries extend the built-in set and can never drop a
  code-owned provider check, health result or fenced writer.
- `ServiceMappings` also replaces its default when configured (#3051). List
  every service this host maps, each with `ServiceId`, `ComposeServiceName`,
  `ImageEnvironmentVariable` and `ImageRepository`; a partial override of one
  default entry is not merged. An empty list, an entry missing a field, a
  duplicate `ServiceId`, or an `ActiveServiceIds` entry with no mapping fails
  startup validation.
- Wrapper parity is regression-tested in CI (`deployment-tests.yml`,
  `host-update-wrapper-tests`): `tests/test-host-update-cli-wrapper.sh` and
  `tests/test-host-update-cli-wrapper.ps1` both run on Ubuntu, macOS and
  Windows runners against a stub CLI. This proves argument validation and
  exit-code pass-through only. The real package is smoke-tested separately
  (`host-update-cli-package-tests`: `tests/test-host-update-cli-package.sh` on
  Linux x64 and ARM64, `tests/test-host-update-cli-package.ps1` on Windows x64),
  which builds the archive with the release packaging code, verifies it
  against its checksum list, extracts it and runs the CLI with `dotnet` poisoned
  on `PATH`.
- There is no physical command reconciliation gate yet; that remains follow-up
  work under #2658. Provider and topology coverage uses fake process and health
  adapters only (#3000, see
  [Provider and topology stop conditions](#provider-and-topology-stop-conditions));
  it is not a live PostgreSQL, SQL Server or Docker proof. The
  installer does not yet place the package or generate its configuration
  (#3045), and macOS has no package (see
  [Install the signed CLI package](#install-the-signed-cli-package)).

#### Provider and topology stop conditions

`status`, `--preview` and `--confirm` are regression-tested for SQLite, local
and external PostgreSQL, and local and external SQL Server, each on the split
(`api`, `frontend`, `slicer-host`, `printer-discovery`, `orcaslicer-worker`)
and monolith (`monolith`) topologies. Both `AppDbContext` and
`SlicerDbContext` use `ConnectionStrings:Default`, so recovery restores one
`database` target; a split database is refused by preflight
(`split_database_not_supported`). The CLI stops, before any restore, image pull
or compose command, when:

| Condition | Result | Operator response |
| --- | --- | --- |
| Database provider, server (host/port or data source), database name, or `DatabaseExternallyOwned` changed after authorization | Exit 12 `drift_reapproval_required` (`configuration_drift`) | Confirm with the deployment owner that the configured database is the one the backup came from. Never reapprove a retarget to a different server. |
| `DatabaseExternallyOwned` is `true` and the manifest includes `database` | Exit 10 `NeedsOperator` (`restore_target_unmapped`); no restore tool is required or run | The database owner restores it with their own procedure; this host never restores an externally owned database. |
| The recorded prior state's services differ from `ActiveServiceIds` (topology changed since the update) | Exit 10 `NeedsOperator` (`prior_state_topology_mismatch`), even after drift reapproval | Do not force a restore onto a different topology. Restore the matching compose configuration or recover manually. |
| Aggregate `/health` unreachable or unhealthy after restore | Exit 10 `NeedsOperator`; admission stays closed | Diagnose the API; do not reopen writers by hand. |
| Fence release fails after a successful restore | Exit 11; a repeat `--confirm` only redrives the release | Re-run `--confirm` once the fence adapter is reachable. It never repeats the restore. |

Connection-string credentials are never part of the fingerprint, a process
argument or CLI output; PostgreSQL and SQL Server restores receive the password
through the environment only (`PGPASSWORD`, `SQLCMDPASSWORD`).

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
