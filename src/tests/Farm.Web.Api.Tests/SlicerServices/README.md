## Slicer Service Integration Test Strategy

This directory contains integration tests for slicer worker services (PrusaSlicer & OrcaSlicer) plus shared, parameterized coverage.

### Goals
1. Verify container images build successfully.
2. Confirm worker containers start and become healthy.
3. Assert presence, permissions, and basic invocation of slicer binaries.
4. Validate environment variable wiring & distinct worker identities.
5. Provide fast feedback on common cross-worker behavior without duplication.

### Structure

| File | Purpose |
|------|---------|
| `PrusaSlicerDockerIntegrationTests.cs` | Prusa-specific health & multi-service coordination tests. Binary & version tests removed (now shared). |
| `OrcaSlicerDockerIntegrationTests.cs` | Orca-specific health & environment tests. |
| `SlicerWorkerDockerCommonTests.cs` | Parameterized theories covering binary existence + version/help invocation for both workers. |
| `DockerTestHelpers.cs` | Shared adaptive polling + docker exec helpers (argument validated, timeout-capable). |

### Categorization & Traits

All Docker tests are tagged with: `Trait("Category", "Docker")`.

Other repository-wide categories used for filtering:

* `DbHeavy` – Requires database schema + EF interactions.
* `Slow` – Known to take noticeably longer or exercise large I/O paths.
* `Docker` – Spins up containers; excluded from fast inner-loop.
* (Implicit) `Fast` – Any test lacking the above categories.

### Parameterized Coverage Rationale

Originally both worker test classes duplicated logic to verify:
* Binary installation (file exists, executable).
* Help / version-style invocation.
* Environment variable pointing to binary location.

This duplication increased maintenance and promoted drift. The class `SlicerWorkerDockerCommonTests` centralizes these behaviors using a worker matrix:

```csharp
new object[] { "prusaslicer-worker", 8082, "/usr/local/bin/prusa-slicer", "Worker__PrusaSlicerPath", "Prusa" },
new object[] { "orcaslicer-worker", 8081, "/usr/local/bin/orcaslicer", "Worker__OrcaSlicerPath", "Orca" }
```

Advantages:
* Single place to extend when adding a new slicer worker.
* Consistent assertions & adaptive waiting logic.
* Reduced flakiness: shared polling primitives with clear timeout semantics.

### Adaptive Polling

`DockerTestHelpers` implements:
* `WaitForServiceAsync` – Polls health endpoint (default `/healthz`) until success or timeout.
* `WaitForExecSuccessAsync` – Repeatedly performs `docker compose exec` until the command exits 0.
* Both methods capture timing and last failure message to aid diagnostics.

### Timeouts & Stability

* Binary existence waits: 90s upper bound (cold image pulls / first startup).
* Service health waits: 30–90s depending on call site.
* Exec polling interval defaults (2–3s) chosen to balance responsiveness vs. log noise.

### When Adding a New Worker
1. Append a new row to `WorkerMatrix` in `SlicerWorkerDockerCommonTests`.
2. Add any worker-specific environment or health tests in a dedicated `<WorkerName>DockerIntegrationTests` file if needed.
3. Ensure docker-compose file exposes a health endpoint (or gracefully handle absence—the common tests already swallow health exceptions).

### Filtering Test Runs

Use the helper script `scripts/grouped-tests.sh` to run groups sequentially and isolate hangs or long-running sets.

Examples:
```bash
# Run only fast tests (exclude Docker/DbHeavy/Slow)
scripts/grouped-tests.sh INCLUDE=Fast

# Run DbHeavy then Fast (alphabetical order of specification preserved)
INCLUDE=DbHeavy,Fast scripts/grouped-tests.sh

# Run everything except Docker
EXCLUDE=Docker scripts/grouped-tests.sh
```

The script emits per-group timing and a JSON summary under `test-logs/grouped/`.

### Future Improvements
* Add a health matrix test parameterizing ports + endpoints (currently each worker handles its own health separately).
* Introduce retry budget metrics in output summary for better visibility into borderline startup times.
* Potential integration with CI to fail early if cumulative Docker group time regresses beyond a threshold.

---
Last updated: 2025-09-12
# Slicer Services Docker Integration Test Strategy

This directory contains Docker-based integration tests for the slicer worker services:

- `prusaslicer-worker`
- `orcaslicer-worker`

## Goals

1. Verify container images build and start correctly.
2. Assert presence & executability of slicer binaries inside containers.
3. Validate environment variable configuration (worker IDs, binary paths).
4. Provide adaptive, bounded polling for health and exec readiness (no fixed long `Task.Delay`).
5. Minimize duplication across workers via parameterized/common test coverage.

## Structure

| File | Purpose |
|------|---------|
| `PrusaSlicerDockerIntegrationTests.cs` | Prusa-specific health / mixed stack / config tests (binary + version tests removed; now covered centrally). |
| `OrcaSlicerDockerIntegrationTests.cs`  | Orca-specific build, health, env, version tests (binary + version also covered centrally). |
| `SlicerWorkerDockerCommonTests.cs`     | Parameterized tests covering binary presence, env var, and version/help behavior for both workers. |
| `../Util/DockerTestHelpers.cs`         | Shared adaptive polling & docker command execution helpers with timeouts. |

## Categories & Filtering

All Docker tests are tagged with: `Trait("Category", "Docker")`.

Run only non-Docker tests:
```bash
dotnet test ./farm-web.sln --filter Category!=Docker
```

Run only Docker tests:
```bash
dotnet test ./farm-web.sln --filter Category=Docker
```

## Adaptive Polling Pattern

Instead of fixed large delays (e.g., 60s sleeps), tests call:

- `WaitForServiceAsync` – Repeatedly probes a health endpoint until success or timeout.
- `WaitForExecSuccessAsync` – Repeatedly attempts a `docker compose exec` command (e.g., `test -f <binary>`) until it succeeds.

Both methods:
- Capture last failure message for diagnostics.
- Accept configurable timeout & poll interval.
- Throw a `TimeoutException` with contextual information on failure.

## Timeouts & Hanging Prevention

To avoid IDE (VS Code) lock-ups:

1. Docker command execution now enforces a max wall-clock timeout (see `DockerTestHelpers`).
2. Each adaptive wait has explicit overall timeout (defaults 60–90s depending on scenario).
3. Long, full-stack or end-to-end slicing tests are marked `[Fact(Skip = ...)]` to prevent accidental inclusion in standard runs.

## Extending Tests

When adding a new slicer worker or capability:

1. Add a new row to `WorkerMatrix` in `SlicerWorkerDockerCommonTests` with:
   - service name
   - port
   - binary path
   - env var for path
   - marker string for version/help matching
2. (Optionally) Add a specialized test file if there is behavior unique to that worker.
3. Reuse existing helpers; do **not** duplicate polling logic.

## Skipped Tests Rationale

Some legacy Prusa-specific binary/version tests were removed in favor of the parameterized matrix. Skipped long-running tests (end-to-end slicing) remain as documentation and can be enabled manually when needed for a deeper validation cycle.

## Future Improvements

- Add a parameterized health test (if all workers expose a uniform `/healthz`).
- Incorporate container log tail on failure for improved diagnostics.
- Surface timing metrics (startup latency) as test output for performance baselining.
- Integrate trait-based grouping for "Common" vs "Extended" docker tests.

---
Last updated: (auto-generated) see git history for changes.
# Slicer Services Integration Test Strategy

This directory contains Docker-based integration tests for the PrusaSlicer and OrcaSlicer worker containers and shared, parameterized coverage for behaviors common to all slicer workers.

## Goals
1. Fast feedback that each worker container builds and boots correctly in isolation.
2. Shared adaptive polling logic (no fixed long `Task.Delay` usage) for reliable, time‑bounded readiness checks.
3. Minimize duplication between Prusa and Orca test suites via a parameterized matrix.
4. Enable running the broader test suite *excluding* slow / Docker categories for rapid inner‑loop development.

## Key Components

| File | Purpose |
|------|---------|
| `DockerTestHelpers.cs` | Centralized adaptive polling + docker/compose exec helpers. Avoids code drift between workers. |
| `PrusaSlicerDockerIntegrationTests.cs` | Prusa-specific health & mixed‑stack tests (binary/version tests removed—now centralized). |
| `OrcaSlicerDockerIntegrationTests.cs` | Orca-specific startup, env var and optional full‑stack smoke test. |
| `SlicerWorkerDockerCommonTests.cs` | Parameterized `[Theory]` tests covering binary presence, permissions, env var path mapping and `--help`/version invocation for both workers. |

## Parameterized Test Matrix
`SlicerWorkerDockerCommonTests.WorkerMatrix` currently defines:

```
service            | port | binary path                    | env var key              | marker
-------------------|------|--------------------------------|--------------------------|--------
prusaslicer-worker | 8082 | /usr/local/bin/prusa-slicer    | Worker__PrusaSlicerPath  | Prusa
orcaslicer-worker  | 8081 | /usr/local/bin/orcaslicer      | Worker__OrcaSlicerPath   | Orca
```

Add a new slicer worker by appending a row to this matrix; no additional test class needed for basic binary/env validation.

## Adaptive Polling Pattern
All waits use loops that:
1. Attempt a health endpoint (if exposed) or a docker exec command.
2. Back off with a small, fixed poll interval (2–3s).
3. Enforce a maximum timeout (default 60–120s depending on the scenario).
4. Capture the *last* failure message for actionable timeout diagnostics.

Benefits:
* Eliminates nondeterministic long sleeps.
* Provides granular progress information in test output via `ITestOutputHelper`.
* Makes failures fast when the container crashes early.

## Category & Filtering
All Docker tests are tagged: `Trait("Category", "Docker")`.

Run **only non‑Docker** tests (faster inner loop):
```
dotnet test ./farm-web.sln -c Debug --filter "Category!=Docker"
```

Run **only Docker** tests:
```
dotnet test ./farm-web.sln -c Debug --filter "Category=Docker"
```

## Removed / Consolidated Tests
Prusa-specific binary + version tests were removed after introducing `SlicerWorkerDockerCommonTests` to eliminate duplication. Orca equivalents remain only where behavior differs (e.g., frontend-inclusive full‑stack smoke test which is intentionally skipped by default).

## Adding a New Worker
1. Implement the container & compose service.
2. Add a matrix row in `SlicerWorkerDockerCommonTests`.
3. If the worker exposes a health endpoint on a new port, include that port in the matrix.
4. (Optional) Add a dedicated test file only for behavior unique to that worker (keep it lean).

## Troubleshooting Timeouts
Common causes:
* Binary path mismatch (adjust matrix binaryPath).
* Service name mismatch with compose (`docker compose ps` to verify).
* Health endpoint not yet exposed—acceptable; the helper falls back to exec existence checks. Consider adding an endpoint or extend timeout.
* Local Docker daemon resource pressure—raise timeout value in test call.

## Future Enhancements (Backlog Ideas)
* Parameterized health test (shared) once every worker consistently exposes `/healthz`.
* Add log scraping on timeout for last 50 lines of container logs.
* Introduce a shorter “smoke” subset tag (e.g., `Trait("Size","Smoke")`).
* Integrate with CI to run Docker tests behind an opt‑in flag (environment variable or test filter).

---
Last updated: 2025-09-12
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
