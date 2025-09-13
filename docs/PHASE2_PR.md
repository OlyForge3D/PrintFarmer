Phase 2: Slicer Integration — Summary of changes

This PR contains the Phase‑2 work to add robust, testable slicer integration including:

- Worker & Queue
  - Redis-based job queue (sorted-set) improvements: scheduledAt, retry counting, jitter persisted and applied on requeue.
  - SlicerWorkerHostedService decoupled with IProcessRunner abstraction for deterministic tests (TestProcessRunner used in unit tests).
  - Exponential backoff + admin-configurable jitter applied when requeuing transient failures.

- API
  - Slicer orchestrator and job lifecycle improvements.
  - `SlicingSubmissionController`: robust multipart handling (modelFile and files fallback), deterministic Testing-mode behavior that registers queued jobs in `SlicingJobStore` for testability, and richer SliceResultDto responses for integration tests.
  - `SlicerSettingsController` for admin-configurable runtime settings (JitterPercent + per-engine settings). Server validation added (Jitter 0..100).
  - `SlicerJobsController` and SSE progress stream stability improvements.

- Frontend
  - Admin UI page `Admin → Slicer Worker Settings` to manage per-engine paths/args and the jitter percent.
  - Validation for jitter percent on client and server.

- Tests
  - Deterministic worker & monitor unit tests added (no external processes needed).
  - Redis queue tests use mocked Redis interfaces.
  - Added small UI test for SlicerSettings page (validation + save behavior).

- Docs
  - `documentation/SlicerRuntimeSettings.md` added with operational guidance, default values, migration notes and how the admin UI interacts with the service.
  - This PR summary (this file) describing the scope and follow-ups.

Known follow-ups / next steps

- CI: address remaining frontend unit test expectations (some tests assume different DOM structure for skeletons/labels) and restore lint rules to green.
- Frontend linting/production build: resolve outstanding TypeScript/ESLint findings before final merge.
- Gate Docker integration tests in CI or provide step to provision required test images.
- Small analyzer warnings remaining (formatting & minor suggestions) — these can be addressed in a subsequent smaller PR or by running `dotnet format` in CI.

Notes for reviewers

- Large portion of the changes are test scaffolding to make worker behavior deterministic without Docker/binaries; please focus review on the public API (controllers + settings DTOs) and the worker retry semantics.
- The SlicerSettings UI already exposes the jitter percent setting; verify server validation messages and UX flows for admin users.

## Files changed (high level)

- API:
  - `src/api/Controllers/Slicing/SlicingSubmissionController.cs` — multipart form robustness, testing-mode job registration, richer SliceResultDto response
  - `src/api/Controllers/Slicing/SlicingJobsController.cs` — (unchanged behavior, used by tests)
  - `src/api/Services/SlicerServices/SlicerWorkerHostedService.cs` — worker task/processing robustness
  - `src/api/Services/SlicerServices/Progress/*.cs` — parser and monitor resilience fixes
  - `src/api/Services/SlicerServices/Process/SystemProcessRunner.cs` — small defensive formatting and guard handling

- Tests:
  - `src/tests/Farm.Web.Api.Tests/SlicerServices/*` — worker, queue, monitor unit tests
  - `src/tests/Farm.Web.Api.Tests/Slicing/*` — integration-level tests that exercise the submission and job endpoints

- Frontend:
  - `src/Web/ReactApp/src/pages/SlicerSettingsPage.tsx` (UI) and `src/Web/ReactApp/src/test/pages/SlicerSettingsPage.test.tsx` (validation test)

## Local validation for reviewers

1. From `src`, run `dotnet build ./farm-web.sln -c Debug`.
2. Run focused tests locally (units & integration): `dotnet test ./tests/Farm.Web.Api.Tests/Farm.Web.Api.Tests.csproj -c Debug` or use the IDE Test Explorer.
3. Start the API and React dev server, open Admin → Slicer Worker Settings, and verify that jitter percent can be changed and saved.
4. Manual smoke: submit a slicing job via Admin UI or curl and verify `/api/slicer/jobs/{id}` returns queued job metadata in Testing env.

## Merge strategy and follow-ups

- Merge into `dev/jpapiez/slicer-integration` with a subsequent small cleanup PR that runs `dotnet format` across the solution and addresses remaining lightweight analyzer warnings (Program.cs formatting noted in review). This second PR keeps the functional changes small and focused.

- After the formatting PR, re-run full CI (all tests + frontend production build & lint) and address any TypeScript/ESLint findings.

