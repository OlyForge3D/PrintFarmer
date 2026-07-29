# Coordinator Acceptance Review — Iteration 3

## Verdict: FAIL

Iteration 3 is rejected. PR #979 must remain draft. The Inspector PASS is
invalid because it inferred missing behavior, accepted placeholders and
SQLite-only evidence, and modified production code while acting as the
independent verifier. A separate read-only Opus audit and coordinator review
confirmed the following blockers.

## Blocking Work for Builder Iteration 4

1. **Commit/build hygiene.** Keep the correct two-argument
   `ProcessSingleEventAsync(evt, ct)` call, but Builder—not Inspector—must own
   the product change. Run full CI rather than skipping tests or migration
   drift checks.

2. **Bed-clear must durably start the exact job.**
   `BedClearAcknowledgementService` currently duplicates claim writes, sets
   `Starting`, consumes the acknowledgement, and emits an event that only
   SignalR consumes. No backend command executes, so the job remains Starting
   forever. Route acknowledgement through the one fail-closed shared claim
   and atomically persist a durable backend-start command consumed by the
   adapter orchestrator before returning 202. Preserve operation key for
   exact replay versus mismatch and revalidate replay expiry/state.

3. **Fail closed on acknowledgement and telemetry.**
   `DispatchClaimService` must reject calibration when
   `AcknowledgedJobId` is null, wrong, expired, or key-mismatched. Missing
   telemetry must reject, not pass. Freshness must use capability-advertised
   limits. Current tests incorrectly prove a claim succeeds with no persisted
   acknowledgement and null telemetry; replace them.

4. **One complete authoritative claim validator.**
   Require enabled, available, non-maintenance, fresh online+idle, no Paused/
   backend-active/unresolved attempt, promoted G-code and exact authoritative
   hash/lineage, required capabilities/nozzle/tool/model/build, hard
   filament/SKU/spool sufficiency, complete project/attempt/orchestration/
   snapshot lineage, non-null exact Klipper firmware + Klipper dialect +
   upstream Orca distribution/version/container digest and every profile/
   specification/G-code hash, current printer revision, and exact persisted
   acknowledgement. Null compatibility fields fail. Persist typed blocked
   reasons without consuming acknowledgement. Enforce expected job and
   dispatch row versions.

5. **Eliminate every start bypass.**
   Production currently supplies null acknowledgement keys, so calibration
   cannot dispatch through the only claim call. Resolve and pass the persisted
   exact acknowledgement or return a typed refusal; exclude calibration from
   auto-selection until ready. Route `SlicePrintBridgeController`,
   `PrintersController`/`PrintersService` file starts, timed scheduler,
   Desktop and generic paths through shared claim+adapter orchestration.
   `JobSchedulingService` must never set Printing directly. Reject generic
   PUT transitions to Starting/Printing. Keep calibration rerun prohibition
   or implement a provenance-safe new attempt.

6. **Typed backend outcomes and real reconciliation.**
   Replace bool/success-shaped outcomes with Accepted/Rejected/
   FailedBeforeStart/Unknown and backend identity. Timeout after send remains
   Starting with lease and reconciliation required; never release/retry or
   report success. Persist backend command/job IDs. Reconciler must query the
   authoritative backend and atomically resolve/advance/release the matching
   lease; it may not merely age an upload into Unknown or log it.

7. **Complete terminal lease lifecycle.**
   Atomically clear/advance matching active job/attempt/ack/history/outbox on
   completion, print failure, cancel, abort, requeue, known pre-start failure,
   and reconciled terminal state.

8. **Fix provider fencing and migrations.**
   PostgreSQL still maps store-generated nullable `bytea` while code stamps
   app tokens, so tokens are not written and concurrent claims can both win.
   Configure `ValueGeneratedNever()` app-managed non-null concurrency tokens
   for PostgreSQL (or genuine xmin), migrate/backfill non-null values, and
   align model snapshots. SQLite runtime/snapshot currently drift. Prove
   migrated separate-context races on SQLite and PostgreSQL and native SQL
   Server rowversion behavior.

9. **Authoritative race-idempotent creation.**
   Derive classification/scope/provenance/hashes server-side from promoted
   G-code and authoritative rows; validate ownership/attempt/orchestration/
   snapshot/printer compatibility. Add SliceJobId and all immutable inputs.
   Canonical hash must include priority and material/nozzle/model/tool/
   capabilities. Catch filtered unique-index loss, reread winner, compare
   hash, and return replay or mismatch—not 500. Make `AppDbContext` required.
   Emit `Idempotency-Replayed`. Prevent calibration G-code from being queued
   as Standard through any management/analytics path.

10. **Real ordered idempotent outbox and durable work consumer.**
    `Sequence` is currently always zero, not generated or unique. Configure
    provider-correct database monotonic generation/uniqueness, stable durable
    event ID/sequence/timestamp/revisions/attempt/bed-clear envelope,
    atomic row leases, retry/backoff, consumer dedup, and crash-after-send
    recovery. Publishing must use `evt.Id`, not a new GUID. Durable consumer
    must drive scheduling/backend commands; DropOldest remains wake-up only.
    Concurrent publishers must not double-publish.

11. **Priority and scheduling consistency.**
    Centralize one Urgent > High > Normal > Low selector across readiness,
    filament checks, skip, display, automatic/batch/dispatch. Existing auto
    readiness/skip still selects Low first. Reject undefined priority on
    create/update/reorder and allocate positions transactionally. Release
    global selection lock after atomic claim, enforce configured concurrent
    starts during network work, and never discard durable work.

12. **Resource-scoped authorization/events/audit.**
    Do not auto-join all authenticated users to global Farm. Require
    `queue:read` plus authorized farm/printer/project/job subscriptions and
    enforce resource ownership in services. Publish stable schemaVersion=1
    monotonic events for every required transition, with gap/refetch support
    and no `Clients.All`, secrets, URLs, or paths. Persist audit rows in the
    same transactions for actor/resource/operation/ack/claim/start/cancel/
    override/reconciliation. Add SaveChanges immutability guard for every
    calibration/provenance/idempotency/hash/tuple field on existing PrintJob.

13. **Complete revision contract and acknowledgement invalidation.**
    Expose quoted job and dispatch ETags on authoritative reads/create.
    Enforce both claim row-version inputs and mandatory expected printer
    revision. Require `If-Match` on assignment, reorder/priority, cancel,
    delete/abort, safety override and acknowledge/start; stale is 412,
    semantic conflict 409. Public reads must expose dispatch ETag.
    Production reorder/insert/cancel/requeue/config-change paths must invoke
    exact acknowledgement invalidation. Generic `BedPreConfirmed` must never
    authorize calibration.

14. **Production-fidelity acceptance tests.**
    Add migrated SQLite/PostgreSQL/SQL Server integration/race matrix;
    concurrent create winner/replay/mismatch; all-path bypass prevention;
    backend adapter invocation from bed-clear durable command; complete
    safety/filament/tuple/hash/config/telemetry drift; acknowledgement replay/
    mismatch/reorder/insert/cancel/expiry/revision/non-consumption; typed
    accepted/known/unknown/crash outcomes and duplicate prevention; terminal
    lease cleanup; backend-probing reconciler; outbox lease/order/retry/dedup/
    crash recovery; invalid priority; scoped event/gap/auth matrix; public
    ETag flows; migration/backfill/drift. InMemory, EnsureCreated, disabled
    foreign keys, and SQLite-only tests cannot be the sole evidence.

## Process Constraints

- Continue on the same branch and draft PR #979 targeting `development`.
- Do not create another PR, merge, touch `main`, weaken tests, or mutate
  shared beads data.
- Update the PR body only after exact gates pass; retain `Closes #900`.
- Run full build, all relevant test categories, provider migration drift/
  probes, format, compliance, compose validation, and fresh CI.
