# Host update executor

The safe-executor core (`HostUpdateExecutor`, unmodified since the foundation commit) supplies
immutable release identity contracts, exact six-target validation, a bounded process/installation
lock, a durable hash-chained journal, and a checkpoint-aware state machine. On top of that
foundation, issue #2663 adds concrete, repository-appropriate step adapters that turn the
foundation into a functional **manual-first** update path: an operator (or, later, #2666's
scheduler once it is granted standing permission — not yet the case here) submits one immutable
`HostUpdateExecutionRequest` through the admin API, and the executor drives it through
preflight → drain → fence → backup → migration → apply → verify, or into `RecoveryRequired` on any
failure. There is still no automatic/unattended execution path.

## Configuration: `HostUpdateExecutionOptions`

Bound from the `HostUpdateExecution` configuration section (see
`src/infra/Services/HostUpdates/HostUpdateExecutionOptions.cs`) and enforced at process start by
`HostUpdateExecutionOptionsValidator` via `ValidateOnStart()`:

- **`RootDirectory`** (required, no default): an absolute, host-controlled, persistent directory
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
  environment variable, and `ghcr.io/olyforge3d/printfarmer-*` repository); `HealthCheckBaseUrl`
  for readiness probes.

## Concrete adapters (`src/infra/Services/HostUpdates/`)

| Step | Adapter | Notes |
|---|---|---|
| Preflight | `HostUpdatePreflightCheck` | Revalidates installed version/digests, provider allowlist, disk free space, and every configured migration target's provider name before anything else runs. |
| Drain | `HostUpdateDrainCoordinator` | Closes the admission gate to new submissions/scheduling, then bounded-polls `IActiveWorkObservationPort` (backed by `AppDbContext`) for active prints/pending outbox work to finish naturally — never a blind cancellation. Times out closed. |
| Fence | `HostUpdateFenceCoordinator` | Proves every registered `IFenceableWriter` (the admission gate; the queue-outbox publisher, `PowerReadingPruneService`, and `QueueRetentionPruneService` via their own independent `IHostUpdateWriterActivityFlag`-derived instances) has actually quiesced before backup — bounded-polled, not assumed. The executor's availability provider also fails closed (`insufficient_fenced_writers:<names>`) if `HostUpdateExecutionOptions.RequiredFencedWriterNames` names a writer with no registered `IFenceableWriter`. |
| Backup | `HostUpdateBackupCoordinator` + `HostUpdateDatabaseBackupTargetFactory` + `DirectoryCopyBackupTarget` | Coordinated, checksummed backup of the database (via the host's own `sqlite3`/`pg_dump`/`sqlcmd` tooling — never a duplicate ad-hoc dump, and never invoked through a shell string) plus every owned directory. An externally-owned database (`DatabaseExternallyOwned = true`) always fails closed rather than silently skipping. |
| Migration | `HostUpdateMigrationCoordinator` + `DbContextMigrationTarget<AppDbContext>`/`<SlicerDbContext>` | Wraps the existing `ProviderAwareMigrationRunner` under the executor's own single-writer lock; inspects the actual installed provider state and fails closed on an unsupported/mixed configuration rather than duplicating migration logic. |
| Apply | `HostUpdateImageApplier` | Applies immutable `repository@sha256` images via the existing compose templates and `docker compose up -d`, using an explicit process argument list — never shell interpolation, never a mutable tag. |
| Verify | `HostUpdateHealthVerifier` + `AggregateHostUpdateHealthCheck` | Confirms exact running digests (`docker inspect`) plus the aggregated `/health` endpoint's JSON body has a top-level `Status` of exactly `"Healthy"` (never merely an HTTP 200 — ASP.NET Core's default health middleware also returns 200 for `"Degraded"`) before reporting healthy and allowing writers to reopen. |
| Recovery | `HostUpdateRecoveryCoordinator` + `DefaultHostUpdateRecoveryCompatibilityEvaluator` + `ProcessHostUpdateRestoreExecutor` + `FileHostUpdateRecoveryOutcomeStore` | On any failure, decides image-only rollback vs. coordinated restore, restores both databases and owned storage/config together via the same provider-native restore tooling (structured process args/env only — no shell string, no password on argv), and durably persists the `RolledBack`/`NeedsOperator` outcome itself (survives a crash immediately after cancellation) before reporting it. Resumable/idempotent via the same durable journal after a process restart. |

## Availability contract

`IHostUpdateExecutionAvailabilityProvider` (`HostUpdateExecutionAvailability.cs`) positively probes
— never assumes — that the executor is actually usable: the root directory is writable, the
journal is not corrupt, at least one migration and one backup target are configured, every
configured compose file exists on disk, and the container runtime (`docker version`) is reachable.
`HostUpdateExecutionAvailabilityHostedService` computes this immediately at process startup
("restart reconciliation" — a fresh process re-proves its own readiness rather than trusting a
previous run's state) and then periodically rechecks, publishing every result into the singleton
`HostUpdateExecutionAvailabilityHolder` that both the admin API and a future #2666 scheduler poll
without re-running the probe on every read. `Available` carries no reasons; `Unavailable` always
carries the exact missing mechanism(s) (e.g. `root_directory_unwritable:...`,
`compose_file_missing:...`, `docker_runtime_unavailable`) so an operator is never left guessing.

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
bind one immutable request per call. Both endpoints now gate on the same
`HostUpdateExecutionAvailabilityHolder` the availability hosted service maintains: a request that
arrives while the executor reports `Unavailable` (including while restart reconciliation still
reports an unresolved prior release) is rejected with `503 Service Unavailable` and the exact
reasons, before it ever reaches `IHostUpdateExecutor`/`IHostUpdateRecoveryCoordinator` — see
`HostUpdateControllerAvailabilityTests`. Replay/duplicate-submission protection currently relies on the
executor's own file-based journal/lock rather than a controller-level idempotency key; stale-plan
checking against the separate staging/authorization layer (`HostUpdateFoundation`/
`SignedUpdateInfrastructure`) is a known remaining gap — see the acceptance-gap list tracked
against issue #2663.

## Known limitations

- Bishop/Hicks review of the base commit surfaced (and this slice fixed, with tests) five
  critical defects: `FileHostUpdateExecutionJournal.Append` silently truncated the entire
  hash-chained journal to its newest record on every append (a temp-file-then-move pattern that
  wrote only the new line before moving it over the real path); `AddHostUpdateExecution`
  registered `IEnumerable<IHostUpdateBackupTarget>` explicitly with a factory that itself called
  `sp.GetServices<IHostUpdateBackupTarget>()`, which the .NET container resolves as
  `IEnumerable<IHostUpdateBackupTarget>` again -- an unbounded self-recursive registration that
  would `StackOverflowException` the process the first time anything resolved the backup target
  list; `HostUpdateExecutionOptionsValidator`'s `.ValidateOnStart()` required `RootDirectory` (and
  every other host-update-execution option) unconditionally, but no supported deployment shape
  configures it, so the entire API crashed at startup the moment `AddHostUpdateExecution` was
  registered; and `AggregateHostUpdateHealthCheck` looked up the real `/health` payload's status
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
  (monolith/shared-DB) case is fully supported.
- Owned application directories (blobs/profiles/calibration/config/certs/keyrings) are fail-closed
  by default: a configured, required owned directory that is missing at backup time throws
  `HostUpdateBackupIncompleteException` instead of writing a `.empty` sentinel and reporting
  success. A directory can be explicitly opted into the old empty-sentinel behavior via
  `HostUpdateExecutionOptions.OptionalOwnedDirectories` only when its absence is genuinely
  expected (e.g. an optional feature that was never enabled on this host).
- Preflight also fails closed (`unmapped_service_target:<serviceId>`) if a request names a service
  ID with no corresponding `HostUpdateExecutionOptions.ServiceMappings` entry, and the digest
  verifier's container-name resolution independently throws (`service_mapping_missing:<serviceId>`)
  rather than silently falling back to a guessed container name if it is ever reached without that
  earlier guard having run.
- Only four writers are concretely fenced today (the admission gate, the queue outbox
  publisher, `PowerReadingPruneService`, and `QueueRetentionPruneService`); other background
  schedulers/bridges/API replicas/workers still need `IFenceableWriter` implementations
  registered as they are identified. Remaining known unfenced hosted services:
  `MaintenanceAlertHostedService`, `CatalogUpdateDetectionService`,
  `VerifiedReleaseDiscoveryMonitorService`, `OrphanedJobSyncStartupService`,
  `HistorySeedingBackgroundService`, `ActiveExternalJobSyncBackgroundService`. The availability
  provider fails closed (`insufficient_fenced_writers:<names>`) only for names explicitly listed
  in `HostUpdateExecutionOptions.RequiredFencedWriterNames`; it cannot detect a writer that was
  never added to that list in the first place, so extending coverage still requires deliberate,
  audited work per writer rather than a generic scan. The admission gate itself
  (`IHostUpdateAdmissionGate`) is wired into exactly one real submission chokepoint today:
  `JobQueueService.AddJobToQueueAsync` (which "OctoPrint upload+print", "Manual queue from UI",
  and "Direct API calls" all funnel through) consults `IsClosedAsync` and throws
  `HostUpdateAdmissionClosedException` rather than admitting a new print job while the drain step
  has the gate closed. It is **not yet** wired into printer-command dispatch
  (`JobQueueController`'s `/dispatch`/`/dispatch-to`, `AutoDispatchController`), slicer-job
  submission, or any bridge/webhook ingress path — those remain open call sites that can still
  admit new work during a drain. `AdmissionFenceableWriter.IsQuiescedAsync` reports only that the
  gate itself has flipped closed, not that every producer honors it; do not read a quiesced
  admission-gate report as proof that no new physical work can start until the remaining call
  sites above are wired and audited the same way.
- PostgreSQL/SQL Server backup and restore commands are invoked as a structured process argument
  list with the connection password passed only via an environment variable (`PGPASSWORD` /
  `SQLCMDPASSWORD`), never on the command line and never through a shell (`sh -c`) string — closing
  the earlier shell-quoting/argv-exposure risk for both directions. The interpolated T-SQL
  identifier/literal (`[database]` / `N'path'`) and the interpolated sqlite3 `.backup`/`.restore`
  literal path are also escaped (doubled `]`/`'` respectively, the standard T-SQL/sqlite3
  convention) so a database name or backup-root path containing one of those characters cannot
  terminate the literal early and inject additional dot-command/T-SQL text into the same batch.
- Recovery's `NeedsOperator`/`RolledBack` outcome is now durably persisted by
  `FileHostUpdateRecoveryOutcomeStore` (one JSON file per release, atomic write-then-rename) as
  part of `RecoverAsync` itself — including when recovery is cancelled mid-flight — so the outcome
  survives a process crash immediately afterward even if whatever invoked recovery never gets a
  chance to persist it. It does not yet expose an operator-facing read API beyond the store
  itself; the admin API does not currently surface historical recovery outcomes.
- The manual admin API's request fingerprint binding (same `ReleaseId` with a changed
  sequence/manifest/source/channel/targets resuming a stale plan) and the executor's crash-resume
  idempotency semantics for individual side-effecting operations are owned by the scheduler
  integration work (issue #2665/#2666) rather than this physical-adapter slice; see that work's
  acceptance criteria for current status.

