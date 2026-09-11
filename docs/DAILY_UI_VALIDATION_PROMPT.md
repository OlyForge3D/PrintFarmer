Run daily full-stack UI validation for OlyForge3D/PrintFarmer. Execute the runner,
analyze real failures, track issues, report, and clean up. Do not implement fixes,
dispatch implementation sessions, create process-tracking files, or rebuild images.

## Obtain the reviewed harness

Windows is orchestration only. In PowerShell 7, obtain this immutable harness
revision in its own cache, never in the application's checkout:

```powershell
$sha = '@@HARNESS_REVISION@@'
$harness = Join-Path $env:LOCALAPPDATA "PrintFarmer\DailyValidation\harness\$sha"
if (-not (Test-Path (Join-Path $harness '.git'))) {
    New-Item -ItemType Directory -Force $harness | Out-Null
    git -C $harness init --quiet
    if ($LASTEXITCODE) { throw 'Harness init failed' }
    git -C $harness config core.autocrlf false
    git -C $harness sparse-checkout set --no-cone /scripts/ci/daily-validation.ps1 /scripts/ci/wsl-transport.ps1 /scripts/ci/daily-validation.py /scripts/ci/daily-validation-reporter.mjs
    if ($LASTEXITCODE) { throw 'Harness sparse checkout failed' }
    git -C $harness fetch --depth 1 https://github.com/OlyForge3D/PrintFarmer.git $sha
    if ($LASTEXITCODE) { throw 'Reviewed harness fetch failed' }
    git -C $harness checkout --detach FETCH_HEAD
    if ($LASTEXITCODE) { throw 'Harness checkout failed' }
}
if ((git -C $harness rev-parse HEAD).Trim() -ne $sha) { throw 'Wrong harness revision' }
if (git -C $harness status --porcelain --untracked-files=no) { throw 'Modified harness' }
$runner = Join-Path $harness 'scripts\ci\daily-validation.ps1'
& $runner -Command init
```

Record the returned validation ID immediately. One `init` only: it selects the
latest successful `daily-development-images.yml` run on `development`, verifies
the atomic six-image `daily-development-image-set` manifest, checks out exactly
its commit, prepares the existing deployment and dependencies, and records
cleanup intent before resources. The tested commit and reviewed harness revision
are deliberately different identities. Never fetch a newer application commit,
mix manifests, regenerate secrets, or substitute a local build.

## Run and resume

All real work and evidence live on Ubuntu-24.04's native ext4 filesystem. The
launcher uses a login shell and probes `/usr/bin/docker` package ownership,
native service, client/server and Compose. Do not modify the host, start Docker
Desktop, use `/mnt/*` for runtime work, or improvise shell quoting. A missing
prerequisite is an infrastructure blocker, not a product defect.

Use only these entrypoints with the SAME validation ID:

```powershell
& $runner -Command deploy -RunId <validation-id>
& $runner -Command phase-a -RunId <validation-id>
& $runner -Command phase-b -RunId <validation-id>
& $runner -Command status -RunId <validation-id>
& $runner -Command read -RunId <validation-id> -Evidence summary.json
& $runner -Command cleanup -RunId <validation-id>
```

Run A then B synchronously on the identical healthy stack. You may delegate each
long phase to a synchronous general-purpose task agent given the exact runner
path, validation ID, recorded state, and command; it must return compact fresh
evidence, not improvise deployment. The commands preserve:

- A: `npm run test:e2e:moonraker -- --project=chromium`
  (Moonraker grep and workers=1 unchanged).
- B: `npm run test:e2e:emulator -- --project=chromium --workers=1 --grep-invert Moonraker`.

The harness imports the tested Playwright configuration but disables its local
webServer; it never starts Vite. UI, API and four emulator endpoints come from the
run's persisted ports. Required topology is PostgreSQL microservices, distributed
slicing, exactly one Orca worker, deterministic discovery and actual Moonraker
emulators. The two deployment documents copied from the tested commit are
authoritative. Read Linux evidence ONLY through `read` or explicit WSL tools,
never Windows view/glob on `/home` or `/tmp`.

Read `phase-a.json`, `phase-b.json`, phase logs, `health-logs.log`, and
`summary.json` through the runner. Only verified invocation IDs, manifest/commit/
harness bindings, timestamps, exit codes and structured results count. Missing
execution means not invoked/did-not-run, NEVER zero failures or a pass. Keep
passed, failed, explicitly skipped and did-not-run/interrupted distinct; include
retries/flaky results. Historical global state/logs are not evidence.

Do not rerun completed phases. Resume only via `status` proving the same healthy
resources and configuration, then the next uninvoked phase. There is one setup,
one deployment and one invocation per phase; interrupted setup/phase requires
cleanup and a BLOCKED/INCOMPLETE report, not recloning or rediscovery. Health waits
and commands have deadlines. Cleanup permits two total attempts. Do not loop.
After a test failure, run B only if A has verified meaningful execution and
prerequisites remain healthy. A harness/fixture/health blocker ends testing.

## Classify and track

For each failure, examine assertion, source/fixture at the tested commit,
trace/screenshot and browser console/network or API evidence. Assign exactly one:
`confirmed product defect`, `test defect`,
`missing deterministic fixture/unsupported coverage`, `infrastructure failure`.
Do not infer a product defect from a failed harness command.

Search existing open issues before filing; update duplicates. Product bugs need
reproduction, failing test/phase/assertion, tested commit/digest, browser/viewport
and evidence. Test defects and missing coverage need `testing`, plus `area:ci`
when relevant. Use existing `squad:*` routing and priority labels as appropriate;
infrastructure preventing meaningful execution is P0/blocker handling.
Every reproducible B failure and every missing-fixture/unsupported skip must
have an issue number. If filing is blocked, report that blocker.

Enumerate each explicit skip's names/count/reason and whether intentional or a
coverage gap; enumerate did-not-run/interrupted groups with their causal failure.
Private browser traces may contain credentials. Inspect/redact before attaching
anything to this PUBLIC repository; do not publish raw artifacts, credentials,
tokens or internal hostnames. Sanitized run JSON/logs still require human/agent
inspection before publication.

## Cleanup and report

Always invoke `cleanup` in a finally path, including after failure/interruption.
It uses the recorded complete Compose file/env set, verifies ownership, tears
down only this run and removes its exact runtime. It retains run provenance,
sanitized logs/results and private browser evidence. Do not delete other runs,
global leftovers, shared images or caches. Report actual `cleanup.status`;
failure is a blocker, not a successful teardown. Preserve evidence for issues.

Use exactly one first-line outcome (substitute the tested commit):

- `✅ VALIDATION PASSED @ <commit>` only if deployment and A passed, B has no
  failures/did-not-run, no skips represent missing fixtures/coverage, and every
  intentional skip has a reason.
- `⚠️ VALIDATION PASSED WITH COVERAGE GAPS @ <commit>` if A passed and B has any
  test-defect or missing-fixture/unsupported failure/skip, or did-not-run/
  interrupted tests. Gaps must be visible and tracked.
- `🐛 VALIDATION FOUND DEFECTS @ <commit>` if A finds a reproducible defect or a
  B failure is confirmed as a real product defect; a clean A is not converted
  to this outcome by unclassified B failures.
- `🚫 VALIDATION BLOCKED: <reason>` if deployment or infrastructure prevents A
  or meaningful B, or cleanup fails.
- `⛔ VALIDATION INCOMPLETE: <what was reached>` only if the session runs out
  of room; name remaining work.

Then report environment versions; validation ID/evidence directory; image run,
manifest hash, commit and six digests; harness revision/hash; topology/health;
A and B counts, failures and flaky attempts; each B classification/evidence/issue;
all skip and did-not-run groups/reasons/issues; issues updated/filed; exactly what
was not tested; and actual cleanup status. Never plain-pass missing execution,
unexplained failures, collapsed categories or untracked gaps. Exit after reporting.
