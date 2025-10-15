## Slicer Services Test Strategy

This folder contains integration and Docker-based tests for the PrusaSlicer and OrcaSlicer workers.

### Goals
1. Validate per-worker functionality (queue integration, progress notifications, local storage, etc.).
2. Exercise Docker images for each slicer worker to ensure:
   - Image builds successfully
   - Binary is present & executable
   - Environment variables are wired correctly
   - (Where exposed) health endpoints become responsive within adaptive timeout windows
3. Avoid duplication while keeping per-worker failure signals clear.

### Shared Docker Testing Pattern
Earlier each worker had its own copy of Docker command + polling logic. These were consolidated into:

`Util/DockerTestHelpers.cs` – central helper providing:
- `RunDockerCommandAsync` / `RunDockerComposeCommandAsync`
- Adaptive HTTP health polling: `WaitForServiceAsync`
- Adaptive exec polling (binary existence / readiness): `WaitForExecSuccessAsync`
- Simple health probe: `CheckServiceHealthAsync`

### Parameterized Common Tests
`SlicerWorkerDockerCommonTests` contains two `[Theory]` tests that run against a matrix of slicer workers:
- `Binary_ShouldBeInstalled_InContainer`
- `Version_CommandInvocation_ShouldReturnHelpOrExist`

These cover binary presence, executability, env var correctness, and (best‑effort) help/version output for both workers. The matrix currently includes:
```
prusaslicer-worker (port 8082, /usr/local/bin/prusa-slicer)
orcaslicer-worker (port 8081, /usr/local/bin/orcaslicer)
```

### Per-Worker Tests
Worker-specific / cross-stack behavior still lives in:
- `PrusaSlicerDockerIntegrationTests`
- `OrcaSlicerDockerIntegrationTests`
- Mixed / microservices composition: e.g. combined health start test
Worker-specific / cross-stack behavior has been moved to the dedicated integration test project:
- `src/tests/Farm.Web.IntegrationTests/SlicerServices/PrusaSlicerDockerIntegrationTests.cs`
- `src/tests/Farm.Web.IntegrationTests/SlicerServices/OrcaSlicerDockerIntegrationTests.cs`
- Mixed / microservices composition: see the integration project for combined health/start tests

Removed / skipped tests: Redundant Prusa-only binary & version tests were removed after consolidation.

### Adding Another Slicer Worker
1. Ensure docker-compose service name, port, and binary path are defined.
2. Add an entry to `WorkerMatrix` in `SlicerWorkerDockerCommonTests` with:
   `serviceName, port, binaryPath, envVarNameForPath, expectedMarkerString`
3. (Optional) Add any worker-specific tests to a new `XYZSlicerDockerIntegrationTests` file.

### Rationale for Adaptive Polling
Hard-coded long `Task.Delay` sequences caused slow / flaky builds. Adaptive polling:
- Polls quickly (2–3s) until healthy or timeout
- Captures last failure message for diagnostics
- Fails fast when a service will not become ready

### Running Only Non-Docker Tests
Use a trait exclusion (example):
```
dotnet test --filter "Category!=Docker"
```

### Future Improvements
- Parameterize health/startup test across workers
- Add G-code generation smoke test (currently skipped due to runtime cost)
- Extend matrix to cover additional slicer variants or configuration permutations (e.g., GPU-enabled images)

---
Last updated: 2025-09-12
