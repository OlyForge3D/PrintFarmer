---
name: "obico-contract-fallback"
description: "Adapting PrintFarmer backend code between upstream self-hosted Obico GET contracts and legacy multipart upload contracts"
domain: "backend-integrations"
confidence: "high"
source: "earned"
---

## Context
Use this when Obico/self-hosted ML integration work touches `ObicoFailureDetectionService` or `ObicoServerController`, especially when upstream `ml_api/server.py` behavior differs from older assumptions in PrintFarmer.

## Patterns

### Prefer the upstream snapshot URL contract
- Canonical self-hosted Obico flow is `GET /p/?img=<snapshot-url>`.
- The upstream response shape is `{"detections": [...]}`.
- For snapshot-based analysis, try this contract first before considering any local fetch/upload fallback.

### Parse both upstream and legacy confidence shapes
- Upstream `detections` may arrive as tuple-style arrays (for example `[label, confidence, bbox]`) or object-style items with a confidence property.
- Older PrintFarmer assumptions used `{"result":{"p":0.85}}`.
- Keep a parser that accepts both shapes so behavior stays backward compatible.

### Fall back only on clear contract mismatch
- If the upstream `GET /p/?img=...` call responds with `404`, `405`, or `415`, treat that as “this endpoint likely does not support the upstream GET contract” and retry the legacy multipart flow.
- Do **not** blanket-fallback on every upstream `400`.
- Do fall back when the `400` body specifically says the ML server could not fetch the supplied snapshot URL (for example `failed to fetch`, `could not download`, `no route to host`, `connection refused`, or timeout-style wording). PrintFarmer now centralizes that detection in `src/infra/Services/FailureDetection/ObicoSnapshotFallbackDetector.cs`.
- Do **not** automatically fall back on every failure. Auth, connectivity, and timeout failures should surface as real errors.
- When the upstream GET route responds with `400` because the ML server could not fetch the provided snapshot URL, align runtime and validation on the same recovery rule: local snapshot fetch plus legacy upload probe/upload where available.

### Keep validation aligned with runtime behavior
- `ObicoServerController` health/create/enable validation must probe the same contract the runtime service prefers.
- If runtime uses GET `/p/?img=...`, controller validation should probe the upstream JSON shape first, then use the legacy POST probe only as compatibility fallback.
- Snapshot reachability failures on the controller’s probe URL are not proof of an incompatible server. Validation must treat those the same way runtime does so admins can still save a compatible Obico target.
- A legacy `POST /p/` probe returning `405` is **not** compatibility; it means the fallback contract is unavailable and the server should be rejected as incompatible.

## Examples
- Runtime service: `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs`
- Validation/controller: `src/api/Controllers/ObicoServerController.cs`
- Focused tests:
  - `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs`
  - `src/tests/Farm.Web.Api.Tests/Controllers/ObicoServerControllerTests.cs`

## Anti-Patterns
- **POST-only assumption** — Breaking self-hosted upstream Obico by always posting multipart data to `/p/`.
- **Single-shape JSON parsing** — Assuming only `result.p` exists and rejecting upstream `detections` responses.
- **Validation/runtime drift** — Updating the service contract without updating add/enable/health validation.
- **Treating POST 405 as healthy** — Accepting a method-mismatch on the legacy probe even though runtime monitoring cannot use that server.
- **Fallback on auth/network failures** — Masking real operational issues as compatibility retries.
