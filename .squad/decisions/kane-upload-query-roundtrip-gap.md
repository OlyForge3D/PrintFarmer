# Decision: Upload→Query Round-Trip Regression Test Gap

**Author:** Kane (QA)  
**Date:** 2025-07-25  
**Status:** Implemented (new test) + Open (broken existing tests)

## Context

User reported "files not displayed after upload success toasts." Investigation found:

1. **The query key fix is already in the repo** — `ModelUploadModal.tsx` correctly invalidates `['file-browser']`
2. **Most likely stale deployment** — user may be running an older build
3. **Critical test gap**: No test covered the HTTP round-trip (upload → query → verify model appears)
4. **6 existing backend regression tests are broken** by implementation drift (mock sequences no longer match)
5. **2 existing frontend tests are broken** by component timing changes

## Decision

- **Added** `Model3DUploadQueryRoundTripTests.cs` — 3 HTTP integration tests covering the exact user failure path
- **Deferred** fixing the 6 broken backend tests (separate scope — mock sequences need updating to match current service code)
- **Deferred** fixing the 2 broken frontend tests (timing assertions need re-calibration)

## Action Items

- [ ] Fix `Model3DUploadCompletionRegressionTests` — update mock operation sequence counts
- [ ] Fix `Model3DFileDownloadRegressionTests` — use factory temp dirs instead of hardcoded paths
- [ ] Fix `ModelUploadModal.test.tsx` — timing assertion for toast and button state tests
- [ ] Investigate frontend `mapQueryParams` field name mismatch (search/sort silently ignored)
- [ ] Verify user's deployment is current (redeploy if stale)
