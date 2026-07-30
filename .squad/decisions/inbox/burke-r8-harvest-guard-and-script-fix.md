# Decision: #715 r8 — Harvest reserved-prefix guard + migration script/collation deploy-safety

**By:** Burke (r8 remediation author)
**Date:** 2026-07-13
**Issue:** #715 (F10 offline tolerance / write-queue persistent Idempotency-Key)
**Cycle:** r8 (resolves r7's 2× REQUEST_CHANGES)

## What was decided/done

1. **B1/H3 — Harvest endpoint now rejects client-supplied reserved OperationKeys.** Added `[ReservedOperationKeyPrefix]` to `HarvestJobRequest.OperationKey` (DTO boundary) + defense-in-depth service guard in `PartHarvestService.HarvestJobAsync` (returns 400 InvalidRequest). Closes a cross-job harvest-key poisoning exploit (client writes `harvest:{otherJob:N}` verbatim → later autogeneration collides on the unique filtered index → victim job's harvest permanently broken). Server-side autogeneration path (`PartHarvestService.cs:181`) intentionally left unguarded — it bypasses DTO/service and must be able to emit `harvest:` keys.

2. **H1a — SQL Server migration scripts are now single-batch safe.** `OnlineAwareCreateUniqueIndex` emits unique per-index variable names (`@online_{ix}`, `@sql_{ix}`) instead of repeated `DECLARE @online`/`@sql`, fixing Msg 134 ("variable already declared") under script-based deploys (SQLCMD/sqlpackage) where all `migrationBuilder.Sql()` calls collapse into one GO batch.

3. **H1b — Down migrations are now catalog-collation-agnostic.** Replaced hardcoded `SQL_Latin1_General_CP1_CI_AS` reverts with dynamic SQL reading `DATABASEPROPERTYEX(DB_NAME(),'Collation')` (helper `RevertCollationToCatalogDefault`, uniquely-named `@coll_{table}_{column}` vars). Applies to 4 columns in `20260713235657` Down() + 3 columns in `20260713163813` Down(). Collation is captured at rollback time, not authoring time.

## Notable deviations (for reviewers)

- Task's recommended H1a/H1b "Option A" (inline `EXEC('...' + CASE/DATABASEPROPERTYEX + '...')`) is **invalid T-SQL** — `EXEC()` string concat rejects CASE/function operands. Used variable-based dynamic SQL with collision-free unique names (task's Option C style) instead.
- `dotnet ef migrations script` requires FULL migration names (bare IDs fail).
- Skipped the optional controller-level attribute test (no easy harness — `JobQueueControllerTests` mocks the service and bypasses model binding). Coverage mirrors the adjust DTO: service-guard tests + existing `ReservedOperationKeyPrefixAttributeTests` unit tests.

## Validation

- format (scoped) clean; build 0W/0E (warnings-as-errors); focused tests ×3 → 219 pass, deterministic.
- has-pending-model-changes clean on BOTH providers (sqlserver + postgres).
- Script-generation proof: UP + DOWN scripts have **zero** duplicate DECLARE in any GO batch; DOWN has 7 dynamic `DATABASEPROPERTYEX` collation reverts, **zero** hardcoded CI_AS.
- Full suite: only the 4 pre-approved failures (3× OrcaSlicerAssetRegistry CRLF, 1× FilamentCoverage perf-budget). No regressions in idempotency/parts areas.

## Preserved (untouched)

Frost r6 BIN2 Up(); Newt r7 WITH NOCHECK + EngineEdition ONLINE detection; Apone r5 NFKC; Ripley r4 attribute mechanism; Hudson r3 reclaim TOCTOU; Kane r1 IdempotencyRecords BIN2. No feature-flag / route / naturally-idempotent behavior changes.
