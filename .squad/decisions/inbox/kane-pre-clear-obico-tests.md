# Kane — Pre-Clear & Obico ML Badge Test Report

**Date:** 2026-03-17
**Author:** Kane (Tester)
**Status:** FINDINGS

## Bug Found: AutoPrintController 404 vs 400 Mismatch

**File:** `src/api/Controllers/AutoPrintController.cs` line 106-122

The `MarkPreClearAsync` endpoint declares `[ProducesResponseType(StatusCodes.Status404NotFound)]` in its OpenAPI attributes, but the catch block only returns `BadRequest(400)` for all `InvalidOperationException` errors — including when the printer is not found.

**Impact:** API consumers (including the React client) may expect 404 for missing printers but will always receive 400. Swagger/OpenAPI documentation is misleading.

**Recommendation:** Either:
1. Differentiate exceptions: throw a `KeyNotFoundException` for missing printers and catch it separately to return 404, OR
2. Remove the `ProducesResponseType(Status404NotFound)` attribute if 400 is the intended behavior.

This pattern also exists in the `SetEnabledAsync` endpoint (line 68-78) which has the same mismatch.

## Decision: Test File Placement

Placed API pre-clear tests in `Controllers/AutoPrintPreClearTests.cs` (not the `Dispatch/` folder) because these test the HTTP endpoint behavior, not the background service dispatch logic. The existing `Dispatch/AutoDispatchBackgroundServiceTests.cs` tests the internal channel/trigger mechanism.
