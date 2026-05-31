# Decision: Fix spurious LoginAuditEntries migration drift in PRs #381 and #390

**Author:** Ripley (Frontend, acting on backend per lockout rule — Lambert locked out)  
**Date:** 2026-05-31  
**PRs:** #381 (squad/346-power-monitor-entities), #390 (squad/344-printjob-cost-aggregation)  
**Status:** Applied

## Context

Bishop flagged both PRs in cycle 2 review: their SqlServer migrations included an unrelated `AlterColumn` on `LoginAuditEntries.Timestamp` (DateTimeOffset → DateTime). This change is technically correct (matches the entity) but should NOT be piggybacked onto unrelated feature migrations.

## Root Cause

The `AddLoginAuditLog` SqlServer migration (20260526173129) was generated with a stale snapshot that recorded `LoginAuditEntries.Timestamp` as `DateTimeOffset`, while the entity model uses `DateTime`. The Designer.cs for that migration captured the wrong type. When subsequent branches added migrations, EF detected the drift and injected a corrective `AlterColumn`.

PostgreSQL was unaffected because `timestamp with time zone` handles both `DateTime` and `DateTimeOffset` transparently.

## Fix Applied

1. Merged `origin/development` into both branches to sync latest code
2. Deleted the offending migration files (both providers)
3. Restored model snapshots from development
4. Corrected `AddLoginAuditLog.Designer.cs` on SqlServer to reflect `DateTime`/`datetime2`
5. Regenerated migrations via `dotnet ef migrations add`
6. Surgically removed any remaining `AlterColumn` on `LoginAuditEntries` (EF still generates it because the previous migration's actual `.cs` creates the column as datetimeoffset — a known EF limitation)
7. Verified `Up()`/`Down()` only touch intended tables

## Timestamp Coordination

| PR | PostgreSQL | SqlServer |
|----|-----------|-----------|
| #381 PowerMonitor | 20260531200723 | 20260531201002 |
| #390 KwhUsed | 20260531201819 | 20260531201932 |

#381 lands first chronologically — required because KwhUsed cost hook references PowerMonitor entities.

## Outstanding

The `LoginAuditEntries.Timestamp` column type mismatch (datetimeoffset in DB vs datetime2 in model) on SqlServer still exists in deployed databases. A dedicated migration should be filed to correct this cleanly on its own, not as a side-effect of feature work.

## Health Report

| Check | PR #381 | PR #390 |
|-------|---------|---------|
| Build (0 errors) | ✅ | ✅ |
| No LoginAuditEntries refs in migration | ✅ | ✅ |
| Migration only touches intended tables | ✅ | ✅ |
| Timestamps ordered correctly | ✅ | ✅ |
| Pushed to origin | ✅ | ✅ |
| PR commented | ✅ | ✅ |
