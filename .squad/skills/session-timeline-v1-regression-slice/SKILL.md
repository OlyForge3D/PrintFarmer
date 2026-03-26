---
name: "session-timeline-v1-regression-slice"
description: "Focused QA gate for PrintFarmer's minimal print-session timeline work"
domain: "testing"
confidence: "high"
source: "earned"
---

## Context

Use this when PrintFarmer is stitching together a minimal print-session timeline from existing persisted sources instead of building a general audit/event platform.

## Patterns

- Keep the backend gate centered on the composition seam:
  1. service test for merged event ordering and session association
  2. controller/integration test for the timeline endpoint contract
- Prefer the printer-scoped contract that actually shipped: `GET /api/printers/{printerId}/session-timeline?take=N`, returning recent sessions with nested mixed events rather than a flat synthetic stream the frontend must regroup.
- Include an orphan-incident boundary test: persisted `FailureDetectionIncident` rows with `JobId = null` should still attach when their timestamp falls inside the printer session window.
- Keep the frontend gate centered on operator-visible rendering:
  1. chronological mixed rows (state changes + failure incidents)
  2. auto-pause companion row when a failure incident paused the job
  3. loading/error/empty states for the timeline panel
- Reuse the failure-incident persistence triad as a dependency gate. If failure incidents stop being stored or lose job context, the session timeline becomes untrustworthy even if the timeline service still returns data.
- Treat backend/frontend contract mismatches as first-class regression seams. If backend composes sessions in one endpoint but frontend still assembles the view from older endpoints, QA should call that out explicitly.

## Minimum Test Slice

### Backend

- `PrinterSessionTimelineServiceTests`
  - event composition and stable ordering
  - orphan incident attachment by session window
  - newest-session-first behavior and take limiting
- `PrinterSessionTimelineControllerTests`
  - success path
  - 404 for missing printer or missing target resource

### Frontend

- `PrintSessionTimeline.test.tsx`
  - mixed timeline row rendering in chronological order
  - incident confidence + snapshot affordance
  - loading and error states

### Dependency Gate

- `FailureDetectionIncidentHistoryServiceTests`
- `FailureDetectionControllerTests`
- `PrintFailureMonitorPersistenceTests`

## Anti-Patterns

- Testing only the new timeline endpoint without proving failure incidents still enter it correctly
- Treating printer-level recent history as equivalent to a single print session without a boundary test
- Writing only snapshot tests for the UI; prefer assertions on row order, labels, and links
- Ignoring frontend/backend contract drift because both sides "sort of" show timeline data
