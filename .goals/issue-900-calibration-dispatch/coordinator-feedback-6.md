# Coordinator Continuation — Iteration 6

## Verdict: INCOMPLETE

Iteration 6 addressed the allocator row, command-row fencing, and a first
backend status probe, but it did not complete the original acceptance
contract or the unresolved items in `coordinator-feedback-3.md`.

## Remaining Required Work

1. **Sequence allocation must not lose caller work.** A concurrent allocator
   loser currently rolls back its outbox-producing transaction and surfaces
   `DbUpdateConcurrencyException`. Add bounded transaction retry/reload so
   every legitimate producer obtains the next unique sequence and persists
   its own event. Prove N concurrent producers produce N distinct contiguous
   sequences with no lost work across separate service providers. Validate
   PostgreSQL and SQL Server provider behavior through CI harnesses, not only
   SQLite `EnsureCreated`.

2. **Complete exact acknowledgement and durable execution semantics.**
   Verify no calibration claim can proceed without the persisted exact job,
   key, expiry, expected revisions and current config. Durable command exact
   replay must return the same command/state; mismatched key/hash is 409.
   Consumer must await shared claim + adapter and keep the command Processing/
   uncertain when timeout-after-send occurs; it must not mark Published
   merely because orchestration returned a success-shaped job.

3. **Finish authoritative claim policy.** Implement and test actual equality,
   not null-only checks: enabled/maintenance/IsAvailable, online+idle and
   capability freshness limit, Paused/backend-active/unresolved exclusion,
   authoritative promoted G-code and `GcodeSha256`, required capabilities,
   nozzle/tool/model/build, hard material/SKU/spool sufficiency, complete
   project/attempt/orchestration/snapshot lineage, Klipper firmware + Klipper
   dialect + upstream Orca distribution/version/container digest, every
   specification/profile/G-code hash, and current printer configuration
   revision. Persist typed blocked reason without consuming acknowledgement.

4. **Finish authoritative calibration creation.** Server-derive and verify
   classification/scope/provenance/hashes from promoted `GcodeFile` and
   prerequisite records; add/handle `SliceJobId` and all immutable inputs;
   enforce resource ownership; include priority/material/nozzle/model/tool/
   capabilities in canonical hash; prevent calibration artifacts from being
   queued as Standard. Catch filtered unique-index race, reread the winner,
   compare hash, and return replay/mismatch. Emit Location, quoted ETag, and
   `Idempotency-Replayed`.

5. **Eliminate every remaining production bypass.** Add concrete production
   tests for `SlicePrintBridgeController`, printer file-start endpoints,
   timed scheduler, Desktop-facing commands, generic PUT/status, scored,
   batch, automatic, rerun/requeue. Scheduler must never set Printing
   directly. Generic mutation must reject Starting/Printing. All starts go
   through shared claim + adapter orchestration.

6. **Complete typed backend outcomes and reconciliation.** Replace every bool
   result/success-shaped HTTP or auto event. Persist backend command/job
   identity before/after send. Timeout-after-send remains Starting and leased
   as Unknown/reconciliationRequired, never retried. Reconciler must query the
   adapter's command/job state (not infer only from generic printer status),
   resolve accepted/active/completed/rejected/absent/unknown, and atomically
   update attempt/job/lease/outbox/history/audit.

7. **Complete terminal lease lifecycle.** Completion, print failure, cancel,
   abort, delete, requeue, known failure and reconciled terminal states must
   clear only matching active job/attempt and invalidate/re-arm exact
   acknowledgement as policy requires. Add crash-point and stale-attempt
   fencing tests.

8. **Complete priority/scheduler behavior.** One shared semantic comparator/
   query selector must drive readiness, filament checks, skip, display,
   automatic, batch and dispatch. Reject undefined values on every mutation.
   Allocate positions transactionally/deterministically, release the global
   selection lock after claim, and honor configured concurrent network starts.

9. **Complete authorization, audit and event authority.** Resource ownership
   checks belong in services. Hub subscriptions require queue:read and
   authorized farm/printer/project/job scope; never auto-join arbitrary users
   to global Farm. Persist audit rows in the same transactions. Produce stable
   schemaVersion=1 events with durable ID/sequence/occurred time/revisions/
   attempt/bed-clear/failure for every required transition, with change-feed
   gap/refetch behavior and secret/path redaction.

10. **Complete public concurrency contract.** Authoritative reads expose both
    job and dispatch quoted ETags. Require/bind `If-Match` for acknowledge/
    start, assignment, reorder/priority, cancel, delete/abort, and safety
    override. Return 412 for stale revisions and 409 for semantic conflict.
    Invoke acknowledgement invalidation from insertion/reorder/cancel/requeue/
    config/firmware/profile/G-code change; generic BedPreConfirmed never
    authorizes calibration.

11. **Complete provider migrations and tests.** Add non-null token/allocator/
    outbox-command migration backfill and migration-applied tests for SQLite,
    PostgreSQL and SQL Server. Verify snapshots/drift. Add the full issue test
    matrix through production services: concurrent create identical/
    conflicting/terminal replay, all-path claim, bed-clear drift/replay/
    expiry/revision/non-consumption, hard safety/filament/tuple/hash/config,
    accepted/known/unknown/crash duplicate prevention, terminal cleanup,
    adapter reconciliation, outbox retry/order/dedup/restart, priority invalid
    values, scoped event/gap/auth, audit, public ETag flows, secret redaction.

Do not request acceptance review or call the task complete until every item
above and every checkbox in `goal.md` has concrete code and test evidence.
