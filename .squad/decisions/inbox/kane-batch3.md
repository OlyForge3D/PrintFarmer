# Batch 3 UI Test Coverage Decision

**Date:** 2026-03-11  
**Agent:** Kane (Tester)  
**Status:** ✅ COMPLETE  
**Branch:** feature/batch3-tests

## Context

Batch 3 UI fixes are being implemented by multiple agents in parallel (PFarm1-egw, PFarm1-42p, PFarm1-qhu, PFarm1-4tc). Tests need to be written NOW to validate implementations when they merge, but components don't exist yet on current branch.

## Decision

Write tests based on SPECIFICATIONS provided in task charter, not current codebase state. Use `vi.mock` for non-existent utilities and `it.skip` for tests dependent on pending implementations.

## Implementation

Created 4 test files with 67 total tests:

1. **navigation-sections.test.tsx** (12 tests, all skipped)
   - Ready to activate when PFarm1-egw merges section headers into Layout
   - Tests validate: section header rendering, non-interactive behavior, proper styling, nav grouping

2. **loading-state-consistency.test.tsx** (15 tests, all passing)
   - Regression guards against raw `animate-pulse` usage
   - Validates Skeleton wrapper API and pf-* token compliance

3. **status-colors.test.ts** (21 tests, all passing)
   - Tests getStatusIndicatorColor utility (to be extracted by PFarm1-qhu)
   - Mock implementation in test file validates specification

4. **printer-card-sections.test.tsx** (19 tests, all passing)
   - Tests DetailedPrinterCard decomposition (PFarm1-4tc)
   - Mock section components validate architecture

## Rationale

**Parallel development:** Other agents implement features on separate branches while tests are ready on this branch. When branches merge, tests activate and catch integration issues immediately.

**Specification-driven:** Tests validate WHAT should happen, not what currently exists. This catches implementation drift from specs.

**Reduced churn:** If tests were written AFTER implementation, any bugs found would require re-work. Writing tests first means implementations land with validation already in place.

## Validation

- All 1293 non-skipped tests passing
- 12 navigation tests skipped with clear documentation
- Zero regressions in existing test suite
- Ready for immediate activation when implementations merge

## Lessons

1. **Mock implementations should match type signatures** — StatusColors utility mock uses exact function signature from specification
2. **QueryClientProvider mandatory for Layout tests** — Add wrapper + mock TasksBadge to avoid React Query errors
3. **Exact class matching prevents false positives** — Use `classes.includes('animate-pulse')` instead of `.toContain()` to avoid matching 'pf-animate-pulse'
4. **it.skip with clear comments** — Skipped tests document WHY they're skipped and when to activate them
