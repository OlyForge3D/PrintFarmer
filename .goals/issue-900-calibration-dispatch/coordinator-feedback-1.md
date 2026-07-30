# Coordinator Acceptance Review — Iteration 1

## Verdict: FAIL

The Inspector's PASS is overridden by a production call-site acceptance
review. PR #979 remains draft. Builder iteration 1 added partial schema and
abstractions but did not implement the authoritative issue contract through
real production paths.

## Blocking Gaps

1. **Canonical calibration creation/idempotency is absent.**
   `JobQueueController.cs:70-107`, `QueueDtos.cs:72-145`,
   `JobQueueService.cs:278-361`, and analytics controller `227-254` do not
   implement the calibration create contract. Extend primary
   `POST /api/job-queue` to validate job kind, authoritative promoted
   GcodeFile/provenance, exact printer/config, tuple/hashes/capabilities,
   authenticated subject/scope, and `Idempotency-Key`. Compute canonical
   SHA-256 over every immutable input. Implement first/replay/terminal replay/
   mismatch/invalid/missing/incompatible outcomes, separate-DbContext
   unique-index race reread-and-compare, and winner-only creation outbox.
   Reject calibration creation through analytics. Enforce immutable fields
   and include missing `SliceJobId` and `GcodeSha256`.

2. **All production start paths bypass the claim.**
   `JobQueueController:195-206`, `JobDispatchService:78-140`, automatic
   dispatch `326-355`, batch `382`, scheduler `286-317`,
   `JobQueueService:569-645`, and rerun
   `PrintJobManagementService:1273-1290` must route through one
   claim-plus-adapter orchestrator. Reject direct PUT transitions to
   `Starting`/`Printing`, require `queue:start`, and preserve calibration
   provenance/new-attempt semantics on rerun.

3. **Claim safety/fencing/lifecycle is incomplete.**
   Within the atomic fenced claim enforce enabled/not-maintenance, fresh
   authoritative online+idle telemetry, Paused/backend-active/unresolved
   attempt exclusion, promoted G-code/hash, required capabilities, hard
   filament/spool policy, calibration lineage, exact Klipper + Klipper +
   upstream Orca tuple/version/digest/profile/spec hashes, current printer
   revision, and mandatory exact acknowledgement. Apply both expected row
   versions and persist one winner. Advance/clear leases on acceptance,
   terminal, cancel, known failure, and reconciliation.

4. **Bed-clear endpoint does not start or durably command work.**
   Perform exact-job/queue-head/telemetry/tuple/hash/config/filament
   validation and atomically claim or persist a durable command before `202`.
   Honor expected printer configuration revision. Replay must recheck
   expiry/state. Add production invalidation callers. Incompatibility must not
   consume the acknowledgement.

5. **Provider fencing is broken.**
   PostgreSQL/SQLite nullable BLOB row versions remain null, allowing
   multiple winners. Implement provider-correct non-null token generation/
   stamping on every `PrintJob` and `PrinterDispatchState` write (or native
   equivalent), backfill migrations, and prove separate-context races yield
   one winner for SQLite, PostgreSQL, and SQL Server.

6. **Backend failures remain success-shaped and uncertain retries unsafe.**
   Fix `PrintJobManagementService:783-788,889-957,996` and auto dispatch
   `357-369`. Return typed outcomes and emit success only after backend
   acceptance. Known pre-start failure releases safely. Unknown response
   stays `Starting` with persisted backend identifiers and
   `reconciliationRequired`, never blindly retries, and has startup/periodic
   reconciliation plus crash-point tests.

7. **Outbox is inert.**
   Add database-generated monotonic sequence and uniqueness, transactional
   producers for creation and every required transition, hosted idempotent
   publisher/consumer with retry/backoff/dedup, and startup/periodic
   reconciliation. The process-local `DropOldest` channel may wake only.

8. **Priority/scheduler behavior remains wrong.**
   Centralize Urgent > High > Normal > Low across every queue/automatic/batch
   query; reject undefined values on create/update/reorder; allocate positions
   transactionally/deterministically; treat Paused as active; enforce
   freshness/capabilities/filament; release the global orchestration lock
   immediately after atomic claim.

9. **ETag/concurrency APIs are missing.**
   Expose job and dispatch revisions in bodies and quoted ETags. Require
   `If-Match` for acknowledgement/start, assignment, reorder/priority,
   cancellation, and safety overrides; bind expected EF original values;
   return `412` for expected concurrency and `409` for semantic conflicts.
   Public authoritative reads must expose the dispatch-state ETag.

10. **Authenticated versioned events and audit are absent.**
    Publish durable monotonic schemaVersion=1 events for creation/replay,
    assignment/reorder/block, bed-clear lifecycle, claim, progress/failure,
    acceptance, uncertainty/reconciliation, and terminal transitions only to
    authorized farm/printer/project/job groups. REST/change feed is
    authoritative after gaps. Never use protected `Clients.All` or expose
    secrets/private paths. Enforce all permissions/resource ownership and
    audit actor/job/printer/attempt/key/ack/claim/override/reconciliation.

11. **Migrations/backfill/compatibility are partial.**
    Align SQLite/PostgreSQL/SQL Server; backfill jobs to Standard/null and
    dispatch state conservatively; fix outbox sequence/fencing, indexes, and
    immutable validation while preserving standard clients.

12. **Issue-mandated integration/race/crash/security tests are missing.**
    Replace controller-mock/default-only evidence with canonical create/
    replay/mismatch/terminal/missing-key tests; identical/conflicting
    concurrent inserts across DbContexts; one creation outbox; two
    processes/jobs one lease; every start path; Paused/uncertain/backend-active
    blockers; all bed-clear drift/replay/revision/non-consumption cases; hard
    filament; urgent/invalid priority; known/unknown/crash reconciliation and
    duplicate prevention; outbox retry/dedup/order/restart; scoped events/gap
    recovery; authorization resource matrix; and secret/path redaction. Run
    provider drift/probes and full CI.

## Completion Conditions

- Fix all gaps by tracing and modifying real production call sites rather
  than adding unused abstractions.
- Keep PR #979 draft on the same branch and base `development`.
- Do not create a replacement PR, merge, use `main`, weaken tests, or mutate
  shared beads data.
- After gates pass, update the existing PR body with exact evidence and keep
  `Closes #900`. Do not undraft until the coordinator explicitly authorizes it.
