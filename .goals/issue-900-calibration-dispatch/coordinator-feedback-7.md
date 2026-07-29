# Final Acceptance Audit — Iteration 7

## Verdict: FAIL

The independent Opus audit found six critical, eight high, and one medium
blocking defect. Continue implementation; do not request another audit until
every item below is fixed with production-service tests.

1. Reconciliation has no persisted backend identity: production passes null
   `BackendJobId`, so a physically printing unknown job is classified absent,
   its lease is cleared, and it can duplicate-start. Persist backend command/
   job identity and reconcile through adapter job/command APIs; unmatched
   printing must never mean absent.

2. Calibration create still does read-then-insert without catching
   `DbUpdateException` for the filtered unique index. Concurrent losers get
   500 instead of rereading the winner and returning replay/mismatch.

3. Calibration classification/provenance/hashes remain client-supplied.
   Server must inspect authoritative promoted immutable `GcodeFile` lineage;
   prevent that artifact from being queued as Standard through primary,
   analytics or management paths.

4. Generic `PUT /api/job-queue/{id}` still sets arbitrary Starting/Printing,
   accepts undefined priority, reassigns printer without `If-Match` or
   acknowledgement invalidation.

5. `JobSchedulingService` still sets Printing directly. `SlicePrintBridge`
   and printer file-start endpoints still call adapters without claim.
   Route every bypass through shared claim + adapter orchestration.

6. Durable backend command is marked Published on known failure and Unknown
   because `DispatchJobWithAckAsync` returns normally for all outcomes.
   Return a typed outcome; Published only means confirmed accepted command.
   Unknown remains leased/reconcilable and is never retried blindly.

7. Claim lacks hard filament/SKU/spool, capabilities, nozzle/tool/model/build,
   capability-advertised telemetry freshness, promoted immutable G-code, and
   fail-closed G-code hash checks. Both expected row-version inputs are dead.
   Claim transaction writes neither job state history nor audit.

8. Bed-clear acknowledgement performs no fresh telemetry, hard filament, or
   complete tuple/hash/lineage validation; expected printer revision remains
   optional. Its filament and offline outcomes are unreachable.

9. Canonical hash omits priority/material/nozzle/model/tool/capabilities and
   `SliceJobId`; `PrintJob` lacks `SliceJobId`. Add all immutable inputs.

10. Event envelope generates a new ID/time per delivery and carries no durable
    sequence, revisions, attempt, bed-clear or failure. Publisher does not
    atomically lease rows before send, so concurrent publishers can duplicate.

11. Public revision contract remains incomplete: no
    `Idempotency-Replayed` header; authoritative GET lacks job/dispatch ETags;
    `If-Match` is absent from PUT/assignment/priority/cancel/abort/delete/
    override. Implement 412 versus 409 mapping.

12. Auto readiness/skip still sorts priority ascending (Low first).
    Undefined priority remains accepted on create/generic update. Add one
    shared ordering selector and transactional queue positions.

13. No audit rows are persisted for required operations. Add durable
    actor/resource/operation/outcome audit in the same transactions.

14. PostgreSQL/SQL Server tests silently return when env vars are absent and
    executed tests use EnsureCreated with foreign keys disabled. Ensure
    provider jobs fail/skip visibly per test framework and CI provisions them;
    apply real migrations and validate backfill/fencing.

15. Mandated production test matrix remains absent: all start paths,
    concurrent queue create, durable command consumer, reconciler,
    terminal cleanup, filament, event isolation/gaps/dedup, ETag mutations,
    ack invalidation drift, auth scope, audit and redaction. Replace enum/
    metadata/direct-service proxy tests with production call-chain tests.

16. Immutability guard checks current JobKind and can be bypassed by flipping
    calibration to Standard in the same save. Use original kind and reject
    JobKind mutation itself.

All existing coordinator feedback remains authoritative. Do not report
completion until these exact defects and the whole `goal.md` matrix pass.
