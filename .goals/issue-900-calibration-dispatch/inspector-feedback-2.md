# Inspector Feedback — Iteration 2

## Verdict: FAIL

Builder iteration 2 added partial production wiring but fails critical acceptance criteria:

1. **Provider-specific concurrency fencing untested** — Tests use in-memory database only
2. **Incomplete start path coverage** — Only 2 calls to DispatchClaimService found; many paths missing
3. **SQLite row version fencing broken** — EF Core .IsRowVersion() generates NULL tokens for SQLite
4. **Outbox publisher unverified** — QueueOutboxPublisherService registered but not tested as running
5. **Race condition tests absent** — No concurrent insert tests on real providers

## Critical Issues

### Gap 1: Canonical calibration creation/idempotency - ✅ Partially (tests use InMemoryDatabase)

### Gap 2: All production start paths - ❌ INCOMPLETE
- Only 2 calls found: PrintJobManagementService.DispatchJobAsync (line 789), BedClearAcknowledgementService (line 171)
- Missing: Auto dispatch, batch dispatch, scheduler, rerun paths not verified
- Fallback in DispatchJobAsync (line 803) bypasses claim entirely

### Gap 3: Claim safety/fencing/lifecycle - ⚠️ INCOMPLETE
- Missing: Telemetry freshness, compatibility tuple validation, hard filament policy, Klipper firmware/dialect/slicer checks
- ✓ Row version configured but SQLite doesn't generate non-null tokens

### Gap 4: Bed-clear endpoint - ❌ NOT ATOMIC
- AcknowledgeAsync persists (line 165), THEN calls AcquireClaimAsync (line 171)
- Separate transactions - crash between them leaves acknowledged job unclaimed

### Gap 5: Provider fencing - ❌ BROKEN FOR SQLite
- SQLite row versions remain NULL, permitting multiple concurrent winners
- Tests use InMemoryDatabase - never tested on real SQLite/PostgreSQL/SQL Server

### Gap 6: Backend failures - ⚠️ INCOMPLETE
- Reconciliation service completely absent
- Unknown outcomes marked RequiresReconciliation but no reconciliation loop

### Gap 7: Outbox - ❌ INERT
- QueueOutboxPublisherService registered but NOT tested as running
- No tests verify events transition from Pending to Published
- No SignalR publishing shown

### Gap 8: Priority/scheduler - ⚠️ INCOMPLETE
- No validation of priority values
- Scheduler not implemented

### Gap 9: ETag/concurrency - ✓ BASIC (partial If-Match enforcement)

### Gap 10: Events/audit - ❌ ABSENT
- QueueEventEnvelope exists but no publishing code
- No SignalR hub, no group scoping, no audit

### Gap 11: Migrations/backfill - ⚠️ SQLite fencing broken (Gap 5)

### Gap 12: Integration/race/crash tests - ❌ ABSENT
- Tests use InMemoryDatabase, not real providers
- No concurrent insert tests across separate contexts
- No two-process, two-job/one-printer races

## Quality Gate Result: ✅ PASS (588 tests)
**CAVEAT:** All tests use InMemoryDatabase, mocks, or unit fixtures - NOT production code on real providers

## What Must Be Fixed (10 Gaps)

1. Implement provider-specific row version fencing (SQLite AUTOINCREMENT, real DbHeavy tests)
2. Make bed-clear acknowledgement + claim atomic
3. Wire all start paths through claim (auto, batch, rerun); remove fallback
4. Verify outbox publisher runs and publishes
5. Implement authenticated SignalR event publishing
6. Add printer telemetry freshness validation to claim
7. Add compatibility tuple/digest validation to claim
8. Add filament policy enforcement to claim
9. Implement reconciliation service
10. Add provider-specific DbHeavy integration tests (real SQLite/PostgreSQL/SQL Server with concurrent inserts)

## Files to Fix

- DispatchClaimService.cs - add validations, remove optional behavior
- BedClearAcknowledgementService.cs - make atomic with claim
- PrintJobManagementService.cs - remove fallback, require claim
- AutoDispatchBackgroundService.cs - add claim acquisition
- BatchDispatchService.cs - add claim acquisition
- QueueOutboxPublisherService.cs - add tests for running/publishing
- AppDbContext.cs - provider-specific row version handling
- New: QueueReconciliationService.cs
- New: QueueEventsHub.cs
- New: CalibrationQueueConcurrencyTests.cs (DbHeavy)
