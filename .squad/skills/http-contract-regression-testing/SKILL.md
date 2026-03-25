---
name: "http-contract-regression-testing"
description: "How to add small, high-signal regression tests for external HTTP contract changes"
domain: "testing"
confidence: "high"
source: "earned"
---

## Context

Use this when an upstream HTTP dependency changes request method, URL shape, or response payload and you need fast regression coverage without overbuilding the test suite.

## Patterns

### Cover both the caller and the validator

- Add one focused test at the service/client seam that asserts the real outbound contract (method, path, query/header shape, and response parsing).
- Add one focused test at any admin/configuration validation seam that probes the same upstream contract. This catches cases where runtime behavior is fixed but setup/health checks still reject the integration.

### Prefer real upstream payloads over guessed DTOs

- If possible, read the upstream source/docs first.
- Use the exact response shape the upstream service returns in your fake handler.
- For Obico self-hosted ML, the upstream response is `{"detections":[["label", confidence, [x, y, w, h]], ...]}` rather than an object like `{"result":{"p":...}}`.

### Make the old behavior fail loudly

- Configure the “old path” to fail in the test so fallback behavior is visible.
- Example: if the fix should stop locally downloading a snapshot, make any local snapshot fetch return 500 and assert it is never called.

### Use direct controller tests for health/validation logic

- If validation is private but reachable through a controller action, instantiate the controller directly with:
  - in-memory EF `AppDbContext`
  - fake `IHttpClientFactory`
  - mocked logger
- This is faster and more surgical than a full host integration test.

## Examples

- `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs`
  - proves the service should use `GET /p/?img=...`
  - proves local snapshot re-fetch is not part of the upstream contract
- `src/tests/Farm.Web.Api.Tests/Controllers/ObicoServerControllerTests.cs`
  - proves create/health validation must probe the GET prediction endpoint

## Anti-Patterns

- **Testing only deserialization** — misses request-method/path regressions.
- **Testing only the runtime service** — misses setup/health validation mismatches.
- **Using guessed payload objects** — creates green tests against the wrong contract.
- **Allowing fallback behavior in the happy-path test** — hides that the new contract still is not truly supported.
