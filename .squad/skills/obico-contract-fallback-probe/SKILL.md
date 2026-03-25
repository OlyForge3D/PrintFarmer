---
name: "obico-contract-fallback-probe"
description: "Keep Obico-compatible integrations aligned by probing the upstream GET snapshot contract first, then falling back to the legacy multipart upload contract."
domain: "backend-integration"
confidence: "high"
source: "earned"
---

## Context

Use this when an external ML or inference API has moved to a new request contract, but PrintFarmer still needs backward compatibility with older servers.

## Patterns

- Update the runtime client and the admin/server-validation path together; a contract fix is incomplete if monitoring uses one route and settings health checks use another.
- Prefer the upstream `GET /p/?img=...` contract first, because that matches self-hosted Obico behavior where the ML server fetches the snapshot directly.
- Keep a legacy fallback to multipart `POST /p/` for older deployments that still expect image upload.
- Parse both response shapes in tests: upstream `detections` arrays and legacy `result.p` payloads.
- Add paired tests for both success paths: upstream GET success and GET-to-legacy fallback.

## Examples

- `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs`
- `src/api/Controllers/ObicoServerController.cs`
- `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs`
- `src/tests/Farm.Web.Api.Tests/Controllers/ObicoServerControllerTests.cs`

## Anti-Patterns

- Fixing only the background monitoring client while leaving the admin health check on the old contract.
- Treating `POST /p/` reachability as proof that a self-hosted upstream GET contract is usable.
- Shipping contract changes without tests that cover both the preferred path and the fallback path.
