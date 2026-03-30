---
name: "failure-detection-context-enrichment"
description: "Enrich PrintFarmer-owned failure-detection payloads with job/file context from existing runtime truth sources instead of adding new persistence or syncing state into Obico."
domain: "backend-api"
confidence: "high"
source: "earned"
---

## Context
Use this when the frontend needs better operator-facing copy for failure-detection badges, modals, or SignalR alerts, but PrintFarmer must remain the source of truth for printer/job/session UX.

## Patterns
- Keep Obico limited to ML/failure detection; do not mirror full printer/job/session state into Obico just to fill UI copy gaps.
- Prefer enriching PrintFarmer API/SignalR payloads with small optional fields (`jobName`, `fileName`) derived from existing runtime sources.
- Read live print context from `IPrinterStatusCacheReader` first because it reflects the backend-reported active file path.
- Fall back to the active PrintFarmer `PrintJob` record when cache data is missing so alerts still have a usable display label.
- Make enrichment fields optional so older frontend surfaces remain compatible.

## Examples
- `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs`
- `src/infra/Services/FailureDetection/FailureDetectionMonitorStatus.cs`
- `src/infra/Dtos/FailureDetectionDto.cs`
- `src/Web/ReactApp/src/types/api.ts`

## Anti-Patterns
- Adding schema churn or new persistence just to show the current file name in a failure alert.
- Teaching the frontend to infer failure-detection job context from Obico.
- Emitting required fields for live context that may be absent during startup or stale-cache situations.
