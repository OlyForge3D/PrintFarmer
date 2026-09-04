# Slicing failure investigation

## Request

Investigate and fix the reported OrcaSlicer slicing failure after redeployment from
`development`. Trace the slice-job flow, collect the four API debugging facts where
possible, inspect recent changes and deployment configuration, and make a focused
code/config fix with tests if the evidence supports one. Do not read or print
secrets. Commit the completed work with the required trailer.

## Action plan

- [x] Trace slice-job ownership from React/API entry points through the slicer host and worker.
- [x] Inspect recent `development` changes and deployment/nginx configuration for relevant regressions.
- [x] Collect backend logs, direct backend result, and unauthenticated result from any available local Docker/API environment.
- [x] Identify whether a production fix is warranted; do not change code without reproducible evidence.
- [x] Review the existing focused tests covering failure diagnostics and preserve existing contracts.
- [x] Run targeted validation and record environment blockers or deployment evidence.
- [x] Review the tracking diff and commit the completed investigation with the required co-author trailer.

## Debugging evidence

| Fact | Result |
| --- | --- |
| Owning backend route | `POST /api/slice` and worker mutation routes are owned by `printfarmer-slicer-host` on port 5246 in split deployments. |
| Owning service logs | Blocked: no `printfarmer-slicer-host` or `printfarmer-api` containers are running locally, so no logs are available. |
| Direct backend result | Blocked: `http://localhost:5246/healthz` and `http://localhost:5245/healthz` both refused connections; no direct slice request could be made. |
| Unauthenticated result | Blocked for the same reason; nginx on `http://localhost/api/slice` also refused the connection. |

## Findings

The repository already contains the relevant issue-1811 failure-diagnostic path: OrcaSlicer `result.json` is read before stream scraping, the worker sends the composed diagnostic plus a typed `SliceFailureReason`, and the API persists the detail while exposing only the safe generic message to non-admin clients. Recent development changes inspected (`544d85bd0`, `0109e067f`, and deployment/nginx changes) do not show a new regression in the slice execution path. The screenshot's generic `Slicing failed.` text is therefore expected for the redacted client-facing channel and is not enough to identify an OrcaSlicer or deployment root cause.

No production code/configuration change is justified without the job ID, owning service logs, request payload, or a reproducible deployment. The duplicated `ArtifactStorage__RootPath` entry in the slicer-host compose template is unrelated to slice execution and was left unchanged.

Focused validation passed: `SlicerFailureReportTransmissionTests` (6 tests) and the slicer failure contract tests (8 tests). Initial parallel execution was invalid because both test processes contended for shared build outputs; sequential reruns passed.

## Summary

Investigation complete with no safe code fix identified. The four required debugging facts are recorded above; deployment evidence is blocked by the absence of local containers and endpoint availability. Existing diagnostic/reporting contracts are covered by focused tests and remain intact.
