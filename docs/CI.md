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
  B -->|paths-only frontend| C[frontend job]
  B -->|any dotnet input| D[dotnet-build full sln]
  D --> E[dotnet-test matrix]
  D --> F[migration-drift]
  B --> G[ci-tools]
  C --> H[summary]
  E --> H
  F --> H
  G --> H
```

Every PR triggers the `select` job, which classifies changed paths and emits
outputs consumed by downstream jobs. This produces a required, stable check
name even when no downstream work runs (e.g. docs-only or React-only PRs).

## Jobs

| Job              | Runs when                                                    | Notes                                                                 |
| ---------------- | ------------------------------------------------------------ | --------------------------------------------------------------------- |
| `select`         | always                                                       | Classifies changed paths; emits `want_*`, `matrix`, `mig_matrix`.     |
| `ci-tools`       | always                                                       | Runs `bash -n` + selector + hook tests; gates changes to selector.    |
| `frontend`       | React inputs changed OR full-safe                            | `npm ci`, lint, test:run, build in `src/Web/ReactApp/`.               |
| `dotnet-build`   | any .NET input changed OR full-safe                          | `dotnet restore && dotnet build` on the whole solution.               |
| `migration-drift`| App or Slicer schema-relevant inputs changed OR full-safe    | `has-pending-model-changes` per provider (App×Pg/SqlServer, Slicer×Pg/SqlServer). |
| `dotnet-test`    | .NET test-relevant inputs changed OR full-safe               | **Matrix** — one leg per affected test project.                       |
| `summary`        | always (`if: always()`)                                      | Aggregates gates; hard-fails on required check regression.            |

## Selection logic (selector script)

`scripts/ci/select-dotnet-tests.sh` reads either:

- `CHANGED_FILES_FROM_Z`: path to a NUL-terminated file list (preferred), or
- `CHANGED_FILES`: newline-separated list (used by the workflow's fallback path).

It classifies each path into one of the buckets below and emits selection
outputs on `$GITHUB_OUTPUT`. `--no-renames` is passed on every `git diff`
invocation so that renames decompose to add+delete pairs — both endpoints
classify.

### Bucket → downstream mapping

| Bucket                                        | want_frontend | want_dotnet_build | want_dotnet_test | want_mig_drift | full_matrix | Notes                              |
| --------------------------------------------- | :-----------: | :---------------: | :--------------: | :------------: | :---------: | ---------------------------------- |
| `frontend` (`src/Web/ReactApp/**`)            | ✓             |                   |                  |                |             | React-only PRs run zero .NET.      |
| `api` (`src/api/**`)                          |               | ✓                 | Api.Tests, Slicer.Tests (both ref api) | App drift ✓  |             |                                    |
| `infra` (`src/infra/**`, `src/config/**`)     |               | ✓                 | Api.Tests, Slicer.Tests               |                |             | Both test projects ref infra.      |
| `backends` (`src/backends/**`)                |               | ✓                 | Api.Tests only                        |                |             | Slicer.Tests does not ref backends.|
| `slicer` (`src/slicer/**`, `src/Slicers/**`, `src/worker-shared/**`) |    | ✓ | Api.Tests, Slicer.Tests | Slicer drift ✓ |             |                                    |
| `migrations_app` (`src/migrations/Farm.Migrations.*/**`)             |    | ✓ | Api.Tests, Slicer.Tests | App drift ✓  |             |                                    |
| `migrations_slcr` (`src/migrations/Farm.Slicer.Migrations.*/**`)     |    | ✓ | Slicer.Tests            | Slicer drift ✓ |             |                                    |
| `tests_api` (`src/tests/Farm.Web.Api.Tests/**`)      |               | ✓                 | Api.Tests             |                |             |                                    |
| `tests_slicer` (`src/tests/Farm.Slicer.Module.Tests/**`) |            | ✓                 | Slicer.Tests          |                |             |                                    |
| `tests_other` (`src/tests/Farm.Web.IntegrationTests/**`, `src/tests/Farm.OrcaSlicer.Worker.Tests/**`) |    | ✓ | full matrix ✓        | full ✓          | ✓          | These test projects are not in the current sln; treat as full-safe rather than silently drop. |
| `orca_worker` (`src/orcaslicer-worker/**`), `discovery` (`src/discovery/**`), `settings` (`src/settings/**`) |    | ✓ | full ✓                | full ✓          | ✓          | Foundational or non-sln; full-safe. |
| `shared_config` (Directory.Build.*, .editorconfig, farm-web.sln, global.json, NuGet.Config, Directory.Packages.props) |   | ✓ | full ✓                | full ✓          | ✓          | Config/graph inputs affect everything. |
| `ci_selector` (workflows/.githooks/scripts/ci changes)               |    | ✓                 | full ✓                | full ✓          | ✓          | CI/selector edits verify themselves against everything. |
| `unknown_src` (`src/**` didn't match any bucket)                     |    | ✓                 | full ✓                | full ✓          | ✓          | Fail-safe.                          |
| `docs_only`, `mobile`, `tools_only`                                  |    |                   |                       |                 |             | Nothing changes downstream.         |

### Full-safe (`full_matrix=1`) triggers

- Any of: `shared_config`, `ci_selector`, `unknown_src`, `discovery`, `settings`, `orca_worker`, `tests_other`, `devcontainer`.
- `workflow_dispatch` event.
- `push` to `main` or `development`.
- Caller sets `FORCE_FULL_SAFE=1`.
- NUL-parse failure of the `_Z` file.
- Git-quoted path detected in newline-form input (non-ASCII name → forces full-safe).

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

### Cache

Successful verifications are stamped at:

```
$(git rev-parse --git-common-dir)/pre-push-fmt-cache/<key>
```

where `<key>` is:

```
sha256(
  "pre-push-format-v1" ||
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
copy. The authoritative gate is the required CI check on the pull request.
Branch protection on `main`/`development` — not the hook — is what actually
enforces formatting for merges.

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
| Docs-only PR                                         | ~20-30 min                    | ~30 s (select + summary only)  |
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
- `dotnet-test` matrix leg failed → per-project `TestResults/*.trx` is uploaded
  as `trx-<project>` artifact. Download and inspect. The workflow also asserts
  that the TRX reports non-zero executed tests, so an empty test run is a hard
  failure rather than a silent pass.
- `migration-drift` failed → the model on your branch drifted from the last
  migration. Regenerate migrations per [Migrations](../src/migrations/README.md)
  and commit them.

## Extending

- **New test project**: add it to `farm-web.sln`, add a row to `ALL_TEST_PROJECTS`
  and (as needed) the classification map in `select-dotnet-tests.sh`, then add
  a matching test case in the selector suite.
- **New bucket**: extend `classify_path()` and add a case in the selector suite.
- **New full-safe trigger**: extend the trigger switch in `main()` of the
  selector and add a case.
- **Docker/external-service opt-in**: consider a separate workflow triggered
  by `workflow_dispatch` rather than expanding this one.
