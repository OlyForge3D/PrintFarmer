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
  D --> E[dotnet-test matrix]
  D --> F[migration-drift]
  B --> G[ci-tools]
  B -->|dotnet inputs or full-safe| I[dependency-compliance]
  C --> H[summary]
  E --> H
  F --> H
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
| `dotnet-build`          | any .NET input changed OR full-safe                          | `dotnet restore && dotnet build` on the whole solution.               |
| `migration-drift`       | App or Slicer schema-relevant inputs changed OR full-safe    | `has-pending-model-changes` per provider (App×Pg/SqlServer, Slicer×Pg/SqlServer). |
| `dotnet-test`           | .NET test-relevant inputs changed OR full-safe               | **Matrix** — one leg per affected test project.                       |
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
| `infra`: `src/infra/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Slicer.Module.Tests`, `Farm.OrcaSlicer.Worker.Tests`, `Farm.Web.IntegrationTests`, `Farm.Modules.SmartPlug.Tests`, `Farm.Modules.PrintQueue.Tests` | `AppPg`, `AppSqlServer` | |
| `backend_core`: `src/backends/Farm.Backend.Plugin.Core/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Slicer.Module.Tests`, `Farm.OrcaSlicer.Worker.Tests`, `Farm.Web.IntegrationTests` | | |
| `backend_plugin`: every other `src/backends/**` path (concrete plugin projects) | | ✓ | `Farm.Web.Api.Tests`, `Farm.Web.IntegrationTests`, `Farm.Modules.PrintQueue.Tests` | | |
| `slicer`: `src/slicer/**`, `src/Slicers/**`, `src/worker-shared/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Slicer.Module.Tests`, `Farm.OrcaSlicer.Worker.Tests`, `Farm.Web.IntegrationTests`, `Farm.Modules.PrintQueue.Tests` | `SlicerPg`, `SlicerSqlServer` | |
| `orca_worker`: `src/orcaslicer-worker/**` | | ✓ | `Farm.OrcaSlicer.Worker.Tests` | | |
| `smartplug`: `src/modules/Farm.Modules.SmartPlug/**` | | ✓ | `Farm.Modules.SmartPlug.Tests`, `Farm.Web.Api.Tests` | | |
| `printqueue`: `src/modules/Farm.Modules.PrintQueue/**` | | ✓ | `Farm.Modules.PrintQueue.Tests`, `Farm.Web.Api.Tests` | | |
| `migrations_app`: `src/migrations/Farm.Migrations.*/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Web.IntegrationTests` | `AppPg`, `AppSqlServer` | |
| `migrations_slcr`: `src/migrations/Farm.Slicer.Migrations.*/**` | | ✓ | `Farm.Web.Api.Tests`, `Farm.Slicer.Module.Tests`, `Farm.Web.IntegrationTests` | `SlicerPg`, `SlicerSqlServer` | |
| `tests_api`: `src/tests/Farm.Web.Api.Tests/**` | | ✓ | `Farm.Web.Api.Tests` | | |
| `tests_slicer`: `src/tests/Farm.Slicer.Module.Tests/**` | | ✓ | `Farm.Slicer.Module.Tests` | | |
| `tests_orca`: `src/tests/Farm.OrcaSlicer.Worker.Tests/**` | | ✓ | `Farm.OrcaSlicer.Worker.Tests` | | |
| `tests_smartplug`: `src/tests/Farm.Modules.SmartPlug.Tests/**` | | ✓ | `Farm.Modules.SmartPlug.Tests` | | |
| `tests_printqueue`: `src/tests/Farm.Modules.PrintQueue.Tests/**` | | ✓ | `Farm.Modules.PrintQueue.Tests` | | |
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

`printqueue` (issue #2040, Phase 12) follows the same controller-owning
pattern as `smartplug`: `PrintJobManagementService` and its 8 dependent
controllers (including `SlicePrintBridgeController`) moved into
`Farm.Modules.PrintQueue`, but the `Dispatch/` `CustomWebApplicationFactory`
integration suite and `RouteTableSnapshotTests` intentionally stayed behind
in `Farm.Web.Api.Tests`, so `printqueue` also selects it. Unlike
`smartplug`, `Farm.Modules.PrintQueue` also references `Farm.Slicer.Module`
directly (`SlicePrintBridgeController` consumes `IArtifactsService`/
`ISliceJobRepository`) and its test project references
`Farm.Backend.Plugin.OctoPrint` directly (`PrintJobManagementService`
`History`-seeding tests), so the `slicer` and `backend_plugin` rows above
also list `Farm.Modules.PrintQueue.Tests` as a dependent.

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
`farm-web.sln`. The selector emits `run_integration=true` for that leg and the
workflow passes `-p:RunIntegrationTests=true` during restore, build, and test so
the project compiles and executes its tests instead of disabling itself.

Each matrix leg also carries a `filter` field. The default PR gate uses
`Category!=DbHeavy&Category!=Docker`, and `dotnet-test` passes that value through
to `dotnet test --filter` so the selector and workflow stay aligned without re-
encoding the same category rule in one branch only. This keeps the ordinary PR
path narrow while leaving provider-heavy `DbHeavy` / `Docker` runs to the
separate out-of-band provider job and the fail-closed full-safe matrix.

### Exclusions

- Docker- and external-service-tagged test categories are excluded from ordinary
  PR CI. Reintroduce them by setting `FORCE_FULL_SAFE=1` on the run or opening a
  dedicated on-demand workflow.

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

Baselines are approximate and depend on runner load, warm caches, and PR size.

| Scenario                                             | Before (single job)          | After (this workflow)          |
| ---------------------------------------------------- | ---------------------------- | ------------------------------ |
| React-only PR                                        | ~20-30 min (built everything) | ~2-3 min (frontend only)       |
| Docs-only PR                                         | ~20-30 min                    | ~1-2 min (select + ci-tools + summary) |
| Single-project .NET change (api or slicer only)      | ~20-30 min                    | ~8-12 min (build + 1 test leg) |
| Two-project .NET change (both test projects)         | ~20-30 min                    | ~10-14 min (build + 2 test legs parallel) |
| Shared config / selector / unknown-path change       | ~20-30 min                    | ~20-30 min (full-safe)         |
| Push to `main` / `development`                       | ~20-30 min                    | ~20-30 min (full-safe)         |

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
- `dotnet-test` matrix leg failed → per-project `TestResults/*.trx` is uploaded
  as `dotnet-test-results-<project>` artifact. Download and inspect. The
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
  test case in the selector suite. Add it to `farm-web.sln` when
  appropriate, but CI also supports required projects that intentionally
  live outside the solution. Run
  `bash scripts/ci/tests/test-dotnet-test-manifest.sh` to confirm the
  manifest still registers every `*.Tests.csproj` on disk exactly once (and,
  for `Farm.Web.Api.Tests`, that its `shards` remain exhaustive, mutually
  exclusive, and non-empty) before opening a PR — the same check also runs
  in the `ci-tools` job.
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
| `name` | Matrix leg/test-project identifier, matched by literal string in `classify_path()`/`main()`. |
| `productionProject` | The `.csproj` under `src/` whose changes this test project primarily covers. Documentation only. |
| `testProject` | Path to the test `.csproj`, relative to `src/`. Consumed by the selector and the CI matrix. |
| `pathPrefixes` | Repo paths whose changes should select this project. Documents the existing `classify_path()` bucket mapping; not re-interpreted at runtime — see the bucket table above for the authoritative mapping. |
| `dependsOnProjects` | Additional production paths this test project's coverage depends on (declarative documentation, validated by eye, not enforced). |
| `defaultFilter` | The `dotnet test --filter` expression used by the ordinary PR gate. |
| `shards` | Optional `{name, namespacePrefixes, filter}` partitions of a large test project's namespaces, for a possible future parallel-shard matrix. Declared and validated (exhaustive, mutually exclusive, non-empty) but **not** wired into `ci.yml` execution today — adding shard-level matrix legs would multiply required checks, which is out of scope; `CI summary` continues to be the only aggregate required check. |
| `requiresProviders` | Non-empty only for projects with `DbHeavy`/`Docker`-tagged tests exercised by the separate `dotnet-test-providers` job (e.g. `["postgres", "sqlserver"]`). |
| `runIntegration` | `true` only for `Farm.Web.IntegrationTests`; passed through as `-p:RunIntegrationTests=true`. |
| `leg` | CI matrix leg the project's tests execute in. Several test projects (or shards of the same project) may share one `leg` without adding a new required check — this decouples test-assembly count from CI-leg count. |

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

