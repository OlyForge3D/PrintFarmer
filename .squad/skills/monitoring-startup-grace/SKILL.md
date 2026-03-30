---
name: "monitoring-startup-grace"
description: "Suppress backend attention states during startup and warmup windows until monitoring is actually expected to be active."
domain: "backend-state"
confidence: "high"
source: "earned"
---

## Context
Use this when a monitoring subsystem keys off a coarse runtime state like `Printing`, but the real workflow has intermediate startup phases such as upload, heating, homing, or first-layer warmup.

## Patterns
- Combine live status-cache state with durable workflow state (for example, the active job row) before deciding monitoring eligibility.
- Treat `Starting` jobs as explicitly non-monitorable, even if normalized printer state already says `Printing`.
- Add a short grace period for freshly-started `Printing` jobs so transient warmup errors do not become operator-facing attention states.
- Keep manual or untracked prints eligible once the live printer state says `Printing` and no tracked warmup window applies.

## Examples
- `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs`
- `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/PrintFailureMonitorServiceTests.cs`

## Anti-Patterns
- Driving monitoring directly from a normalized `Printing` string without checking the underlying job lifecycle.
- Emitting red/error status the moment dispatch finishes, before the print has actually settled.
- Fixing the symptom only in UI display code while leaving backend lifecycle state inconsistent.
