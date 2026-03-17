# Hardcoded API Paths Outside api.ts

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-16  
**Status:** FOR DISCUSSION

## Problem

Found hardcoded API paths in `useAutoDispatch.ts` that bypass the centralized `apiClient` methods in `api.ts`. These call `apiClient.get/post/put` directly with string paths instead of using the typed methods.

## Affected Files

- `src/features/printers/hooks/useAutoDispatch.ts` — 7 direct `apiClient.get/post/put` calls with `/auto-print/` paths
- `src/features/printers/__tests__/BedClearBanner.test.tsx` — 3 test assertions checking those paths

## Impact

When backend routes change (like this kebab-case migration), these hardcoded paths silently break unless someone greps the entire codebase. The centralized `api.ts` methods exist for exactly this reason.

## Recommendation

Refactor `useAutoDispatch.ts` to use the `apiClient.getAutoDispatchStatus()`, `apiClient.markPrinterReady()`, etc. methods already defined in `api.ts` instead of raw path calls.
