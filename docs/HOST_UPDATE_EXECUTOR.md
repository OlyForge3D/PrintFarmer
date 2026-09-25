# Host update executor

The safe-executor core (`HostUpdateExecutor`) supplies
immutable release identity contracts, durable canonical request fingerprint binding, active-topology target validation, a bounded process/installation
lock, a durable hash-chained journal, and a checkpoint-aware state machine. On top of that
foundation, issue #2663 adds concrete, repository-appropriate step adapters for the intended
**manual-first** update path. Production execution remains blocked by the availability facilities
described below: an operator (or, later, #2666's scheduler once it is granted standing permission
— not yet the case here) submits a staged release id through the admin API; execution resolves the immutable request only from server-side verified staging journal evidence, then drives it through
preflight → drain → fence → backup → migration → apply → verify, or into `RecoveryRequired` on any
failure. There is still no automatic/unattended execution path.

## Configuration: `HostUpdateExecutionOptions`

For operator setup, current admin routes, policy opt-out and incident stop
conditions, use the [installation update runbook](HOST_UPDATE_RUNBOOK.md).
The sections below include historical implementation snapshots, not rollout
authorization. Current DI includes target-image migration and a hosted scheduler
when protected host state is enabled, but still registers unavailable candidate
readiness and admission-fence adapters. A complete host-local/offline recovery
path remains a delivery gate; see [offline recovery](OFFLINE_UPDATE_RECOVERY.md).

Bound from the `HostUpdateExecution` configuration section (see
`src/infra/Services/HostUpdates/HostUpdateExecutionOptions.cs`). Root and general executor
settings are validated by `HostUpdateExecutionOptionsValidator`; executable mappings are
validated fail-closed by `ConfiguredHostUpdateExecutableResolver`. The executor remains default-off when the root is unset: validation permits process startup, but all derived executor paths now throw `root_directory_not_configured` instead of resolving under the current working directory, and the runtime availability provider reports `Unavailable` until a writable host-controlled root is configured:

- **`HostExecutablePaths`** (required for process execution): explicit logical-tool to absolute executable
  path mappings. `docker` is required for every deployment; availability derives any additional
  database tooling from the live migration targets in `HostUpdateExecutionAvailabilityProvider`.
  `ConstrainedHostUpdateProcessRunner.AllowedExecutables` separately defines which tool names may
  execute. Bare names are rejected and the ambient `PATH` is never consulted. The executable filename
  and any `.exe` extension must
  be lowercase; for example, `C:\Program Files\Docker\docker.exe` is accepted but
  `C:\Program Files\Docker\DOCKER.EXE` is rejected. Missing, relative, or whitespace-only mappings
  keep process execution unavailable and are reported in availability status rather than crashing startup.
  Configure host-controlled paths such as
  `HostUpdateExecution__HostExecutablePaths__docker=/usr/bin/docker` and the database-native
  tooling required by that deployment (or equivalent JSON/YAML dictionary nesting). On Windows,
  use absolute paths to the installed `.exe` files.
  In containers, every configured path must exist in the container namespace; bind-mount host tools
  read-only into fixed, root-owned locations and configure those mounted paths, rather than relying
  on a host `PATH` that is not visible inside the container.
- **`RootDirectory`** (required for execution, no usable default): an absolute, host-controlled, persistent directory
  that owns all executor state — the durable journal, the execution lock, installed-state, and
  coordinated backups. The validator rejects a relative path, a path under the OS temp directory,
  and the process's current/working directory (or any subdirectory of it), so a container rebuild,
  temp-cleanup, or an application-DB restore can never silently destroy update history or
  in-flight recovery evidence. Configure it via `HostUpdateExecution__RootDirectory` (or the
  equivalent JSON/YAML nesting) to a real, persistent host path (e.g. a dedicated bind-mounted
  volume) before the executor can be used at all.
- Derived paths: `StateDirectory` (`{RootDirectory}/state`), `BackupRootDirectory`
  (`{RootDirectory}/backups`), `DiskWatchPath` (`RootDirectory`, checked for free space at
  preflight).
- Timeouts/poll intervals for drain, fence-proof, backup, verify, apply, and short diagnostic
  process calls; `MinimumFreeBytes`; the EF Core provider allowlist (`SupportedProviderNames`);
  `DatabaseExternallyOwned` (fails backup closed instead of silently skipping a customer-managed
  database); `OwnedDirectories` (name → absolute path for the compose-managed API container's real
  mount points: `/data`, `/app/models`, `/app/gcode`, `/app/profiles`,
  `/app/data-protection-keys`); `ComposeFiles`/`ComposeProjectName`/`ServiceMappings` (the six
  canonical service ids — `api`, `frontend`, `slicer-host`, `printer-discovery`,
  `orcaslicer-worker`, `monolith` — each mapped to its compose service name, pinned-image
  environment variable, and `ghcr.io/olyforge3d/printfarmer-*` repository). A configured
  `ComposeFiles`, `ActiveServiceIds` or `ServiceMappings` list replaces its built-in default
  rather than extending it. An explicitly empty list fails validation. `SupportedProviderNames`
  and the required health/writer lists stay additive (see the
  [runbook's CLI limits](HOST_UPDATE_RUNBOOK.md#host-local-status-and-recovery-cli)).
  `HealthCheckBaseUrl` is used for readiness probes.

## Concrete adapters (`src/infra/Services/HostUpdates/`)

Signed release mapping keeps inventory requirements separate from execution
targets. On `linux-amd64`, the mapper retains six canonical execution IDs,
including `monolith`, with their authenticated platform child digests; inventory
still contains five observed services and uses its `discovery` / `slicer-worker`
aliases. Both child and index digests must use canonical lowercase SHA-256
grammar; the mapper rejects rather than normalizes other forms.

The candidate adapter validates explicit execution targets without replacing an
incomplete set from inventory. The inventory fallback is used only when no
explicit targets exist. Missing, duplicate, or unknown targets still report
`verified_release_target_set_invalid`. Current `linux-arm64` signed manifests
lack a worker child, so their five execution targets remain fail-closed; no
worker digest is synthesized. Passing this target check does not bypass the
separate readiness, admission, or authorization requirements.

| Step | Adapter | Notes |
|---|---|---|
| Preflight | `HostUpdatePreflightCheck` | Revalidates installed version/digests, provider allowlist, disk free space, and every configured migration target's provider name before anything else runs. |
| Drain | `HostUpdateDrainCoordinator` | Closes the admission gate to new submissions/scheduling, then bounded-polls every registered `IActiveWorkObservationPort`: `AppDbContext` active prints/pending outbox work plus `SlicerDbContext` active processing leases. In-flight slicer lease progress/completion/failure remains allowed so already claimed work can finish naturally; new enqueue/claim is blocked. Times out closed and never blind-cancels physical work. |
| Fence | `HostUpdateFenceCoordinator` | Proves each writer required by `HostUpdateExecutionOptions.RequiredFencedWriterNames` (`api-admission`, `queue-outbox-publisher`, `power-reading-prune`, `queue-retention-prune`, `backend-start-command-consumer`, `backend-control-command-consumer`, `bed-clear-acknowledgement-expiry`, `auto-dispatch`, `webhook-delivery`, and `queue-reconciliation`) has actually quiesced before backup — bounded-polled, not assumed. `queue-outbox-publisher`, `power-reading-prune`, `queue-retention-prune`, the two backend-command consumers, `bed-clear-acknowledgement-expiry`, and `queue-reconciliation` re-check for a requested pause every 250 ms while idle between passes. For those loop-shaped writers, acknowledgement follows completion of the preceding iteration and is bounded by `max(in-flight iteration, <=250 ms)`: an in-flight publish batch, including sequential SignalR dispatch, must finish first and is not bounded by 250 ms. `auto-dispatch` is channel-triggered with a 30 s durable fallback scan and acknowledges only after `TrackedWorkerCount` reaches zero; `webhook-delivery` checks between deliveries, with a 500 ms idle delay and a 1 s paused delay. The API admission writer is gate-shaped: once Drain closes the gate, its quiescence check directly returns `IsClosedAsync`; Drain, rather than that acknowledgement, establishes active-work quiescence. The executor's availability provider also fails closed (`insufficient_fenced_writers:<names>`) if `HostUpdateExecutionOptions.RequiredFencedWriterNames` names a writer with no registered `IFenceableWriter`. |
| Backup | `HostUpdateBackupCoordinator` + `HostUpdateDatabaseBackupTargetFactory` + `DirectoryCopyBackupTarget` | Coordinated, checksummed backup of the database (via the host's own `sqlite3`/`pg_dump`/`sqlcmd` tooling — never a duplicate ad-hoc dump, and never invoked through a shell string) plus every owned directory. An externally-owned database (`DatabaseExternallyOwned = true`) always fails closed rather than silently skipping. |
| Migration | `HostUpdateMigrationCoordinator` + `DbContextMigrationTarget<AppDbContext>`/`<SlicerDbContext>` | Intentionally unavailable for production execution until a target-image/dedicated migration runner exists. `DbContextMigrationTarget<T>` explicitly throws through the `HostUpdateMigrationStep.cs` implementation; the current/old API assembly's `ProviderAwareMigrationRunner` is not an approved forward-update path. |
| Apply | `HostUpdateImageApplier` | Stages each immutable `repository@sha256` image first with `docker image pull` (`--platform` on forward execution), then applies via the existing compose templates and `docker compose up -d --no-build --pull never`, using an explicit process argument list — never shell interpolation, never a mutable tag. Production DI wraps this runner in `ConstrainedHostUpdateProcessRunner`, which permits only the audited tool names (with explicit `.exe` support) and accepts rooted paths only from trusted host tool directories; unrelated names and untrusted rooted paths are rejected before launch. If staging any image fails, compose mutation is not attempted. |
| Verify | `HostUpdateHealthVerifier` + `AggregateHostUpdateHealthCheck` + `DigestHostUpdateHealthCheck` | Confirms exact running digests via a two-step `docker container inspect --format {{.Image}}` → `docker image inspect --format {{index .RepoDigests 0}}` probe (`.RepoDigests` exists only on image-inspect output, never on container-inspect output -- see "Known limitations" for the bug this replaced) plus the aggregated `/health` endpoint's JSON body has a top-level `Status`/`status` of exactly `"Healthy"` and every configured required result entry (default: `comprehensive`, `signalr`, `spoolman`) is present and healthy. Verification also fails before probing if the digest map is not the exact configured service set, so partial target mappings cannot reopen writers. |
| Recovery | `HostUpdateRecoveryCoordinator` + `DefaultHostUpdateRecoveryCompatibilityEvaluator` + `ProcessHostUpdateRestoreExecutor` + `FileHostUpdateRecoveryOutcomeStore` | On any failure, decides image-only rollback vs. coordinated restore, restores both databases and owned storage/config together via the same provider-native restore tooling (structured process args/env only — no shell string, no password on argv), and durably persists the `RolledBack`/`NeedsOperator` outcome with the same write-through/atomic-replace primitive used by the journal. Restored payload files are flushed before the destination tree is synced; unmapped manifest targets fail closed. A duplicate/restarted `RolledBack` recovery does not replay restore/apply, but it does re-drive idempotent fence release if the previous process crashed before reopening. |

The `auto-dispatch` loop checks the same fence on trigger arrivals and on its
30 s durable scan tick, including when the trigger channel is empty or automatic
dispatch is disabled. A scan tick acknowledges only after all tracked workers have
finished; it neither cancels in-flight dispatches nor starts new ones while paused.
Periodic reconciliation is skipped while paused and resumes after fence release,
so queued jobs can be rediscovered without an external trigger. Trigger ownership
is retired when a consumed event is suppressed by the fence or abandoned after
a scan wins the wait; otherwise its in-flight marker would prevent rediscovery.
Allow up to the next scan tick after worker drain for an otherwise idle loop to
acknowledge.

## Availability contract

`IHostUpdateExecutionAvailabilityProvider` (`HostUpdateExecutionAvailability.cs`) positively probes
— never assumes — that the executor is actually usable: the root directory is writable, the
journal is not corrupt, no configured `RequiredUnavailableFacilities` entries remain, at least one migration and one backup target are configured, every
configured compose file exists on disk, and the container runtime (`docker version`) is reachable.
`HostUpdateExecutionAvailabilityHostedService` computes this immediately at process startup
("restart reconciliation" — a fresh process re-proves its own readiness rather than trusting a
previous run's state) and then periodically rechecks, publishing every result into the singleton
`HostUpdateExecutionAvailabilityHolder` that both the admin API and a future #2666 scheduler poll
without re-running the probe on every read. `Available` carries no reasons; `Unavailable` always
carries the exact missing mechanism(s) (e.g. `root_directory_unwritable:...`,
`compose_file_missing:...`, `docker_runtime_unavailable`, `insufficient_fenced_writers:<name>[,<name>...]`, `facility_unavailable:sql_server_visible_backup_path_mapping_unverified:<evidence>`, `database_provider_tooling_unsupported:<context>:<provider>`, or `host_executable_not_configured:<tool>`) so an operator is never left guessing. There is no code-owned blanket unavailability list; availability closes on this concrete runtime evidence instead. `RequiredUnavailableFacilities` remains an explicit operator override for a deployment-specific prerequisite that must keep execution closed. Separately, installations without verified signed release evidence remain manual-only and fail closed in the `NotManaged` inventory state; that state carries the `ManagedEligibilityNotEstablished` reason only when no blocking compatibility or channel evidence is also present — if such evidence exists, the inventory state is `Blocked` instead and `ManagedEligibilityNotEstablished` is not among its reasons. A current signed release clears the manual-only reason but does not produce an `Eligible` evaluator state. No trust bootstrap is planned or supported: the supported transition is one manual installation of a current signed release followed by inventory refresh, after which normal signed discovery can establish managed eligibility.

The process boundary is production-ready independently of those facilities:
`ConfiguredHostUpdateExecutableResolver` requires an explicit absolute path for each audited native
tool, and `ConstrainedHostUpdateProcessRunner` rejects bare names, rejects ambient `PATH` lookup,
allows only the audited tools, and accepts rooted paths only when explicitly configured or in fixed
system directories. It still delegates through the existing no-shell `ArgumentList` runner. It does
not bootstrap an updater, make unsigned legacy releases eligible, or change the manual authorization
boundary. No protected bootstrap or operator assertion is supported; signed verification remains required before
managed eligibility can be established.

## Scheduler adapter lifecycle

`HostUpdateSchedulerExecutorAdapter` guards each linked cancellation source and
active operation before entering its lifecycle lock. If setup or a pre-armed
cancellation callback throws, cleanup removes only that exact request generation,
disposes the source, and completes the operation's shutdown wait before the
exception propagates. The same request ID can then be scheduled again; a refused
duplicate cannot remove or disable cancellation of an already-running generation.
This in-memory lifecycle cleanup does not release the executor's durable writer
fence or change admission and rollout requirements.

## DI wiring

`HostUpdateExecutionStartup.AddHostUpdateExecution` (`src/api/Startup/HostUpdateExecutionStartup.cs`,
called from `FeatureServicesStartup`) composes every adapter above behind the unmodified
`IHostUpdateExecutionSteps`/`IHostUpdateExecutor` contracts, plus the options/validator, the
availability provider/holder/hosted service, and the durable journal/lock/installed-state stores —
all rooted under the validated `RootDirectory`. It registers only the manual admin API's
dependencies; it never grants #2666's scheduler (or anything else) standing automatic-execution
permission.

## Manual admin API

`HostUpdateController` (`Farm.Modules.Administration`) exposes the manual, operator-invoked
surface: `[RequirePermission("system_settings", "admin")]`-gated execute/recover endpoints that
bind one immutable request per call. Both endpoints now gate on the same `HostUpdateExecutionAvailabilityHolder` the availability hosted service maintains: a new execute request that arrives while the executor reports `Unavailable` is rejected with `503 Service Unavailable` and the exact reasons before it reaches `IHostUpdateExecutor`; matching resume/recovery stays available for the authenticated release identity when restart reconciliation has fenced a nonterminal prior release — see `HostUpdateControllerAvailabilityTests`. Replay/duplicate-submission protection relies on the executor's file-based journal/lock plus the durable request fingerprint. If migration or apply has a `:before` journal receipt without the matching `:after`, the executor first requires operation-specific proof before continuing: migration must show every registered context has no pending migrations, and apply must show the exact requested running digests. If either proof is unavailable or negative, it records `RecoveryRequired` with `uncertain_side_effect:<phase>:<reason>` so an operator must recover while fences remain closed.

## Known limitations

- The verify adapter persists each verified target's platform alongside its digest
  before releasing the writer fence. The real installed-state store requires an
  exact service-to-platform map for recovery; omitting it previously caused
  `installed_state_corrupt:service_platforms_missing` after health verification.
  The retained map also supplies rollback's `docker image pull --platform`
  arguments through `ApplyByDigestsAsync`.
  `HostUpdateExecutionStepsAdapterTests` covers the producer/file-store contract
  and preserves fail-closed ordering on verification or persistence failure.
  This repair does **not** establish managed-update eligibility: production
  readiness/admission adapters, independently verified installed observations,
  and first-update retained prior-state provisioning remain integration gaps.
- Signed-identity and candidate admission, plus execution-request validation,
  reject noncanonical manifest/image digests rather than lowercasing them:
  publication emits lowercase hex, so normalization would hide evidence that
  did not come from that canonical publication path. Execution also requires
  the installed-state store's canonical release-id grammar. Rejection occurs
  before the execution lock, journal, policy read, or any update step, not
  after apply during installed-state persistence. Existing diagnostics remain
  `release_identity_invalid` (foundation identity),
  `verified_release_identity_invalid` (candidate identity),
  `verified_release_target_platform_invalid` (candidate target; arm64 uses
  `verified_release_target_platform_unavailable`), `release_binding_invalid`
  (execution identity), and `target_invalid` (execution target). No new reason
  codes or eligibility states are introduced.
- **Fixed (this pass) — `DigestHostUpdateHealthCheck` was probing a nonexistent field**: it
  previously ran a single `docker inspect --format {{index .RepoDigests 0}} <container>`, but
  `.RepoDigests` is exclusively a property of `docker image inspect` output; it never exists on
  `docker container inspect` output, so this call could never succeed against a real container.
  Fixed to the correct two-step probe (container inspect resolves `.Image`, then image inspect on
  that reference resolves the real repo digest) with new regression tests in
  `HostUpdateVerifyStepTests` (this type previously had zero test coverage at all).
- Bishop/Hicks review of the base commit surfaced (and this slice fixed, with tests) five
  critical defects: `FileHostUpdateExecutionJournal.Append` silently truncated the entire
  hash-chained journal to its newest record on every append (a temp-file-then-move pattern that
  wrote only the new line before moving it over the real path); `AddHostUpdateExecution`
  registered `IEnumerable<IHostUpdateBackupTarget>` explicitly with a factory that itself called
  `sp.GetServices<IHostUpdateBackupTarget>()`, which the .NET container resolves as
  `IEnumerable<IHostUpdateBackupTarget>` again -- an unbounded self-recursive registration that
  would `StackOverflowException` the process the first time anything resolved the backup target
  list; the executor initially treated an unconfigured `RootDirectory` as a closed admission gate rather than an inert/manual-updater-unavailable state, risking ordinary production submissions when the updater was intentionally default-off; and `AggregateHostUpdateHealthCheck` looked up the real `/health` payload's status
  under the PascalCase key `Status`, but `ProgramHelpers.WriteHealthResponseAsync` serializes with
  `Program.HealthJsonOptions` (`PropertyNamingPolicy = JsonNamingPolicy.CamelCase`), so the wire
  property is `status` -- the lookup silently always missed, meaning verify always reported
  unhealthy for a real response (masked in the original tests, which used a hand-built PascalCase
  fixture that happened to match the bug). See `HostUpdateExecutorTests`,
  `HostUpdateExecutionStartupDiGraphTests`, `HostUpdateExecutionOptionsValidatorTests`, and
  `AggregateHostUpdateHealthCheckTests` for the regression coverage.
- **Fixed (this pass) — restart reconciliation for the perimeter admission gate**: prior to this
  change, a process restart mid-execution (or a crash after reaching `RecoveryRequired`, before an
  operator drove recovery) left the journal's last recorded state on disk, but nothing on startup
  reconciled it: `InMemoryHostUpdateAdmissionGate` and every in-process `IFenceableWriter` default
  back to *open*/*not quiesced* on every process start, so new job-queue submissions were silently
  admitted again the moment the host came back up, even though the prior update never reached
  `Completed` or a confirmed recovery. `HostUpdateExecutionAvailabilityProvider.CheckAsync` (which
  `HostUpdateExecutionAvailabilityHostedService` already ran immediately at startup and then
  periodically) now also enumerates every release the journal has ever recorded
  (`IHostUpdateExecutionJournal.ListReleaseIds()`, new); for any release whose last recorded state
  is neither `Completed` nor a durably confirmed `HostUpdateRecoveryOutcome.RolledBack` (read from
  `IHostUpdateRecoveryOutcomeStore`), it immediately re-`QuiesceAsync`s every registered
  `IFenceableWriter` (closing the admission gate again, requesting every background-writer fence
  flag pause again) and reports `restart_reconciliation_pending:<releaseId>:<state>` as an
  `Unavailable` reason. A release left `RecoveryRequired` with a recorded `NeedsOperator` outcome
  stays fenced the same way, since that outcome means recovery itself did not resolve the release.
  This is intentionally detection-and-re-fence only: it never resumes or retries the update itself
  (that remains an explicit operator call to the admin API's `execute`/`recover` endpoints, which
  already resume idempotently from journal state) and never grants #2666's scheduler any standing
  automatic-execution permission. See `HostUpdateExecutionAvailabilityTests` (`CheckAsync_Release*`
  cases) for the regression coverage: mid-flight state, `RecoveryRequired` with no recorded
  outcome, `RecoveryRequired` with a recorded `NeedsOperator` outcome, `RecoveryRequired` with a
  recorded `RolledBack` outcome (correctly does not re-fence), and a `Completed` release (no-op).
- Split-topology deployments where `AppDbContext` and `SlicerDbContext` point at genuinely
  different physical databases are not yet handled by the single shared database backup/restore
  target. Preflight now fails closed (`split_database_not_supported`) whenever the two contexts'
  connection strings differ (compared only as a SHA256 fingerprint — the raw connection string,
  which may embed a password, is never logged or persisted), so a real split topology is
  explicitly refused rather than silently backing up only one database; the common
  (monolith/shared-DB) case is the only topology covered by the current backup/restore implementation;
  it is not yet an end-to-end production-support claim.
- Owned application directories (blobs/profiles/calibration/config/certs/keyrings) are fail-closed
  by default: a configured, required owned directory that is missing at backup time throws
  `HostUpdateBackupIncompleteException` instead of writing a `.empty` sentinel and reporting
  success. Real zero-byte files are valid and are checksummed in the manifest with length 0; real empty directory trees are represented by `.printfarmer-directories.json` so empty profiles/config subtrees are explicit backup content rather than false omissions. A directory can be explicitly opted into the old empty-sentinel behavior via
  `HostUpdateExecutionOptions.OptionalOwnedDirectories` only when its absence is genuinely
  expected (e.g. an optional feature that was never enabled on this host).
- Preflight also fails closed (`unmapped_service_target:<serviceId>`) if a request names a service
  ID with no corresponding `HostUpdateExecutionOptions.ServiceMappings` entry, and the digest
  verifier's container-name resolution independently throws (`service_mapping_missing:<serviceId>`)
  rather than silently falling back to a guessed container name if it is ever reached without that
  earlier guard having run.
- The required fenced-writer set is `HostUpdateExecutionOptions.RequiredFencedWriterNames`: `api-admission`, `queue-outbox-publisher`, `power-reading-prune`, `queue-retention-prune`, `backend-start-command-consumer`, `backend-control-command-consumer`, `bed-clear-acknowledgement-expiry`, `auto-dispatch`, `webhook-delivery`, and `queue-reconciliation`. The webhook fence pauses the bridge before dequeuing new delivery work, so no external HTTP delivery or webhook delivery-log write starts inside the backup/migration/apply critical section. Adding a required writer name with no registration keeps availability closed.
  `AutoDispatchBackgroundService` -- the loop that physically starts new printer-dispatch
  workers -- now consults its own dedicated `AutoDispatchFenceFlag` at the top of each trigger
  iteration: while paused it skips starting a new dispatch worker entirely (relying on the
  existing 30s durable database scan to rediscover the same eligible printer once the fence
  releases) and only acknowledges quiescence once `TrackedWorkerCount` is zero, so the fence
  coordinator cannot observe "paused" while a printer command is still physically executing.
  `AdmissionFenceableWriter.IsQuiescedAsync` reports only that the gate itself has flipped
  closed, not that every producer honors it; do not read a quiesced admission-gate report as
  proof that no new physical work can start until the remaining call sites above are wired and
  audited the same way.
- PostgreSQL/SQL Server backup and restore commands are invoked as a structured process argument
  list with the connection password passed only via an environment variable (`PGPASSWORD` /
  `SQLCMDPASSWORD`), never on the command line and never through a shell (`sh -c`) string — closing
  the earlier shell-quoting/argv-exposure risk for both directions. The interpolated T-SQL
  restore enters `SINGLE_USER WITH ROLLBACK IMMEDIATE` and uses a TRY/CATCH path that attempts `MULTI_USER` before rethrowing. The interpolated T-SQL identifier/literal (`[database]` / `N'path'`) and the interpolated sqlite3 `.backup`/`.restore` literal path are also escaped (doubled `]`/`'` respectively, the standard T-SQL/sqlite3
  convention) so a database name or backup-root path containing one of those characters cannot
  terminate the literal early and inject additional dot-command/T-SQL text into the same batch.
- **SQL Server visible backup-path mapping is now verified with a real round trip, not assumed.**
  SQL Server writes backups from the *server process's* filesystem view, not the client's, so the
  path PrintFarmer hands to `BACKUP DATABASE` must resolve to the same physical location PrintFarmer
  can later read back for verification and restore. This requires a **shared bind mount**: the
  directory PrintFarmer derives as `BackupRootDirectory` (`{RootDirectory}/backups` — configure the
  root via `HostUpdateExecution__RootDirectory`; `BackupRootDirectory` itself is a computed
  read-only value and is **not** independently configurable) must be mounted into the SQL Server
  container at the identical path — for example a Docker named volume or bind mount attached to
  both the `api`/host-update-executor container and the `sqlserver` container, each mounting it at,
  say, `/data/backups`, with `HostUpdateExecution__RootDirectory=/data` configured on the API side
  so `BackupRootDirectory` resolves to `/data/backups`. There is deliberately no separate "SQL
  Server side" directory setting: `HostUpdateBackupCoordinator` already hands this same
  `BackupRootDirectory`-derived directory straight to `BACKUP DATABASE` for every real backup with
  no translation step, so verifying any other path would not prove what production backups actually
  depend on. The SQL Server login used for the connection also needs `BACKUP DATABASE` permission
  on `master` (e.g. `sysadmin` or `db_backupoperator` in `master`) for the probe itself to succeed —
  a login scoped only to the `db_owner` role on the PrintFarmer application database is not
  sufficient, even though it may be adequate for real per-database backups. Before every
  backup/migration execution, `HostUpdateExecutionAvailabilityProvider.CheckAsync` asks the
  configured backup target to prove the mapping:
  `SqlServerProcessDatabaseBackupTarget.VerifyVisibleBackupPathMappingAsync` first lazily creates
  `BackupRootDirectory` if it does not yet exist (failing closed if it cannot, so a
  freshly-provisioned host with `RootDirectory` set but no `backups` subdirectory yet does not
  report a false negative), then removes any
  stale probe file already visible to PrintFarmer at that path (failing closed if it cannot, so a
  leftover file from an earlier check can never be mistaken for a fresh round trip), then runs a
  real `BACKUP DATABASE [master] TO DISK` probe through the same `sqlcmd` path as production
  backups, writing a small, fixed-name probe file into `BackupRootDirectory` (reused, not
  regenerated, on every check so a persistently broken mapping cannot accumulate probe files on the
  SQL Server volume — each check simply overwrites the same file via `WITH INIT`), then opens and
  reads a byte back from the identical probe file from PrintFarmer's own filesystem view (not
  merely a directory-listing/metadata check) to confirm it is actually readable, and best-effort
  deletes it afterward. The probe itself is capped at a short, fixed timeout (independent of the full
  `BackupTimeoutSeconds` used for real backups) so a hung or unreachable SQL Server cannot block
  every ~5-minute availability check for as long as a real backup would be allowed to run. A
  configuration-presence check alone is explicitly not sufficient and is not what this does — an
  absent, misconfigured (missing or relative), or unreadable-back mapping is reported as
  `facility_unavailable:sql_server_visible_backup_path_mapping_unverified:<evidence>` (e.g.
  `backup_root_directory_not_configured`, `backup_root_directory_not_absolute`,
  `probe_directory_creation_failed:<exception-type>`,
  `probe_stale_file_removal_failed:<exception-type>`, `probe_backup_invocation_failed:<exception-type>`,
  `probe_backup_command_failed:<exit-code>`, `probe_file_not_visible_from_printfarmer`) and closes
  availability for the whole executor until it is verified. This
  verification is resolved lazily (only when the availability probe or a real backup actually
  runs), so a deployment that only uses PostgreSQL or SQLite never touches SQL Server-specific
  resolution at all. See `HostUpdateDatabaseBackupTargetFactoryTests` for the fail-closed and
  successful round-trip regression coverage.
- Recovery's `NeedsOperator`/`RolledBack` outcome is now durably persisted by
  `FileHostUpdateRecoveryOutcomeStore` (one JSON file per release, atomic write-then-rename) as
  part of `RecoverAsync` itself — including when recovery is cancelled mid-flight — so the outcome
  survives a process crash immediately afterward even if whatever invoked recovery never gets a
  chance to persist it. A successful rollback releases the writer fence only after the durable `RolledBack` outcome is written; if fence release fails, recovery overwrites the outcome with `NeedsOperator` and keeps the system closed. The manual admin status endpoint now includes the latest durable recovery outcome for the requested release, so operators can see `RolledBack`/`NeedsOperator` details after the original recovery call has returned or the process has restarted.
- Backup manifests, copied payload files, and recovery outcome records now use the same durable write-through, flush-to-disk, atomic replace, and parent-directory sync primitive as the execution journal before the executor records `backup:after` or a terminal recovery result.
- Duplicate/delayed recovery is idempotent under the durable execution lock: if a previous attempt already persisted `RolledBack`, a later matching recovery call returns that terminal result without replaying restore or compose side effects. If apply may have started and prior installed image evidence is absent, recovery records `NeedsOperator` instead of reporting a successful coordinated restore that could not restore images.
- Power-loss reconciliation is now operation-specific for the unsafe side-effect checkpoints. A restart after `migration:before` without `migration:after` continues only when all migration targets report no pending migrations; a restart after `apply:before` without `apply:after` continues only when exact running digest verification succeeds. Otherwise the journal records durable `RecoveryRequired`/operator action and does not replay migrations or compose automatically.
