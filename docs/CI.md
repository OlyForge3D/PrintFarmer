# CI: Affected .NET Test Selection & Pre-Push Format Gate

This document describes PrintFarmer's CI strategy and the local pre-push
formatting hook that replaced the `dotnet format` step in CI.

Related:

- Workflow: [`.github/workflows/ci.yml`](../.github/workflows/ci.yml)
- Selector script: [`scripts/ci/select-dotnet-tests.sh`](../scripts/ci/select-dotnet-tests.sh)
- Selector tests: [`scripts/ci/tests/test-select-dotnet-tests.sh`](../scripts/ci/tests/test-select-dotnet-tests.sh)
- Pre-push hook: [`.githooks/pre-push`](../.githooks/pre-push)
- Pre-push hook tests: [`.githooks/tests/test-pre-push.sh`](../.githooks/tests/test-pre-push.sh)
- Hook installer: [`.githooks/setup.sh`](../.githooks/setup.sh)

---

## Overview

```mermaid
flowchart LR
  A[PR opened] --> B[select job]
  B -->|frontend inputs or full-safe| C[frontend job]
  B -->|dotnet inputs or full-safe| D[dotnet-build full sln]
  D -->|project tarballs| E[dotnet-test matrix]
  D -->|project tarballs| F[migration-drift]
  D -->|API test + App migration tarballs| J[dotnet-test-providers]
  B --> G[ci-tools]
  B -->|dotnet inputs or full-safe| I[dependency-compliance]
  C --> H[summary]
  E --> H
  F --> H
  J --> H
  G --> H
  I --> H
```

Every PR triggers `select` and `ci-tools`. The selector classifies changed
paths and emits outputs consumed by the conditional jobs. This produces a
required, stable check name even when no application build runs, such as a
docs-only PR.

## Jobs

| Job                     | Runs when                                                    | Notes                                                                 |
| ----------------------- | ------------------------------------------------------------ | --------------------------------------------------------------------- |
| `select`                | always                                                       | Classifies changed paths; emits `want_*`, `matrix`, `mig_matrix`.     |
| `ci-tools`              | always                                                       | Runs `bash -n` + selector + hook tests + `node --test` compliance/squad-tooling suites; no .NET restore. |
| `dependency-compliance` | any .NET input changed OR full-safe (same as `want_dotnet_build`) | `dotnet restore` + `node scripts/compliance/validate-compliance.mjs` — dependency-license/provenance inventory. See #1395. |
| `frontend`              | React inputs changed OR full-safe                            | `npm ci`, lint, build, `npm run test:coverage` in `src/Web/ReactApp/`. |
| `dotnet-build`          | any .NET input changed OR full-safe                          | Restores/builds once, explicitly builds IntegrationTests when selected, and uploads one compressed tarball per selected project. |
| `migration-drift`       | App or Slicer schema-relevant inputs changed OR full-safe    | Restores runner-local project metadata, downloads its compiled project, then runs `has-pending-model-changes --no-build`. |
| `dotnet-test`           | .NET test-relevant inputs changed OR full-safe               | **Matrix** — one leg per project/shard; downloads the archive keyed by `matrix.project`, then executes the test DLL directly. |
| `dotnet-test-providers` | .NET test-relevant inputs changed OR full-safe               | Restores locally for EF metadata, downloads API-test and App-migration outputs, applies both providers, and executes the API test DLL. |
| `summary`               | always (`if: always()`)                                      | Aggregates gates; hard-fails on required check regression.            |

### `dependency-compliance` gating (#1395)

`ci-tools` used to unconditionally run a full `dotnet restore` +
`validate-compliance.mjs` on every event — including mobile-only and
docs-only PRs — solely to keep dependency-license/provenance validation
fail-closed. That restore is real network/CPU cost with nothing to validate
when no `.NET`-relevant bucket changed: the restore only ever covers
`farm-web.sln`'s project graph, and any edit to a `.csproj` already
referenced by that graph lands in a `src/**` bucket (`api`, `infra`,
`backends`, `slicer`, `tools`, etc.) that already forces
`want_dotnet_build=true`; adding a new project to the graph requires
editing `farm-web.sln` itself, which is `shared_config` and always forces
full-safe.

`dependency-compliance` now runs the restore-then-validate pair in its own
job gated by the exact same `want_dotnet_build` output the `dotnet-build`
job uses, so mobile-only/docs-only PRs skip it entirely. Coverage does not
regress: `want_dotnet_build` is forced `true` (full-safe) on every trusted
push to `main`/`development`, on `workflow_dispatch`, and on any
`shared_config` bucket change (`Directory.Packages.props`, `NuGet.Config`,
any `*.sln`, `Directory.Build.*`) — see "Full-safe (`full_matrix=1`)
triggers" above — so dependency/license drift is still caught fail-closed
on the branches that matter. `ci-tools` itself stays restore-free and fast
on every PR.

## Selection logic (selector script)

`scripts/ci/select-dotnet-tests.sh` reads either:

- `CHANGED_FILES_FROM_Z`: path to a NUL-terminated file list (preferred), or
- `CHANGED_FILES`: newline-separated list (used by the workflow's fallback path).

It classifies each path into one of the buckets below and emits selection
outputs on `$GITHUB_OUTPUT`. `--no-renames` is passed on every `git diff`
invocation so that renames decompose to add+delete pairs — both endpoints
classify.

### Bucket → downstream mapping

| Bucket and exact path selector | Frontend | .NET build | .NET tests | Migration drift | Full-safe |
| --- | :---: | :---: | --- | --- | :---: |
| `frontend`: `src/Web/**` | ✓ | | | | |
| `api`: `src/api/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Slicer.Module.Tests`, `Farm.Web.IntegrationTests` | `AppPg`, `AppSqlServer` | |
| `infra`: `src/infra/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Slicer.Module.Tests`, `Farm.OrcaSlicer.Worker.Tests`, `Farm.Web.IntegrationTests`, `Farm.Modules.SmartPlug.Tests`, `Farm.Modules.Maintenance.Tests`, `Farm.Modules.Calibration.Tests` | `AppPg`, `AppSqlServer` | |
| `backend_core`: `src/backends/Farm.Backend.Plugin.Core/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Slicer.Module.Tests`, `Farm.OrcaSlicer.Worker.Tests`, `Farm.Web.IntegrationTests` | | |
| `backend_plugin`: every other `src/backends/**` path (concrete plugin projects) | | ✓ | `Farm.Web.Api.Tests`, `Farm.Web.IntegrationTests` | | |
| `slicer`: `src/slicer/**`, `src/Slicers/**`, `src/worker-shared/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Slicer.Module.Tests`, `Farm.OrcaSlicer.Worker.Tests`, `Farm.Web.IntegrationTests`, `Farm.Modules.Calibration.Tests` | `SlicerPg`, `SlicerSqlServer` | |
| `orca_worker`: `src/orcaslicer-worker/**` | | ✓ | `Farm.OrcaSlicer.Worker.Tests` | | |
| `smartplug`: `src/modules/Farm.Modules.SmartPlug/**` | | ✓ | `Farm.Modules.SmartPlug.Tests`, `Farm.Web.Api.Tests` | | |
| `maintenance`: `src/modules/Farm.Modules.Maintenance/**` | | ✓ | `Farm.Modules.Maintenance.Tests`, `Farm.Web.Api.Tests` | | |
| `calibration`: `src/modules/Farm.Modules.Calibration/**` | | ✓ | `Farm.Modules.Calibration.Tests`, `Farm.Web.Api.Tests` | | |
| `migrations_app`: `src/migrations/Farm.Migrations.*/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Web.IntegrationTests` | `AppPg`, `AppSqlServer` | |
| `migrations_slcr`: `src/migrations/Farm.Slicer.Migrations.*/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Slicer.Module.Tests`, `Farm.Web.IntegrationTests` | `SlicerPg`, `SlicerSqlServer` | |
| `tests_api`: `src/tests/Farm.Web.Api.Tests/**` | | ✓ | `Farm.Web.Api.Tests` | | |
| `tests_slicer`: `src/tests/Farm.Slicer.Module.Tests/**` | | ✓ | `Farm.Slicer.Module.Tests` | | |
| `tests_orca`: `src/tests/Farm.OrcaSlicer.Worker.Tests/**` | | ✓ | `Farm.OrcaSlicer.Worker.Tests` | | |
| `tests_smartplug`: `src/tests/Farm.Modules.SmartPlug.Tests/**` | | ✓ | `Farm.Modules.SmartPlug.Tests` | | |
| `tests_maintenance`: `src/tests/Farm.Modules.Maintenance.Tests/**` | | ✓ | `Farm.Modules.Maintenance.Tests` | | |
| `tests_calibration`: `src/tests/Farm.Modules.Calibration.Tests/**` | | ✓ | `Farm.Modules.Calibration.Tests` | | |
| `tests_integration`: `src/tests/Farm.Web.IntegrationTests/**` | | ✓ | `Farm.Web.IntegrationTests` | | |
| `tests_shared`: `src/tests/Farm.Testing.Shared/**` | ✓ | ✓ | all | all | ✓ |
| `tests_other`: every other `src/tests/**` path | ✓ | ✓ | all | all | ✓ |
| `discovery`: `src/discovery/**`, `src/printer-discovery/**` | ✓ | ✓ | all | all | ✓ |
| `settings`: `src/settings/**` | ✓ | ✓ | all | all | ✓ |
| `shared_config`: `global.json`, any `*.sln`, `Directory.Build.*`, `Directory.Packages.props`, `NuGet.Config`, `src/.editorconfig` | ✓ | ✓ | all | all | ✓ |
| `ci_selector`: `.github/workflows/**`, `scripts/ci/**`, `.githooks/**`, `.devcontainer/**` | ✓ | ✓ | all | all | ✓ |
| `unknown_src`: every other `src/**` path | ✓ | ✓ | all | all | ✓ |
| `tools`: `src/tools/**` | | ✓ | | | |
| `docs`: `docs/**`, root `*.md`, `LICENSE*`, root `.editorconfig`, `.gitignore`, `.gitattributes` | | | | | |
| `mobile`: `mobile/**` | | | | | |
| `unclassified`: every other repository path | | | | | |

`ci-tools` is unconditional and therefore runs for every bucket, including
`docs`, `mobile`, and `unclassified`. `dependency-compliance` is gated on
`want_dotnet_build` (see the ".NET build" ✓ column above) and therefore
runs for the same buckets as `dotnet-build` — it does NOT run for `docs`-
or `mobile`-only buckets, but DOES run for a `tools`-only bucket, since
`src/tools/**` sets `.NET build` to ✓ (a tools-only change still needs the
restored `project.assets.json` the validator reads).

Unlike `orca_worker` (a pure-service module with no owned controller),
`smartplug` also selects `Farm.Web.Api.Tests`: `AdminPowerMonitorsController`
moved into `Farm.Modules.SmartPlug`, but its own coverage
(`RouteTableSnapshotTests`, the `CustomWebApplicationFactory`-based
`AdminPowerMonitorsControllerTests`) intentionally stayed behind in
`Farm.Web.Api.Tests` — see `docs/MODULE_MIGRATION_PATTERN.md`. Any future
`Farm.Modules.*` phase (9-18) that owns a controller must add its API-tests
project the same way; a phase that moves only services (no controller) can
stay as narrow as `orca_worker`.

Similarly, `maintenance` selects `Farm.Web.Api.Tests`: five controllers plus
`MaintenanceHub` (the first SignalR hub extracted into a module) moved into
`Farm.Modules.Maintenance`, but `RouteTableSnapshotTests`,
`MaintenanceHubAuthorizationIntegrationTests`, and
`MaintenanceScheduleDeploymentToolheadScopeTests` intentionally stayed behind
in `Farm.Web.Api.Tests`.

`calibration` follows the same controller-owning pattern: its two moved
controllers' own coverage (`RouteTableSnapshotTests`, the calibration
contract-negotiation and health-check tests) intentionally stayed behind in
`Farm.Web.Api.Tests`, so the bucket selects both `Farm.Modules.Calibration.Tests`
and `Farm.Web.Api.Tests`. Because `Farm.Modules.Calibration` depends on both
`Farm.Infrastructure` and `Farm.Slicer.Module`, the `infra` and `slicer` buckets
also select `Farm.Modules.Calibration.Tests`.

### Full-safe (`full_matrix=1`) triggers

- Any of: `shared_config`, `ci_selector`, `unknown_src`, `discovery`, `settings`, `tests_other`, `devcontainer`.
- `workflow_dispatch` event.
- `push` to `main` or `development`.
- Caller sets `FORCE_FULL_SAFE=1`.
- NUL-parse failure of the `_Z` file.
- Git-quoted path detected in newline-form input (non-ASCII name → forces full-safe).

The workflow intentionally has no `push.paths` filter. Every push to `main` or
`development` dispatches CI, and the selector forces full-safe before reading
the changed-path set.

`Farm.Web.IntegrationTests` is invoked as a project-scoped matrix leg, not via
`farm-web.sln`. The selector emits `run_integration=true` for that leg. When it
is selected, `dotnet-build` restores and builds it explicitly with
`-p:RunIntegrationTests=true` after the solution build. Its consumer then runs
the compiled DLL in assembly mode, where project-evaluation properties no
longer apply.

Each matrix leg also carries a `filter` field. The default PR gate uses
`Category!=DbHeavy&Category!=Docker`, and `dotnet-test` passes that value through
to assembly-mode `dotnet test <test.dll> --filter`, so the selector and workflow
stay aligned without re-encoding the same category rule in one branch only.
VSTest applies the same `=`, `!=`, `~`, `|`, and `&` filter grammar in assembly
mode. This keeps the ordinary PR path narrow while leaving provider-heavy
`DbHeavy` / `Docker` runs to the separate provider job and the fail-closed
full-safe matrix.

Projects with manifest `shards` expand into one leg per shard. Leg names use
`<leg>-<shard>` (for example, `Farm.Web.Api.Tests-core`), which is readable in
the checks UI and safe for the leg's TRX filename and artifact name. Every
shard keeps the same project path and `run_integration` value. Its effective
filter is:

```text
(<shard FullyQualifiedName filter>)&(<project defaultFilter>)
```

The parentheses are required because shard namespace clauses use `|`, while
the category exclusions use `&`. Thus `Farm.Web.Api.Tests` runs as `core`,
`infra`, and `services` in parallel without reintroducing provider-heavy tests.
Projects whose `shards` list is empty retain their previous single matrix leg
unchanged.

### Shared build artifacts

The workflow compiles the solution once in `dotnet-build`. It packages only
each selected runnable project's `bin/Debug/net10.0` directory as a
pre-compressed `.tgz`, then uploads that single file with
`actions/upload-artifact@v7` and `archive: false`. Consumers use
`actions/download-artifact@v8` and fetch only the project archives they need.
Artifacts are keyed by `matrix.project`, not `matrix.name`: multiple matrix
shards can execute the same assembly without rebuilding or publishing duplicate
outputs.

The workflow deliberately does **not** transport `obj/`. Generated
`project.assets.json` and `*.nuget.g.props` files embed absolute workspace,
package-cache, and source-root paths. Those paths commonly match between two
GitHub-hosted Ubuntu runners, but they are not contractual, especially while
the workflow selects a floating `10.0.x` SDK patch. Tests avoid project
evaluation entirely by executing the compiled DLL. EF consumers still need
project metadata, so each migration leg performs a measured 7–9 second local
restore before running `dotnet ef --no-build`; the provider job similarly
restores locally before consuming the API-test and App-migration binaries.

Per-project archives are also an economic constraint, not just organization.
A measured build produced about 4 GiB under all `bin/Debug` trees; even the 11
relevant output directories were about 1.8 GiB raw. One 714 MiB compressed
monolith downloaded by every consumer would transfer roughly 8.4 GiB per
full-safe run. Project archives preserve dependency closures but prevent each
leg from downloading unrelated test and migration outputs. Tar also turns
hundreds of filesystem entries into one transfer object and preserves
permissions without paying for a second artifact compression pass.

### Exclusions

- Ordinary `dotnet-test` matrix legs exclude `DbHeavy` and `Docker` categories
  through their `--filter`. The `dotnet-test-providers` job executes those
  categories on the same ordinary .NET PR runs whenever
  `want_dotnet_test=true`.

## Pre-push format gate

`.githooks/pre-push` replaces the CI `dotnet format` step. It runs
`dotnet format ./farm-web.sln --verify-no-changes` against the **exact
outgoing Git tree** — not your working directory — so local dirty state cannot
poison the check.

### Contract

- Reads Git's push list from stdin (`<local_ref> <local_sha> <remote_ref>
  <remote_sha>\n`).
- For each non-delete ref, computes `.NET`-relevant paths in the outgoing diff:
  - `src/**/*.cs`, `src/**/*.csproj`
  - `src/farm-web.sln`, `src/.editorconfig`, `src/Directory.Build.props|targets`
- If none affected → skip the format run and pass immediately.
- Otherwise, extracts the tip's tree via `git archive | tar -x` into a
  detached temporary directory and runs `dotnet format --verify-no-changes`.
- Successful verifications are cached; subsequent pushes of the same tree
  under the same SDK & formatter version skip the run.

### C# encoding and generated migrations

- `src/.editorconfig` requires UTF-8 with a BOM for C# files. Preserve the BOM
  when creating or rewriting a `.cs` file; otherwise the unfiltered gate reports
  `CHARSET` even when the source text is unchanged.
- EF Core scaffolds migration history with block-scoped namespaces. The
  migration-only `IDE0161` override keeps the file-scoped namespace preference
  for handwritten code while avoiding mass indentation churn in generated
  migration bodies. Do not manually reformat generated migrations solely to
  satisfy that style rule.
- Intentional analyzer exceptions in handwritten code remain local to the
  behavior that requires them; do not add those diagnostics to the solution-wide
  suppression list.

### Cache

Successful verifications are stamped at:

```
$(git rev-parse --git-common-dir)/pre-push-fmt-cache/<key>
```

where `<key>` is:

```
sha256(
  "pre-push-format-v2" ||
  sha256(hook_script) ||
  <tree_sha> ||
  <dotnet --version> ||
  <dotnet format --version>
)
```

Any of these changing invalidates the cache. All five fields must produce a
64-hex digest — empty SDK or formatter version fails the push closed.

### Fail-closed behaviour

- Missing `dotnet` binary → push rejected (`rc=1`).
- Missing `sha256sum`/equivalent → push rejected.
- Missing `src/farm-web.sln` in the outgoing tree → push rejected.
- Empty `dotnet --version` or `dotnet format --version` → push rejected.
- Git-diff failure → push rejected.

### Standard bypass

The hook is enforced by `git push`. The documented Git bypass is:

```bash
git push --no-verify
# or
git push -n
```

This skips all local pre-push hooks (including this one) exactly once, per
Git's design. Use it in genuine emergencies only.

**Local hooks are not server-enforceable.** Anyone can bypass or delete their
copy. CI no longer reruns `dotnet format`, so branch protection enforces the
build/test/drift checks but does not independently enforce formatting.

## Install the hooks

From the repo root:

```bash
.githooks/setup.sh
```

This points `core.hooksPath` at `.githooks/` and marks the hooks executable.
Devcontainer setup calls this on first attach; you only need to run it
manually on host-native checkouts.

## Timing (order-of-magnitude)

Baselines depend on runner load, artifact-service throughput, and PR size.
Run 32928133031 measured the duplicated restore/build work that this topology
removes:

| Full-safe duplicated work | Measured before | Shared-build shape |
| --- | ---: | ---: |
| Seven ordinary test legs | 1,081 runner-seconds | One project archive download/extract per leg |
| Four migration legs | 577 runner-seconds | 7–9 second local restore per leg plus one project archive |
| Provider job | 242 runner-seconds | Local restore plus three project archives |
| Total restore/build duplication | about 1,900 runner-seconds | One roughly 296-second solution build plus packaging and consumers |

The pre-change experiment projected about **1,640 runner-seconds (27
runner-minutes) saved per full-safe run even with a monolithic archive**.
Per-project archives reduce transfer below that conservative model, although
API sharding downloads the same API-project archive once per shard. This is a
billing optimization: the central build becomes a fan-out barrier, so expect a
roughly **60–90 second fan-out delay** before test execution. In exchange, API
test sharding targets the measured 26-minute long tail at roughly **12 minutes
wall-clock** by running its three partitions concurrently. Narrow .NET
selections can see a smaller version of the shared-build tradeoff. React-only
and docs-only runs are unchanged because `dotnet-build` remains selector-gated.

The pre-push hook shifts ~15-90 s of `dotnet format` out of CI onto the
committer's machine — cached after the first successful verification of any
given tree.

## Failure diagnosis

- `select` failed → inspect the "changed paths" section printed to the job
  summary; treat any surprise as a bug in the selector and add a test case in
  `scripts/ci/tests/test-select-dotnet-tests.sh`.
- `ci-tools` failed → the selector or hook tests regressed. Reproduce with
  `bash scripts/ci/tests/test-select-dotnet-tests.sh` and
  `bash .githooks/tests/test-pre-push.sh` locally.
- `dependency-compliance` failed → a NuGet package license/provenance check
  regressed, or the solution failed to restore. Reproduce with
  `cd src && dotnet restore ./farm-web.sln && cd .. && node scripts/compliance/validate-compliance.mjs`.
  If it unexpectedly ran (or was skipped) for a given PR, check
  `want_dotnet_build` in the `select` job summary — it mirrors `dotnet-build`.
- `dotnet-test` matrix leg failed → per-leg `TestResults/*.trx` is uploaded
  as `dotnet-test-results-<leg>` artifact. Download and inspect. The
  workflow also asserts that the TRX reports non-zero executed tests, so an
  empty test run is a hard failure rather than a silent pass.
- `migration-drift` failed → `dotnet ef migrations has-pending-model-changes`
  exited non-zero for one or more `context × provider` matrix legs. That exit
  code does **not** uniquely mean "the model drifted"; the same non-zero
  status is also returned for EF Core tooling, design-time context, provider
  loading, or restore/build failures. Inspect the failing leg's `dotnet ef`
  output in the job log: if it reports pending model changes, regenerate the
  affected migration by running (from `src/`, one invocation per affected
  `context × provider` pair):

  ```bash
  DB_PROVIDER=<postgres|sqlserver> dotnet ef migrations add <PascalCaseName> \
    --project ./migrations/<MigrationsProject> \
    --startup-project ./migrations/<MigrationsProject> \
    --context <AppDbContext|SlicerDbContext>
  ```

  where `<MigrationsProject>` is one of `Farm.Migrations.PostgreSQL`,
  `Farm.Migrations.SqlServer`, `Farm.Slicer.Migrations.PostgreSQL`, or
  `Farm.Slicer.Migrations.SqlServer` — the matrix leg's `MATRIX_PROJECT`
  value in the failing job log identifies which one. `AppDbContext` pairs
  with the two `Farm.Migrations.*` projects; `SlicerDbContext` pairs with
  the two `Farm.Slicer.Migrations.*` projects. Commit the generated files
  under `src/migrations/<MigrationsProject>/Migrations/` alongside the
  model change. If instead the log reports a tool / design-time / provider
  / build error, fix that — no new migration is needed.

## Extending

- **New test project**: add an entry to the checked manifest
  `scripts/ci/dotnet-test-manifest.json` (`name`, `productionProject`,
  `testProject`, `pathPrefixes`, `dependsOnProjects`, `defaultFilter`,
  `shards`, `requiresProviders`, `runIntegration`, `leg`) and (as needed)
  the classification map in `select-dotnet-tests.sh`, then add a matching
  test case in the selector suite. Add the project's direct-upload step to
  `dotnet-build`; consumers derive the matching archive name from
  `matrix.project`. Add the project to `farm-web.sln` when appropriate, but
  CI also supports required projects that intentionally live outside the
  solution. Run
  `bash scripts/ci/tests/test-dotnet-test-manifest.sh` to confirm the
  manifest still registers every `*.Tests.csproj` on disk exactly once (and,
  for `Farm.Web.Api.Tests`, that its `shards` remain exhaustive, mutually
  exclusive, non-empty, and that each xUnit test source is covered by its
  owning shard's `FullyQualifiedName` filter) before opening a PR — the same
  check also runs in the `ci-tools` job.
- **New bucket**: extend `classify_path()` and add a case in the selector suite.
- **New full-safe trigger**: extend the trigger switch in `main()` of the
  selector and add a case.
- **Docker/external-service opt-in**: consider a separate workflow triggered
  by `workflow_dispatch` rather than expanding this one.

### Test-project manifest

`scripts/ci/select-dotnet-tests.sh` loads its `ALL_TEST_PROJECTS` list from
`scripts/ci/dotnet-test-manifest.json` at startup (via a `python3`/`python`
loader; override the path with `TEST_MANIFEST_PATH` for testing) instead of a
hardcoded array, so there is exactly one checked source of truth per test
project. The manifest is fail-closed: a missing file, invalid JSON, or an
empty `testProjects` list all exit non-zero (rc=3) rather than silently
producing an empty test matrix.

Per-entry fields:

| Field | Meaning |
| --- | --- |
| `name` | Test-project identifier used to select a manifest entry; emitted shard legs derive unique names from it. |
| `productionProject` | The `.csproj` under `src/` whose changes this test project primarily covers. Documentation only. |
| `testProject` | Path to the test `.csproj`, relative to `src/`. Consumed by the selector and the CI matrix. |
| `pathPrefixes` | Repo paths whose changes should select this project. Documents the existing `classify_path()` bucket mapping; not re-interpreted at runtime — see the bucket table above for the authoritative mapping. |
| `dependsOnProjects` | Additional production paths this test project's coverage depends on (declarative documentation, validated by eye, not enforced). |
| `defaultFilter` | The `dotnet test --filter` expression used by the ordinary PR gate. |
| `shards` | Optional `{name, namespacePrefixes, filter}` partitions. Each shard becomes a unique matrix leg while retaining the same `matrix.project`, so all shards consume one compiled project artifact. |
| `requiresProviders` | Non-empty only for projects with `DbHeavy`/`Docker`-tagged tests exercised by the separate `dotnet-test-providers` job (e.g. `["postgres", "sqlserver"]`). |
| `runIntegration` | `true` only for `Farm.Web.IntegrationTests`; passed through as `-p:RunIntegrationTests=true`. |
| `leg` | Base CI matrix-leg name. Shards add a unique suffix while `matrix.project` remains the stable build-artifact identity. `CI summary` remains the only aggregate required check. |

`Farm.Moonraker.Emulator.Tests` and `Farm.Slicer.ProfileParsing.Tests` are
registered in the manifest (see #2022) but have no dedicated bucket in
`classify_path()` — a change to either project's own directory only
currently reaches them via the `tests_other` full-safe fallback, and a
change to their production dependencies does not scope-select them. This is
intentionally unchanged by the manifest; giving them a dedicated bucket is a
possible future improvement, not part of this file's job.

Validate the manifest itself with:

```bash
bash scripts/ci/tests/test-dotnet-test-manifest.sh
```

The validator's own logic (duplicate-`testProject`-path detection, full
schema enforcement, fail-closed behavior on a crashed reader) has its own
regression suite, run against mutated copies of the real manifest:

```bash
bash scripts/ci/tests/test-dotnet-test-manifest-checks.sh
```
