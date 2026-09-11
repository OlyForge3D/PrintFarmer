## Daily UI validation runner

`scripts/ci/daily-validation.ps1` is the Windows orchestration entrypoint for
native Ubuntu-24.04 WSL validation. Use PowerShell 7. The runner requires native
Docker Engine and its Compose plugin, Python 3 with `ruamel.yaml`, Node/npm,
GitHub CLI authentication, git, jq, openssl and envsubst inside WSL.
It reports installed versions rather than assuming a permanently supported
version. It does not install system packages, start services or use Docker Desktop.

```powershell
.\scripts\ci\daily-validation.ps1 -Command probe
.\scripts\ci\daily-validation.ps1 -Command run
```

`run` performs preparation, deployment, A, B, and mandatory teardown. Test failures
retain their child exit code; setup/health/evidence/cleanup failures return
nonzero with a redacted blocker. The summary is evidence, not a product-defect
classification or a claim that assertions passed.

For agent-led investigation, use `init`, record its ID, then `deploy`, `phase-a`,
`phase-b`, `status`, `read` and finally `cleanup`, all with `-RunId <id>`.
The saved [automation prompt](DAILY_UI_VALIDATION_PROMPT.md) defines the exact
classification, issue and final report contract. A failed preparation/deployment/
phase precondition automatically attempts cleanup; separate completed test
failures remain available for B and analysis until explicit cleanup.

### Ownership and provenance

The WSL login-shell transport sends base64 JSON over stdin, not interpolated shell
arguments. Literal dollars, quotes, spaces, Unicode and multiline arguments
survive. Linux files are read via `-Command read -Evidence <filename>`; do not use
Windows filesystem tools on POSIX paths.

Each run owns `~/.local/share/printfarmer-daily/runs/<validation-id>/runtime` on
ext4. `state.json` and evidence live in its private parent, outside the disposable
runtime. A cleanup intent is persisted before any resource is created. State
records workflow run, original manifest/hash, all six digest references, tested
commit, harness revision/content hash/dirty flag, exact source/frontend paths,
ordered Compose files, secret-env hash, configuration hashes, port leases,
step attempts, health/container identities and phase invocations.

Secrets are random per run, saved separately in `runtime/secrets.json` and
`runtime/compose.env` with mode 0600 under private directories. Every new process
reloads them; every Compose command uses the same explicit env file, project
and ordered file list. Ambient Compose/image/port variables cannot change them.
No secrets or resolved Compose environment are printed.

The existing tested-commit Compose generator, daily registry/validation overlays,
certificate helper, worker-temp helper and acceptance provenance verifier are
reused. A final run-owned overlay labels every service and uniquely names/labels
all networks and named volumes, including the otherwise fixed validation network
and Orca custom profiles. It does not replace service network aliases.
All published ports must be loopback-only and leased to this run. An external
process winning the final port-bind race fails deployment; it never triggers a
silent port/source/image substitution.

The harness verifies the exact topology, pinned application images, healthy
containers, frontend/API build provenance, real admin login/reset, Moonraker and
offline fixtures, deterministic discovery, and the single worker's writable temp.
No local image build is permitted. Upstream PostgreSQL/nginx retain the tested
deployment's versions; their actual container image IDs are recorded as well.

### Playwright evidence and recovery

The tested checkout stays unchanged. An external config imports and spreads its
Playwright config, preserving fixtures/projects/timeouts/retry policy/reporters,
and changes only `webServer` (disabled), absolute test/output paths and an added
structured reporter. `npm ci` uses that checkout's lockfile; `playwright install
chromium` installs only its browser into this run, without system `--with-deps`.
No Vite process or locally rebuilt frontend can substitute for the pinned image.

Each phase has one fresh invocation ID, start/end times, command and real exit
code. Structured results bind those to validation ID, commit, manifest hash and
harness hash. Missing/inconsistent/stale/zero-test reports fail closed. Explicit
skip/fixme annotations are distinct from serial omissions and interruptions.
Attempts, expected status, flaky outcomes, errors and artifact references remain
available. A healthy unchanged deployment is required before and after phases.

Command execution captures stdout/stderr once into phase-specific redacted logs
and returns the actual child exit status (there is no tee pipeline to mask it).
Operations have finite timeouts; the full topology wait permits 60 observations.
One preparation, deployment and invocation per phase are allowed. Completed
steps are not replayed. Resume checks configuration and checkout hashes plus
running container IDs/start times; restarts/replacements invalidate previous
phase evidence. An interrupted command cannot be relabeled complete.

`cleanup` acquires the run lock, rejects foreign labels, checks configuration
hashes, runs down with volumes, and verifies absence of owned resources. For
root-owned bind content, a named, labeled, network-disabled helper using the
already selected API digest may repair ownership of this exact stack directory.
It never uses a mutable helper image or cleans shared/global resources.
Teardown errors remain visible, retain runtime for investigation, and allow only
one further cleanup attempt. A hard process/host kill cannot execute a finally
handler: use the recorded ID and original harness to run cleanup after recovery.

Sanitized results/logs and copied tested-commit deployment documents remain after
runtime deletion. Raw browser traces/screenshots are kept separately in private
evidence and MUST be reviewed/redacted before public attachment. Retention is
deliberate; this runner does not sweep other runs or expire issue evidence.

### Saved automation activation

The canonical prompt contains `@@HARNESS_REVISION@@`. After review, replace that
token with the full reviewed, remotely reachable commit SHA and update ONLY the
existing automation's prompt. Preserve its ID, daily 06:00 local schedule,
enabled state, host, model and other settings. Do not activate an unreviewed
revision or a prompt expecting unmerged files on the application's checkout.

The prompt's bootstrap fetches the reviewed immutable harness separately into a
Windows sparse-checkout cache, verifies HEAD and tracked-file cleanliness, and
then launches it. The application checkout is independently fetched inside WSL
at the image manifest commit. Publishing a reviewed feature-branch commit makes
the harness available without merging/changing the application under test.
Verify the bootstrap fetch and rendered prompt before updating the saved task;
read it back and compare against the canonical rendered content.

### Focused regression commands

```powershell
.\scripts\ci\tests\test-wsl-transport.ps1
```

```bash
# From a repository checkout inside native WSL:
node --test scripts/ci/tests/test-daily-validation.mjs
python3 scripts/ci/tests/test_daily_validation.py
```

The Node test invokes the Python regressions; the second command is useful for
focused diagnostics, not a required duplicate run. Native Linux lifecycle tests
are explicitly skipped on Windows. Include native WSL results in validation
evidence, not just the Windows-only subset.
