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

Bound from the `HostUpdateExecution` configuration section (see
`src/infra/Services/HostUpdates/HostUpdateExecutionOptions.cs`). Root and general executor
settings are validated by `HostUpdateExecutionOptionsValidator`; executable mappings are
validated fail-closed by `ConfiguredHostUpdateExecutableResolver`. The executor remains default-off when the root is unset: validation permits process startup, but all derived executor paths now throw `root_directory_not_configured` instead of resolving under the current working directory, and the runtime availability provider reports `Unavailable` until a writable host-controlled root is configured:

- **`HostExecutablePaths`** (required for process execution): explicit logical-tool to absolute executable
  path mappings for `docker`, `sqlite3`, `pg_dump`, `pg_restore`, and `sqlcmd`. Bare names are rejected
  and the ambient `PATH` is never consulted. The executable filename and any `.exe` extension must
  be lowercase; for example, `C:\Program Files\Docker\docker.exe` is accepted but
  `C:\Program Files\Docker\DOCKER.EXE` is rejected. Missing, relative, or whitespace-only mappings
  keep process execution unavailable and are reported in availability status rather than crashing startup.
  Configure host-controlled paths such as
  `HostUpdateExecution__HostExecutablePaths__docker=/usr/bin/docker`,
  `HostUpdateExecution__HostExecutablePaths__sqlite3=/usr/bin/sqlite3`,
  `HostUpdateExecution__HostExecutablePaths__pg_dump=/usr/bin/pg_dump`,
  `HostUpdateExecution__HostExecutablePaths__pg_restore=/usr/bin/pg_restore`, and
  `HostUpdateExecution__HostExecutablePaths__sqlcmd=/opt/mssql-tools18/bin/sqlcmd` (or equivalent
  JSON/YAML dictionary nesting). On Windows, use absolute paths to the installed `.exe` files.
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
  environment variable, and `ghcr.io/olyforge3d/printfarmer-*` repository); `HealthCheckBaseUrl`
  for readiness probes.

## Concrete adapters (`src/infra/Services/HostUpdates/`)

| Step | Adapter | Notes |
|---|---|---|
| Preflight | `HostUpdatePreflightCheck` | Revalidates installed version/digests, provider allowlist, disk free space, and every configured migration target's provider name before anything else runs. |
| Drain | `HostUpdateDrainCoordinator` | Closes the admission gate to new submissions/scheduling, then bounded-polls every registered `IActiveWorkObservationPort`: `AppDbContext` active prints/pending outbox work plus `SlicerDbContext` active processing leases. In-flight slicer lease progress/completion/failure remains allowed so already claimed work can finish naturally; new enqueue/claim is blocked. Times out closed and never blind-cancels physical work. |
| Fence | `HostUpdateFenceCoordinator` | Proves every registered `IFenceableWriter` (the admission gate; the queue-outbox publisher, `PowerReadingPruneService`, and `QueueRetentionPruneService` via their own independent `IHostUpdateWriterActivityFlag`-derived instances) has actually quiesced before backup — bounded-polled, not assumed. The executor's availability provider also fails closed (`insufficient_fenced_writers:<names>`) if `HostUpdateExecutionOptions.RequiredFencedWriterNames` names a writer with no registered `IFenceableWriter`. |
| Backup | `HostUpdateBackupCoordinator` + `HostUpdateDatabaseBackupTargetFactory` + `DirectoryCopyBackupTarget` | Coordinated, checksummed backup of the database (via the host's own `sqlite3`/`pg_dump`/`sqlcmd` tooling — never a duplicate ad-hoc dump, and never invoked through a shell string) plus every owned directory. An externally-owned database (`DatabaseExternallyOwned = true`) always fails closed rather than silently skipping. |
| Migration | `HostUpdateMigrationCoordinator` + `DbContextMigrationTarget<AppDbContext>`/`<SlicerDbContext>` | Intentionally unavailable for production execution until a target-image/dedicated migration runner exists. `DbContextMigrationTarget<T>` explicitly throws through the `HostUpdateMigrationStep.cs` implementation; the current/old API assembly's `ProviderAwareMigrationRunner` is not an approved forward-update path. |
| Apply | `HostUpdateImageApplier` | Stages each immutable `repository@sha256` image first with `docker image pull` (`--platform` on forward execution), then applies via the existing compose templates and `docker compose up -d --no-build --pull never`, using an explicit process argument list — never shell interpolation, never a mutable tag. Production DI wraps this runner in `ConstrainedHostUpdateProcessRunner`, which permits only the audited tool names (with explicit `.exe` support) and accepts rooted paths only from trusted host tool directories; unrelated names and untrusted rooted paths are rejected before launch. If staging any image fails, compose mutation is not attempted. |
| Verify | `HostUpdateHealthVerifier` + `AggregateHostUpdateHealthCheck` + `DigestHostUpdateHealthCheck` | Confirms exact running digests via a two-step `docker container inspect --format {{.Image}}` → `docker image inspect --format {{index .RepoDigests 0}}` probe (`.RepoDigests` exists only on image-inspect output, never on container-inspect output -- see "Known limitations" for the bug this replaced) plus the aggregated `/health` endpoint's JSON body has a top-level `Status`/`status` of exactly `"Healthy"` and every configured required result entry (default: `comprehensive`, `signalr`, `spoolman`) is present and healthy. Verification also fails before probing if the digest map is not the exact configured service set, so partial target mappings cannot reopen writers. |
| Recovery | `HostUpdateRecoveryCoordinator` + `DefaultHostUpdateRecoveryCompatibilityEvaluator` + `ProcessHostUpdateRestoreExecutor` + `FileHostUpdateRecoveryOutcomeStore` | On any failure, decides image-only rollback vs. coordinated restore, restores both databases and owned storage/config together via the same provider-native restore tooling (structured process args/env only — no shell string, no password on argv), and durably persists the `RolledBack`/`NeedsOperator` outcome with the same write-through/atomic-replace primitive used by the journal. Restored payload files are flushed before the destination tree is synced; unmapped manifest targets fail closed. A duplicate/restarted `RolledBack` recovery does not replay restore/apply, but it does re-drive idempotent fence release if the previous process crashed before reopening. |

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
`compose_file_missing:...`, `docker_runtime_unavailable`, `insufficient_fenced_writers:webhook-delivery`, a code-owned `facility_unavailable:...`, or `host_executable_not_configured:<tool>`) so an operator is never left guessing. In the current production code, availability is unconditionally closed by every entry in `CodeOwnedUnavailableFacilities` (`HostUpdateExecutionAvailability.cs`): target-image migration runner, queue-reconciliation writer fencing, and SQL Server visible backup-path mapping. It is also closed when a required audited host tool path is not configured (`host_executable_not_configured:<tool>`). Separately, unsigned legacy installs remain blocked by the `NotManaged` / `ManagedEligibilityNotEstablished` inventory state until protected bootstrap and trusted state are established. These are unimplemented or unconfigured fail-closed mechanisms, not missing acceptance evidence; they must be addressed before this executor can report production `Available`.

The process boundary is production-ready independently of those facilities:
`ConfiguredHostUpdateExecutableResolver` requires an explicit absolute path for each audited native
tool, and `ConstrainedHostUpdateProcessRunner` rejects bare names, rejects ambient `PATH` lookup,
allows only the audited tools, and accepts rooted paths only when explicitly configured or in fixed
system directories. It still delegates through the existing no-shell `ArgumentList` runner. It does
not bootstrap an updater, make unsigned legacy releases eligible, or change the manual authorization
boundary. A future trusted bootstrap implementation must be selected by the release/operator owners
before any of the remaining availability blockers are removed.

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
- Six writer paths are concretely fenced today: the durable admission gate, the queue outbox publisher, `PowerReadingPruneService`, `QueueRetentionPruneService`, `AutoDispatchBackgroundService`, and outbound `WebhookService` delivery. The webhook fence pauses the bridge before dequeuing new delivery work, so no external HTTP delivery or webhook delivery-log write starts inside the backup/migration/apply critical section. Other background schedulers/bridges/API replicas/workers still need `IFenceableWriter` implementations registered as they are identified; adding a required writer name with no registration keeps availability closed.
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
- Recovery's `NeedsOperator`/`RolledBack` outcome is now durably persisted by
  `FileHostUpdateRecoveryOutcomeStore` (one JSON file per release, atomic write-then-rename) as
  part of `RecoverAsync` itself — including when recovery is cancelled mid-flight — so the outcome
  survives a process crash immediately afterward even if whatever invoked recovery never gets a
  chance to persist it. A successful rollback releases the writer fence only after the durable `RolledBack` outcome is written; if fence release fails, recovery overwrites the outcome with `NeedsOperator` and keeps the system closed. The manual admin status endpoint now includes the latest durable recovery outcome for the requested release, so operators can see `RolledBack`/`NeedsOperator` details after the original recovery call has returned or the process has restarted.
- Backup manifests, copied payload files, and recovery outcome records now use the same durable write-through, flush-to-disk, atomic replace, and parent-directory sync primitive as the execution journal before the executor records `backup:after` or a terminal recovery result.
- Duplicate/delayed recovery is idempotent under the durable execution lock: if a previous attempt already persisted `RolledBack`, a later matching recovery call returns that terminal result without replaying restore or compose side effects. If apply may have started and prior installed image evidence is absent, recovery records `NeedsOperator` instead of reporting a successful coordinated restore that could not restore images.
- Power-loss reconciliation is now operation-specific for the unsafe side-effect checkpoints. A restart after `migration:before` without `migration:after` continues only when all migration targets report no pending migrations; a restart after `apply:before` without `apply:after` continues only when exact running digest verification succeeds. Otherwise the journal records durable `RecoveryRequired`/operator action and does not replay migrations or compose automatically.
