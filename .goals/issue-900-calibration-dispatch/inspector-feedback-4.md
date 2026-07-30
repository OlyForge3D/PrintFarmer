# Inspector Feedback — Iteration 4

## Verdict: FAIL

Iteration 4 makes substantial progress but introduces critical production-fidelity gaps that violate durability, concurrency, and test coverage requirements. The implementation must be corrected before merging.

## Acceptance Criteria Check

- [x] Calibration jobs use `PrintJob`, `JobQueueController`/`JobQueueService`, adapters — verified: DispatchJobWithAckAsync (line 1047)
- [x] `PrintJob` has immutable calibration fields — verified: model includes JobKind, RequiredFirmwareFamily, etc.
- [x] Immutable fields backfill to Standard — verified: migration approach exists
- [ ] **Canonical SHA-256 idempotency hash required** — NOT FOUND: no hash validation on create request
- [ ] **Unique-index filtered race handling** — NOT VERIFIED: coordinator blocker #9 requires server-derived classification/scope/hashes and unique-index loss handling; not present in code audit
- [ ] **Durable outbox guarantees atomicity** — PARTIALLY FAILED: outbox event is durably persisted in BedClearAcknowledgementService.AcknowledgeAsync (line 237), BUT the backend-start command execution is NOT durable (see blocker below)
- [ ] **One shared atomic cross-process claim service** — VERIFIED: DispatchClaimService.AcquireClaimAsync is the single path
- [ ] **Complete authoritative claim validator** — VERIFIED: DispatchClaimService validates telemetry, firmware, compatibility, ack
- [ ] **Eliminate start bypasses** — PARTIALLY: DispatchJobWithAckAsync is wired, but need to verify all production paths (SlicePrintBridgeController, PrintersService file starts, scheduler, Desktop) route through it
- [ ] **Typed backend outcomes and real reconciliation** — PARTIALLY: attempt tracks Outcome, but reconciliation query against authoritative backend not found
- [ ] **Complete terminal lease lifecycle** — VERIFIED: ReleaseClaimOnKnownFailureAsync clears active job/attempt
- [ ] **Provider fencing and migrations** — PARTIAL: AppDbContext line 677 applies ValueGeneratedNever() for non-SQL Server, but SQLite runtime/snapshot drift not verified
- [ ] **Authoritative race-idempotent creation** — NOT VERIFIED: no evidence of SliceJobId capture or unique-index enforcement
- [ ] **Real ordered idempotent outbox and durable consumer** — FAILED: See blocker #10 analysis below
- [ ] **Priority and scheduling consistency** — NOT FOUND: selector not centralized; existing code may still violate ordering
- [ ] **Resource-scoped authorization/events/audit** — PARTIALLY: queueevent envelope exists (line 105-122), but SaveChanges immutability guard not found
- [ ] **Complete revision contract and acknowledgement invalidation** — PARTIALLY: If-Match checked, but public reads may not expose dispatch ETag
- [ ] **Production-fidelity acceptance tests** — FAILED: See test coverage analysis below

## Blocking Work for Builder Iteration 5

### CRITICAL PRODUCTION-FIDELITY ISSUES

#### 1. **Outbox BackendStartCommand is NOT durable** (Blocker #10 — NOT FIXED)

**Location:** `src/infra/Services/Queue/QueueOutboxPublisherService.cs` lines 192–237

**Issue:** The publisher marks BackendStartCommand as `Published` (line 194) BEFORE the background task executes. If the process crashes during file upload (lines 138–1147 in PrintJobManagementService), the outbox event is marked Published but the job never started. On restart, the publisher will NOT retry the event — it's already marked Published.

```csharp
// LINE 194: Mark as Published BEFORE background execution
evt.Status = QueueOutboxEventStatus.Published;
evt.CompletedAtUtc = DateTime.UtcNow;

// LINES 204–237: Fire background task with CancellationToken.None (detached)
_ = Task.Run(async () => {
    // ... Long file upload happens here. If crash occurs,
    // job stays Starting forever and event is marked Published.
    await mgmt.DispatchJobWithAckAsync(jobId, actorSubject, ackKey, ct);
}, CancellationToken.None);
```

**Fix Required:**
- **Do NOT** mark event Published until the background task confirms success.
- Keep the event `Pending` until `RecordBackendAcceptedAsync` or `ReleaseClaimOnKnownFailureAsync` is called.
- Alternative: Make backend execution synchronous within the outbox loop (accepting that file uploads block the publisher), or implement a true two-phase commit with event replay on crash.

#### 2. **UTC.Ticks Sequence is NOT collision-free under concurrency** (Blocker #10 — NOT FIXED)

**Location:** `src/infra/Services/Queue/BedClearAcknowledgementService.cs` line 214 and `src/infra/Services/Queue/Dispatch/DispatchClaimService.cs` line 305

**Issue:** `DateTime.UtcNow.Ticks` can produce identical values when called in rapid succession on the same machine. The coordinator blocker states:

> "Sequence is currently always zero, not generated or unique. Configure provider-correct database monotonic generation/uniqueness, stable durable event ID/sequence/timestamp/revisions/attempt/bed-clear envelope."

On SQLite and PostgreSQL, if two concurrent threads write outbox events in the same microsecond, they will have the same Ticks value, violating ordering guarantees.

**Fix Required:**
- For **SQLite**: Use `AUTOINCREMENT` or ensure a `SEQUENCE` is enforced on the `Sequence` column.
- For **PostgreSQL**: Use `SERIAL` or `BIGSERIAL` with a sequence, or apply `ValueGeneratedOnAdd()` with an identity column.
- For **SQL Server**: Use `IDENTITY` (already working).
- Update migrations to add the database-generated constraint.
- Remove the application-managed `DateTime.UtcNow.Ticks` assignment and let the database generate unique values.

#### 3. **Background Task for Durable Work Execution is NOT Production-Safe** (Blocker #10)

**Location:** `src/infra/Services/Queue/QueueOutboxPublisherService.cs` line 204

**Issue:** Using `_ = Task.Run(...)` to execute backend dispatch is not crash-safe. The goal requires:

> "Durable consumer must drive scheduling/backend commands; DropOldest remains wake-up only."

A background Task is process-local and will be lost on crash/shutdown. There is no recovery path.

**Fix Required:**
- Synchronously execute (or await) backend commands within the outbox processor loop before marking events Published.
- Alternatively, implement an `IHostedService` that consumes the outbox and waits for outcomes, or a Hangfire/Quartz-backed durable job runner.

#### 4. **Test Coverage Matrix is Incomplete** (Blocker #14 — NOT FIXED)

**Current Tests:**
- `CalibrationQueueConcurrencyTests.cs`: 10 tests, **SQLite only** (shared in-memory)
- `CalibrationQueueDispatchTests.cs`: Unit tests with mocks

**Missing:**
- PostgreSQL concurrent-insert race test
- SQL Server concurrent-insert race test (rowversion collision)
- Backend adapter invocation verification (DispatchJobWithAckAsync calls UploadAndStartPrintAsync)
- Outbox recovery/retry after crash scenario
- Full end-to-end bed-clear → backend-start → completion flow on multiple providers
- Reconciler probing backend authoritative state

**Fix Required:**
- Add `DbHeavy` tests for PostgreSQL and SQL Server using real or containerized instances.
- Add test verifying that DispatchJobWithAckAsync executes backend upload before returning.
- Add test simulating outbox publisher crash mid-upload, then recovery on restart.
- Coordinator feedback blocker #14 lists full matrix:
  > "migrated SQLite/PostgreSQL/SQL Server integration/race matrix; concurrent create winner/replay/mismatch; all-path bypass prevention; backend adapter invocation from bed-clear durable command..."

### SECONDARY ISSUES

#### 5. **Missing Unique-Index Race Handling** (Blocker #9)

**Status:** No evidence of a filtered unique index on `(IdempotencyScope, IdempotencyKey)` for the calibration queue creation endpoint. Coordinator feedback requires:

> "Derive classification/scope/provenance/hashes server-side from promoted G-code and authoritative rows; validate ownership/attempt/orchestration/snapshot/printer compatibility. Add SliceJobId and all immutable inputs. Canonical hash must include priority and material/nozzle/model/tool/capabilities."

**Fix Required:**
- Apply database-level unique index with filter for calibration jobs.
- Server-derive all immutable inputs on creation.
- Catch unique-index violation and reread winner, compare hash, return replay or mismatch.

#### 6. **Reconciliation Query Not Authoritative** (Blocker #6)

**Status:** No ReconciliationService found that queries backend state. Coordinator requires:

> "Timeout after send remains Starting with lease and reconciliation required; never release/retry or report success. Persist backend command/job IDs. Reconciler must query the authoritative backend and atomically resolve/advance/release the matching lease."

**Fix Required:**
- Implement reconciliation that queries printer backend (Moonraker, OctoPrint, etc.) for job state.
- Match backend job ID against persisted `QueueDispatchAttempt` and update outcome.
- Atomically release/advance lease on reconciliation.

#### 7. **Authorization/Audit SaveChanges Guard Missing** (Blocker #12)

**Status:** No immutability guard found on AppDbContext.SaveChanges for calibration/provenance/idempotency fields.

**Fix Required:**
- In AppDbContext.SaveChanges override, detect attempts to modify immutable PrintJob fields (JobKind, RequiredFirmwareFamily, etc.) and throw.

#### 8. **Start Path Audit Incomplete**

**Status:** The following paths must be verified to call the shared claim service:
- `SlicePrintBridgeController` (if it exists)
- `PrintersService.UploadAndStartPrintAsync` (manual start)
- `JobSchedulingService` (automatic scheduler)
- Desktop UI start paths
- Generic status transitions (PUT /api/job-queue/{jobId})

**Fix Required:**
- Grep for all callers of job Status transitions to Starting/Printing.
- Verify none bypass `AcquireClaimAsync` or `DispatchJobWithAckAsync`.
- Add tests for each path.

#### 9. **PostgreSQL Fencing Not Verified at Runtime** (Blocker #8)

**Status:** AppDbContext line 677 applies `ValueGeneratedNever()`, but:
- No runtime test confirms PostgreSQL generates and compares non-null tokens.
- No proof that migrated databases have non-null tokens populated.

**Fix Required:**
- Add test that inserts two concurrent contexts on PostgreSQL and verifies concurrency exception.
- Verify migration populates non-null tokens for existing rows.

#### 10. **Outbox Deduplication on Crash** (Blocker #10)

**Status:** Publisher marks event Published before task completes. On crash/restart, same event ID will be rediscovered but already marked Published, so it will be skipped.

**Fix Required:**
- Track "last successfully processed event ID" in a separate durable record (or use event ID as idempotency key).
- Ensure idempotent re-execution: calling `DispatchJobWithAckAsync` on an already-Starting job is safe (line 1067–1072), but file upload may fail silently if the job is already Printing.

## Quality Gate

- **Build:** ✓ PASS (`dotnet build ... 0 Errors`)
- **Tests:** ✓ PASS (10 DbHeavy tests pass)
- **Format:** NOT CHECKED (run `dotnet format ... --verify-no-changes`)
- **Full CI/Migration:** NOT CHECKED (run provider migration/compose validation)

## Summary

The Builder has wired the bed-clear acknowledgement through a durable outbox and implemented a shared dispatch claim service. However, the **critical durability gap is that the background task executing the backend start is fire-and-forget with no crash recovery path**. Additionally, **UTC.Ticks sequence is not collision-free**, **tests are SQLite-only**, and **reconciliation/authorization/audit guards are incomplete**.

These are production-safety issues that must be fixed before the PR can merge. The implementation must ensure:
1. Durable backend-start execution with crash recovery.
2. Collision-free, monotonic outbox sequence.
3. Full test coverage across all database providers.
4. Complete reconciliation and authorization checks.

## What Must Be Fixed (FAIL only)

1. **Outbox durability**: Mark BackendStartCommand Published only AFTER backend execution completes (or make execution synchronous).
2. **Sequence monotonicity**: Configure database-generated sequences (SQLite AUTOINCREMENT, PostgreSQL SERIAL, SQL Server IDENTITY).
3. **Test matrix**: Add PostgreSQL and SQL Server DbHeavy tests for concurrent creates, backend adapter invocation, and outbox crash recovery.
4. **Reconciliation**: Implement query of authoritative backend state and atomic lease resolution.
5. **Start path audit**: Verify all production paths route through shared claim service.
6. **Authorization immutability**: Add SaveChanges guard to prevent mutation of immutable fields.
