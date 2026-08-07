# Copilot Processing

## Request

Implement issue #1160: add a real, authorization-safe printer summary projection for dashboard statistics and compatible alert consumers, migrate compatible frontend consumers to one shared summary query/cache, add focused tests, validate, commit, push, pass Bishop/Hicks/Vasquez consensus, open a non-draft PR with `Closes #1160`, and complete the merge/closure lifecycle.

## Action plan

- [x] Inspect printer API routes, authorization helpers, DTOs, frontend consumers, and focused tests.
- [x] Implement the additive projected summary endpoint without changing existing detailed printer contracts.
- [x] Add backend authorization, empty-result, status/count, and projection-focused tests.
- [x] Migrate compatible dashboard consumers to one summary query/cache and add frontend tests.
- [x] Run targeted backend/frontend validation and fix regressions.
- [ ] Review the diff, commit, and push the branch.
- [ ] Obtain mandatory Bishop/Hicks/Vasquez exact-head adversarial consensus.
- [ ] Open a non-draft PR targeting development with `Closes #1160`, verify issue linkage, and report lifecycle evidence.
- [ ] Follow CI, trusted verdict, merge, and issue-closure status through completion.

## Summary

Implemented the additive `/api/printers/summary` projected endpoint with admin-safe disabled-printer handling, cached live status merging, and minimal DTO fields. Migrated dashboard, alert, and catalog-update consumers to the shared React summary query/cache and updated focused tests. Targeted backend tests, React lint, React build, and focused React tests pass. Commit, push, adversarial review, PR, CI, trusted verdict, merge, and issue closure remain.

## Issue #871 Tracking

### Request

Perform an evidence-based cross-stack performance audit, rank ten concrete opportunities by impact, effort, risk, and measurability, implement only the best high-impact low-effort optimization, add focused regression coverage and a reproducible performance validation proxy, and complete the required delivery lifecycle.

### Action Plan

- [x] Audit backend, frontend, database, SignalR, slicer, and deployment performance hotspots.
- [x] Rank ten opportunities and select the best high-impact low-effort candidate.
- [x] Implement the focused optimization without broad refactoring.
- [x] Add focused regression coverage and a reproducible performance validation proxy.
- [x] Run targeted validation and fix regressions.
- [x] Commit and push with exact SHA tracking (initial head `467787169bb49183e5e3ae20c81de9cd58a4a80a`; follow-ups `25e4a3ef0c9dec7c9ccc9c36966a845f3150fe01`, `ecf0ed27d67c77f52d108d3957a0aafbafd029e1`, `d54d9a3a8721aa3232261bf1da9fd556ce3dc5c9`, and `080d6adc42dfbce33ab6cb83db66c5bc5f5b5586`; final follow-up pending commit).
- [ ] Obtain Bishop/Hicks/Vasquez exact-head consensus (initial review: Bishop APPROVE, Hicks CHANGES_REQUESTED, Vasquez REQUEST_CHANGES).
- [ ] Open a non-draft PR targeting development with `Closes #871` and verify linkage.
- [ ] Track CI, trusted verdict, safe merge, issue closure, archival, and report lifecycle state to Ralph.

### Audit Findings And Ranking

| Rank | Opportunity | Impact | Effort | Risk | Measurability |
|---:|---|---|---|---|---|
| 1 | Collapse statistics summary's seven sequential database commands into two aggregate reads | High | Low | Low | Exact EF command-count regression test |
| 2 | Batch per-job resource authorization in queue history instead of one call per returned job | High | Medium | Medium | SQL command count and endpoint timing |
| 3 | Add a shared query/cache for analytics dashboard requests that overlap across views | Medium | Low | Low | Browser request count and React Query cache assertions |
| 4 | Replace repeated daily chart row scans with keyed lookups | Medium | Low | Low | Benchmark synthetic 730-day result |
| 5 | Add bounded caching for rarely changing printer catalog metadata | Medium | Low | Medium | Cache-hit counter and endpoint latency |
| 6 | Batch maintenance dashboard's per-printer schedule lookups | High | Medium | Medium | SQL command count under fleet fixture |
| 7 | Add database indexes for common print-job date/status analytics filters | High | Medium | Medium | Query plan and seeded database timing |
| 8 | Coalesce duplicate SignalR status broadcasts per printer per short interval | Medium | Medium | Medium | Broadcast count under event burst |
| 9 | Lazy-load additional slicer profile schemas only after selection | Medium | Medium | Low | Network transfer and route chunk size |
| 10 | Stream large admin export responses instead of materializing full collections | Medium | High | Medium | Peak allocation and response timing |

Selected #1 because it removes five round trips from a frequently used analytics KPI endpoint with no contract, schema, or authorization change. The provider-neutral `TimeSpan` conversion remains a scalar stream in the second round trip; a numeric companion-column migration would be a separate higher-effort follow-up.

### Validation Evidence

- `dotnet test .\tests\Farm.Web.Api.Tests\Farm.Web.Api.Tests.csproj -c Debug --no-restore --filter 'FullyQualifiedName~StatisticsDateRangeTests|FullyQualifiedName~StatisticsServicePerformanceTests'`
- Result: 22 tests passed.
- Performance proxy: `StatisticsServicePerformanceTests.GetSummaryAsync_UsesTwoDatabaseCommandsAndPreservesAggregateMetrics` asserts all status/total fields, the empty-result path, and exactly two database commands across reader/scalar/non-query interception. Print-time values are streamed to avoid an unbounded list while retaining provider-safe `TimeSpan` handling; the second command remains row-oriented by design.
- Provider proxy: `SummaryAggregate_TranslatesAcrossSupportedProviders` compiles the aggregate query for SQLite, PostgreSQL, and SQL Server without opening a database connection.
- Review follow-up: expanded conditional aggregate and empty-result coverage, strengthened command interception, replaced print-time list materialization with scalar streaming, and changed the constant grouping key to a provider-safe column-bearing predicate in response to Hicks/Vasquez findings.
- Final review follow-up: changed the grouping expression to use the non-empty primary-key invariant (`Id != Guid.Empty`) so SQL Server receives a column-bearing `GROUP BY` expression, folded any defensive extra group in memory, and compiled both aggregate and ticks projections for all supported providers; focused tests: 22 passed; full solution build: 0 warnings, 0 errors.
- Final hardening: accumulated streamed print durations as `double` hours to avoid cumulative tick overflow, asserted date-range filtering for `TotalPrintHours`, and bounded single-sided date validation; focused tests: 25 passed; full solution build: 0 warnings, 0 errors.
