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
- If the server responds with `404`, `405`, `400`, or `415`, treat that as “this endpoint likely does not support the upstream GET contract” and retry the legacy multipart flow.
- Do **not** automatically fall back on every failure. Auth, connectivity, and timeout failures should surface as real errors.

### Keep validation aligned with runtime behavior
- `ObicoServerController` health/create/enable validation must probe the same contract the runtime service prefers.
- If runtime uses GET `/p/?img=...`, controller validation should probe the upstream JSON shape first, then use the legacy POST probe only as compatibility fallback.

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
- **Fallback on auth/network failures** — Masking real operational issues as compatibility retries.
