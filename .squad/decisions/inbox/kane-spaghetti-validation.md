---
decision_type: validation_plan
status: proposed
date: 2026-03-18
author: kane
---

# Spaghetti Detection — Validation Plan (First Slice)

## Context

Backend: `PrintFailureMonitorService` actively polls printers, analyzes snapshots via Obico, broadcasts `FailureDetected` events via SignalR.  
Frontend: Shows "ML" badge when `obicoEnabled && isPrinting`, displays toast notifications on failure events.  
Gap: No end-to-end validation that the **full detection loop** works trustworthy for users.

**Goal:** Prove the user-visible failure detection loop is reliable. Don't attempt comprehensive future-state coverage — gate the first slice.

---

## Quality Gates (User-Visible Trustworthiness)

### Backend Integration Tests (Priority 1)

**File:** `src/tests/Farm.Web.Api.Tests/Services/PrintFailureMonitorServiceTests.cs` (NEW)

**Must verify:**
1. **Printer eligibility** — Service only analyzes printers that are:
   - Online
   - State == "Printing"
   - Have at least one enabled camera with non-empty `SnapshotUrl`
   - Have `ObicoServerId` assigned OR fallback to global Obico URL

2. **Obico server selection** — Service correctly picks:
   - Printer's assigned `ObicoServer` if present
   - Falls back to global `ObicoSettings.ObicoApiUrl` if no assignment
   - Logs which server is used (validate log output)

3. **SignalR broadcast** — When failure detected:
   - `FailureDetectionDto` published to `PrinterHub.Clients.All.SendAsync("FailureDetected")`
   - DTO contains: `printerId`, `printerName`, `jobId`, `confidence`, `detectedAt`, `autoPaused`
   - `jobId` is correct (matches active print job from DB)

4. **Disabled monitoring** — When `ObicoSettings.Enabled == false`:
   - Service sleeps and does NOT analyze printers

5. **Monitoring interval** — Cycles respect `ObicoSettings.ScanIntervalSeconds`

**Edge cases:**
- Printer with camera but offline → skipped
- Printer with camera but idle → skipped
- Printer printing but no camera → skipped
- Multiple printers printing simultaneously → all analyzed (concurrency)
- Obico API returns error → service logs warning, continues to next printer

**Testing strategy:**
- Use `CustomWebApplicationFactory` with in-memory SQLite
- Seed test printers with cameras, Obico assignments, and active print jobs
- Mock `IObicoFailureDetectionService.AnalyzeImageFromUrlAsync` to control failure detection results
- Mock `IHubContext<PrinterHub>` to capture SignalR broadcasts
- Mock `IPrinterStatusCacheReader` to control which printers appear as "Printing"
- Use `IHostedService` test harness to trigger `ExecuteAsync` directly (no 30s delay)

**Coverage target:** All decision branches in `PrintFailureMonitorService.RunMonitoringCycleAsync`

---

### Frontend Component Tests (Priority 2)

**File:** `src/Web/ReactApp/src/test/features/printers/failure-detection-toast.test.tsx` (NEW)

**Must verify:**
1. **Toast display** — When `printerSignalRService.onFailureDetected` fires:
   - Toast shows: `⚠️ Failure detected on [printerName] (confidence: [N]%)`
   - Auto-pause message appended if `autoPaused == true`
   - Toast duration: 8000ms
   - Toast variant: `warning`

2. **Toast content accuracy** — Confidence rounded to integer (e.g., `85.5` → `85`)

3. **Multiple events** — Multiple failure events show multiple toasts (not replaced)

**Testing strategy:**
- Mock `printerSignalRService` with controllable callback trigger
- Render `<App />` (or extract failure handler to testable hook)
- Simulate SignalR event via mock callback
- Assert toast appearance with `screen.getByText` and/or `toast.warning` mock

**Coverage target:** Full `onFailureDetected` callback in `App.tsx`

---

### Frontend Integration Tests (Priority 3)

**File:** `src/Web/ReactApp/src/test/features/printers/ml-badge-integration.test.tsx` (NEW or EXPAND existing obico-ml-badge.test.tsx)

**Must verify:**
1. **ML badge visibility rules** — Badge shows when:
   - `printer.obicoEnabled == true`
   - `printer.state == "Printing"`
   - Badge hidden otherwise (all permutations tested in existing `obico-ml-badge.test.tsx`)

2. **EditPrinterModal toggle** — When user toggles "Enable Obico monitoring":
   - Save button becomes enabled
   - Saving sends `obicoEnabled: true` to API
   - (Already covered by existing `EditPrinterModal.test.tsx`)

**Testing strategy:**
- Existing tests cover this. Validate they still pass after backend changes.
- If backend adds new fields to `FailureDetectionDto`, update type tests.

---

## Edge Cases to Gate (Critical Path)

### Backend Edge Cases
1. **No printers configured** → Service sleeps, no crashes
2. **No cameras configured** → Service finds 0 eligible printers, sleeps
3. **All printers offline** → Service finds 0 eligible printers, sleeps
4. **Obico server down** → Service logs error, continues monitoring other printers
5. **Database connection lost mid-cycle** → Service logs error, retries next cycle
6. **Printer goes offline during analysis** → Service handles exception, continues

### Frontend Edge Cases
1. **SignalR disconnected** → No toasts (expected, not a test failure)
2. **Malformed event** → Toast shows with default values or gracefully skips
3. **User dismisses toast** → No state corruption

### Integration Edge Cases
1. **Printer deleted during monitoring** → Service skips missing printer, no crash
2. **Camera URL becomes invalid** → Analysis fails gracefully, logged warning
3. **Print job completes during analysis** → Event still broadcasts (stale but harmless)

---

## Deferred (Not First Slice)

These are **out of scope** for the first validation slice:

- **History persistence** — `GET /api/failure-detection/history` returns 501. Future work.
- **Auto-pause implementation** — `PrintFailureMonitorService` logs "pause requires backend client integration." Future work.
- **Manual analyze endpoint** — `POST /api/failure-detection/analyze/{printerId}` requires auth and Obico integration. Low priority.
- **Confidence threshold tuning** — No user-configurable threshold yet. Future work.
- **Multi-camera printers** — Service uses `FirstOrDefault()` camera. Future work.
- **Rate limiting** — No protection against Obico API rate limits. Future work.
- **Performance under load** — No stress test for 50+ printers. Future work.

---

## Test Execution Order

1. **Backend integration tests** — Prove monitoring loop correctness
2. **Frontend component tests** — Prove toast notifications work
3. **Manual smoke test** — Developer runs both API + React, triggers failure, sees toast
4. **Full test suite** — All 1480 React + 1645 API tests must still pass

---

## Success Criteria

✅ All backend integration tests pass (new `PrintFailureMonitorServiceTests.cs`)  
✅ All frontend component tests pass (new `failure-detection-toast.test.tsx`)  
✅ Existing test suites pass with 0 regressions  
✅ Developer smoke test confirms end-to-end flow works  
✅ No new linting errors, no new compiler warnings

**If ANY criteria fail, the slice is NOT ready for merge.**

---

## Test Scaffolding Recommendations

### Backend Test Structure (Minimal Scaffold)

```csharp
// src/tests/Farm.Web.Api.Tests/Services/PrintFailureMonitorServiceTests.cs
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class PrintFailureMonitorServiceTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly Mock<IObicoFailureDetectionService> _mockObicoService;
    private readonly Mock<IHubContext<PrinterHub>> _mockHub;
    private readonly Mock<IPrinterStatusCacheReader> _mockStatusCache;

    // Test methods:
    // - Service_OnlyAnalyzesPrintersWithCamerasAndPrintingState
    // - Service_UsesAssignedObicoServerWhenAvailable
    // - Service_FallsBackToGlobalObicoUrlWhenNoAssignment
    // - Service_BroadcastsFailureEventWhenDetected
    // - Service_SkipsAnalysisWhenObicoDisabled
    // - Service_HandlesObicoApiErrorGracefully
}
```

### Frontend Test Structure (Minimal Scaffold)

```typescript
// src/Web/ReactApp/src/test/features/printers/failure-detection-toast.test.tsx
describe('Failure Detection Toast Notifications', () => {
  it('displays toast when FailureDetected event received');
  it('shows confidence as integer percentage');
  it('appends auto-pause message when autoPaused is true');
  it('shows multiple toasts for multiple events');
});
```

---

## Rationale

This plan prioritizes **user-visible correctness** over exhaustive internal unit tests. The critical path is:

1. Backend monitors printers correctly
2. Backend broadcasts events correctly
3. Frontend displays toasts correctly

Once this loop works, future slices can add history persistence, auto-pause, and advanced features.

**No implementation yet** — this is the validation plan. Implementation happens in the next phase.
