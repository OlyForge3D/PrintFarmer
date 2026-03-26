---
name: "modal-http-error-regression"
description: "How to reproduce a UI modal API error through the real query hook and pair it with the backend HTTP contract seam"
domain: "testing"
confidence: "high"
source: "earned"
---

## Context

Use this when a React modal shows a raw backend/API error string and you need high-signal proof of whether the bug lives in the modal path itself or in a downstream HTTP contract.

## Patterns

### Reproduce the exact modal path, not just the presentational component

- Build a small test harness that uses the real query hook feeding the modal trigger component.
- Mock the API client method the hook calls and return the exact status payload that the UI receives in production.
- Open the modal from the trigger and assert the raw error string is visible in the same places the user sees it.

### Pair the UI symptom with the outbound backend contract

- Add a backend test that records the full request sequence: method, path, query, and fallback behavior.
- Make the backend test fail on the old or suspected-bad route so the source of the user-visible message is obvious.
- If the UI route itself is already correct, say so explicitly and reject fixes aimed at that layer.

### Keep the slices narrow

- One frontend test should prove the user-facing modal symptom.
- One backend test should prove the HTTP contract or fallback sequence that produced it.
- Avoid broad page or end-to-end coverage until the exact seam is pinned down.

## Examples

- `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringOverlay.test.tsx`
  - uses `usePrinterFailureDetectionStatus`
  - mocks `apiClient.getFailureDetectionStatus()`
  - opens the spaghetti-detection modal and verifies `API error: HTTP 405`
- `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs`
  - proves `GET /p/?img=...` falls back to `POST /p/`
  - reproduces the final 405 when both contracts return `MethodNotAllowed`

## Anti-Patterns

- Testing only the modal component with injected props and assuming that reproduces the live query path.
- Blaming the UI route before checking the downstream service contract that generated the error string.
- Writing a broad integration test before recording the exact method/path sequence that fails.
