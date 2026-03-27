---
name: "ready-gate-dispatch-alignment"
description: "Keep auto-print ready-gate logic aligned with auto-dispatch eligibility when queues can hold unassigned jobs."
domain: "backend-logic"
confidence: "high"
source: "earned"
---

## Context
Use this when a backend has both a confirmation gate (like PendingReady / bed-clear) and an automatic dispatcher that can pull from a shared queue. These systems often regress when one path still assumes jobs are pre-assigned to a specific worker or printer.

## Patterns
- Treat explicitly assigned queued jobs as highest priority.
- For unassigned queued jobs, reuse the same scorer / eligibility engine the dispatcher uses.
- Apply the dispatcher threshold settings when deciding whether a gate should surface.
- Build gate status DTOs from dispatch-eligible jobs, not from raw queue rows that the dispatcher would never pick.
- If a status endpoint exposes queue depth or next-job previews, make those values reflect what the dispatcher would actually do.

## Examples
- `src/infra/Services/AutoPrint/AutoPrintService.cs` now uses `IDispatchScorer` and `DispatchSettings.MinimumScoreThreshold` to decide whether shared queued jobs should put a printer into `PendingReady`.
- `src/tests/Farm.Web.Api.Tests/Services/AutoPrint/AutoPrintServiceTests.cs` covers the regression with a compatible unassigned queued job.

## Anti-Patterns
- Checking only `AssignedPrinterId == printerId` when the dispatcher also consumes unassigned jobs.
- Showing a confirmation gate for jobs the dispatcher would eliminate anyway.
- Returning queue depth / next-job hints from one eligibility model while dispatch uses another.
