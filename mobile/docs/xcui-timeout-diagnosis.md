## XCUI timeout diagnosis (#2573)

### Historical evidence

The retained `/tmp/gorman-2519-test.log` lines 4740-4742 recover the exact
duration-to-test mapping reported in [#2535](https://github.com/OlyForge3D/PrintFarmer/pull/2535)
and the [corrected breakdown](https://github.com/OlyForge3D/PrintFarmer/issues/2536#issuecomment-5573669887).
The original result bundle named at line 4667 is no longer present. That log
contains test summaries, not the failing XCUI activity, source location, or
diagnostic stacks. None of those missing details is inferred below.

| Duration | Full test identity (`PrintFarmerUITests/`) | Historical failure site/message | Runtime/toolchain | Evidence | Classification | Disposition |
| --- | --- | --- | --- | --- | --- | --- |
| 339.100s | `ShiftTasksUITests/testP7DismissClearsOnlyCurrentErrorWithoutTaskMutation` | Unavailable; reported query timeout | iPhone 17 clone 3, iOS 27 beta, Xcode 26.6 per source PR | [Retained excerpt](#retained-historical-excerpt) | Unresolved | [Owned follow-up #2577](https://github.com/OlyForge3D/PrintFarmer/issues/2577) |
| 337.731s | `ShiftTasksFailedRefreshUITests/testFailedStateHostsRefreshableScrollContainerAndRecoversCanonically` | Unavailable; reported query timeout | iPhone 17 clone 5, same source run | [Retained excerpt](#retained-historical-excerpt) | Unresolved | [Owned follow-up #2578](https://github.com/OlyForge3D/PrintFarmer/issues/2578) |
| 314.184s | `OperatorShellUITests/testAdvancedControlsGatedBehindPrinterDetail` | Unavailable; reported query timeout | iPhone 17 clone 1, same source run | [Retained excerpt](#retained-historical-excerpt) | Unresolved | [Owned follow-up #2579](https://github.com/OlyForge3D/PrintFarmer/issues/2579) |

Neighboring *passing* tests took 277-323 seconds in the same parallel run. This is
consistent with shared runner/runtime trouble, but neither establishes contention
nor rules out an app main-thread stall. Without the missing activities/stacks,
the historical cases cannot be classified as infrastructure defects.

### Retained historical excerpt

Verbatim summaries from `/tmp/gorman-2519-test.log`, preserved as
`historical-gorman-2519-test.log` in the artifact directory below:

```text
Test case 'ShiftTasksUITests.testP7DismissClearsOnlyCurrentErrorWithoutTaskMutation()' failed on 'Clone 3 of iPhone 17 - PrintFarmerUITests-Runner (80495)' (339.100 seconds)
Test case 'ShiftTasksFailedRefreshUITests.testFailedStateHostsRefreshableScrollContainerAndRecoversCanonically()' failed on 'Clone 5 of iPhone 17 - PrintFarmerUITests-Runner (82525)' (337.731 seconds)
Test case 'OperatorShellUITests.testAdvancedControlsGatedBehindPrinterDetail()' failed on 'Clone 1 of iPhone 17 - PrintFarmerUITests-Runner (79404)' (314.184 seconds)
```

SHA-256 of the complete retained log:
`24c081539c8dcbebc18c0ebc1fb1ffa573b51b54b07e96092208b783fc7b2f0d`.

### Pinned reproduction

Baseline app/harness: `325f0814b8d190bd3fb332e7b07f90adf76592a1`.
Xcode 26.6 (17F113), iOS 26.5 (23F77), arm64 iPhone 17 simulator
`0C090C35-D689-404B-926C-6C1B5CCE2429`. The device was created for this investigation,
with parallel testing disabled and deterministic `--uitesting` bootstrap unchanged.
The shared timeout plan was added before the successful run; harness logic was unchanged.

| Case | Before harness correction | Interpretation |
| --- | --- | --- |
| P7 dismiss | 13.312s, passed | Historical timeout not reproduced; cause unresolved |
| Failed refresh | 10.690s, passed | Historical timeout not reproduced; cause unresolved |
| Advanced controls | 11.334s, passed | Final safety assertions reached; not an early-return pass; cause unresolved |

All three passed without skips. `before-recovery-2573.log` and
`before-recovery-2573.xcresult` retain the run. The first attempt stopped during
SwiftPM resolution (`safe.bareRepository=explicit`), before tests ran; the recovery
used a process-scoped Git setting only, not a global configuration change.

### Timeout policy

The shared `PrintFarmer.xctestplan` enables genuine XCTest test timeouts for both
Xcode and CI, while retaining Apple's 600-second general allowance. The three
isolated cases use `executionTimeAllowance = 60` at method entry. Other tests are
not blindly capped at 60 seconds. For isolated diagnosis, the CLI default and
maximum are also 60 seconds, bounding setup as well as the test body.

[Apple's allowance semantics](https://developer.apple.com/documentation/xctest/xctestcase/executiontimeallowance)
round upward to a whole minute. A polling deadline cannot interrupt an in-flight
remote accessibility operation; XCTest's watchdog is the separate backstop.
Destination discovery timeouts and Actions step ceilings serve different purposes.

From `mobile/`, use an explicit stable simulator UDID:

```bash
set -o pipefail
xcodebuild test -project PrintFarmer.xcodeproj -scheme PrintFarmer \
  -destination "platform=iOS Simulator,id=$SIMULATOR_UDID" \
  -parallel-testing-enabled NO \
  -test-timeouts-enabled YES \
  -default-test-execution-time-allowance 60 \
  -maximum-test-execution-time-allowance 60 \
  -only-testing:PrintFarmerUITests/ShiftTasksUITests/testP7DismissClearsOnlyCurrentErrorWithoutTaskMutation \
  -resultBundlePath "$RESULTS/P7.xcresult" 2>&1 | tee "$RESULTS/P7.log"
```

Create `$RESULTS` first; result-bundle paths must not already exist. CI uploads
the text logs and `.xcresult` bundles together, including failed runs.

### Diagnostic probes

`QueryTimeoutDiagnosticUITests` is compiled only with
`SWIFT_ACTIVE_COMPILATION_CONDITIONS='$(inherited) PFARM_TIMEOUT_DIAGNOSTICS'`.
Select that class explicitly in a diagnostic build. Both probes intentionally
fail, and a zero exit code is **not** a successful watchdog demonstration.
Normal builds contain neither deliberately failing test; no test is skipped.

The stall probe blocks the runner for 300 seconds and requests a 60-second
allowance. The missing-destination probe exercises the ordinary short query
failure path. It is not an app-hang reproduction.

`diagnostic-before-2573.xcresult` records exactly **60.000 seconds** for the
deliberate stall and the failure text **Test exceeded execution time allowance
of 1 minute**; `xcodebuild` exited 65. This run relied on the shared plan to
enable timeouts, without a CLI enable override. XCTest restarted its runner
after the timeout; that is recovery, not a hidden rerun of the failed test.

The missing-destination case failed normally in 8.239s including app setup.
Its requested composite query allowance was only 2s: the original helper spent
additional time in initial/tab/sidebar waits, then started a fresh polling
deadline and queried again after that deadline. This is a demonstrated harness
budget defect, **not proof that it caused any historical 314-339s stall**.
Combined testing/runner time was 101.334s for 68.239s of reported tests:
33.095s of startup, diagnostic, restart, and teardown overhead. The bundle's
whole invocation (including build) was 124.018s.

The correction shares one monotonic deadline across initial lookup, sidebar
reveal, fallback gesture, and polling. It stops initiating remote operations
after that deadline, including final fallback and failure-description queries.
Diagnostics attach only locally held operation/timing/identifier strings.
Fake-clock regressions cover aggregate budget consumption, in-flight overrun,
and zero-budget behavior. The XCTest watchdog, not this polling logic, bounds
an operation that has already entered a blocking remote accessibility call.

### After-correction evidence

`diagnostic-after-2573.xcresult` again records the deliberately stalled test
failing at **60.000s**. The missing-destination probe fails with the actual
identifier and a local attachment at **2.0038s helper elapsed / 4.078s total**,
without starting another helper query after expiry. The compact full-root
regression passes in 33.271s.

**Process-level timing is not 60s.** This second invocation subsequently logs
`Failure collecting diagnostics from simulator: Timed out after 600.0 seconds
while waiting for a response from the invoked process`. Total testing/runner
time is **731.991s**, versus 97.349s of reported test durations: 634.642s of
startup, diagnostics, restart, and teardown overhead. Whole invocation time
including build is 749.138s. Exit status remains 65. The independent collector
tail is owned by [#2583](https://github.com/OlyForge3D/PrintFarmer/issues/2583);
the 25-minute CI step ceiling remains a separate process backstop.

### Synchronized-head adjacent validation

After committing, fetched and merged `origin/development` at
`0e4a41702302431623cdcb837b2f1a05781d59f0`, producing implementation-validation
head `e5959d0873879fb676ff01e440dff1bd159bfc67`. Both destinations were checked by
the merged shared resolver against iOS 26.5 build 23F77.

| Destination / bundle | Passed | Failed | Skipped | Evidence |
| --- | --- | --- | --- | --- |
| iPhone 17 / `final-iphone-2573.xcresult` | 13 | 0 | 0 | All ShiftTasks cases, original timeout selectors, adjacent account/Attention/root chrome, and all three budget regressions |
| iPad Pro 11-inch (M5) / `final-ipad-2573.xcresult` | 12 | 1 | 0 | Same original timeout/ShiftTasks and budget coverage, sidebar/root chrome; leading-edge gesture assertion fails |
| Unchanged development / `baseline-ipad-gesture-2573.xcresult` | 0 | 1 | 0 | Same leading-edge assertion reproduces without any branch helper changes |

The iPad is `014FF738-9D3B-4261-B00E-D7A1E5B16E33`. Its full SimpleShellChrome
case passed in **63.862s**, demonstrating why a blanket 60-second suite limit
would be incorrect. The isolated three cases retain their scoped 60s allowances.

The iPad failure is
`JobDetailIPadNavigationUITests/testIPadLeadingEdgeRevealKeepsVisibleSidebarOpen`,
line 45: `The leading-edge gesture must reveal the collapsed iPad sidebar`.
Changed source fails in 20.554s; a clean `git archive` export of the unchanged
development commit on the **same device/runtime** fails in 21.212s with the
same assertion. Show Sidebar was present, unlike #2576's startup-splash failure.
Gesture coordinates are unchanged. This separate baseline issue remains owned
and open in [#2582](https://github.com/OlyForge3D/PrintFarmer/issues/2582); the iPad
run is **not green**.

The first after-harness attempt had 9 passes and a new cold-compact enumeration
failure: waiting for a nonexistent iPad toggle consumed the entire shared
budget before rechecking the tab bar. The corrected helper reserves a short
toggle probe within the same deadline. This was fixed, not skipped.
The initial fake-clock class was accidentally nested and undiscovered; it was
moved to top level. Final `.xcresult` trees explicitly contain each case once,
with Passed and a positive execution duration on **both devices**:

- `UIWaitBudgetTests/testCompositeOperationsShareOneDeadline`
- `UIWaitBudgetTests/testInFlightOverrunDoesNotStartAnotherQueryOrDiagnostic`
- `UIWaitBudgetTests/testZeroBudgetNeverStartsRemoteWork`

Shared scheme/plan resolution, both test targets, enabled timeouts, and unchanged
general 600s allowance were also checked from the XML/JSON and with
`xcodebuild -showTestPlans`. No snapshot references, skips, assertions, or
service-retry behavior were relaxed.

The existing `ShiftTasksUITests` CI matrix invocations also select
`UIWaitBudgetTests`, so the new regression class is executed persistently on
both device families rather than merely compiled. No extra matrix jobs were
added. The actual workflow shell body was exercised with a stubbed `xcodebuild`
for ShiftTasks and OperatorShell: suite selection remains intact, only ShiftTasks
gets the budget selector, logs are retained, and exit codes 0 and 42 propagate
through `tee` unchanged.

### Retained artifacts

Local artifacts live outside the worktree, in the Copilot session
`370ca864-1fd0-48e7-87da-c53c9d2938b4/files` under `~/.copilot/session-state/`.
Each named run retains both `.log` and `.xcresult`; this is local evidence, not
a claim that bundles were uploaded to GitHub:

- `before-recovery-2573`: unchanged-harness focused reproduction.
- `diagnostic-before-2573`: original composite helper and watchdog probes.
- `after-fixed-2573`: the disclosed 9-pass / 1-failure intermediate run.
- `diagnostic-after-2573`: corrected missing query, stall, and compact readiness.
- `final-iphone-2573`, `final-ipad-2573`: synchronized-head adjacent runs.
- `baseline-ipad-gesture-2573`: unchanged-source comparison.

Original SwiftPM/build-failure logs and JSON test trees are retained too.
CI publishes text logs alongside result bundles for future diagnosable failures.

### Owned #2578 recovery: fresh causal evidence, gap retained

Scoped to
`ShiftTasksFailedRefreshUITests/testFailedStateHostsRefreshableScrollContainerAndRecoversCanonically`
alone. The historical 337.731s failure identity is mapped in
`/tmp/gorman-2519-test.log` line 4741; the historical xcresult, source location,
and diagnostic stacks are **still absent** and not inferred. The gap is retained
deliberately.

Fresh run at head `d0330781cbb4d1cd28934b7494a83cdd90d0dd19`, Xcode 26.6
(17F113), iOS 26.5 (23F77), `iPhone 15` UDID
`65801085-CA6B-49F5-A6BE-E26729477142`, `-parallel-testing-enabled NO`,
`-test-timeouts-enabled YES`, default and maximum allowance 60s. Bundle
`fresh-failed-refresh-2578.xcresult` and log
`fresh-failed-refresh-2578.log` (SHA-256
`61686290726d15380b08dc070da1d21ae4b6f738c972976650572442782d3006`) are
retained under Copilot session
`afd03518-d0b9-4faa-921e-0229a4feef88/files`.

The test passed in **11.608s**. Each XCUI activity that a stalled runner would
block on is present in the fresh log with its timestamp:

| t (s) | Activity |
| --- | --- |
| 3.90 – 4.95 | Existence probes and tap on `tab.tasks` Button |
| 6.89 – 7.93 | 10s wait for `shiftTasks.load.errorList` ScrollView (satisfied) |
| 7.95 – 7.99 | Existence check of `shiftTasks.load.error` Button and negative check on `shiftTasks.group.now` |
| 8.01 – 8.34 | Tap `shiftTasks.load.error` (Retry) |
| 8.94 – 9.98 | 10s wait for `shiftTasks.group.now` (satisfied) |
| 10.05 – 11.09 | 5s wait for `shiftTasks.load.errorList` non-existence (satisfied) |

Combined with the retained excerpt above, the fresh evidence distinguishes the
failure modes as follows:

- **Not the harness.** The three composite `waitForExistence` calls used by the
  test each satisfy inside their 10s / 5s windows. Their scoped total is well
  under the 60s allowance. The historical 337.731s exceeded every window used by
  this test by more than an order of magnitude, so the test's assertions cannot
  be the timeout source on their own.
- **Not readiness/query.** The `tab.tasks`, `shiftTasks.load.errorList`,
  `shiftTasks.load.error`, and `shiftTasks.group.now` identifiers all resolve
  end-to-end on the pinned runtime with the same
  `--uitesting-shift-task-initial-load-failure` bootstrap. Identifier drift
  would fail here in 10–20s, not 337s.
- **Not app stall (bootstrap).** Launch to first tap completes by 4.95s, and
  the failed-state → recovery transition traverses cleanly at 8.01–9.98s.
- **Consistent with runner/accessibility, not proven.** The retained gorman
  excerpt shows neighboring *passing* tests on the same source parallel run
  taking 277–323s (`LoginFlowUITests.testLoginTransitionsToOperatorShell` at
  277.168s, `ShiftTasksUITests.testP6RetryCausallyRunsOnceAndPublishesCanonicalEmptyState`
  at 323.063s), and all three unresolved failures cluster at 314–339s. That
  clustering is consistent with a shared runner/accessibility trouble on the
  iPhone 17 clones / iOS 27 beta parallel run but does not by itself rule out
  an app main-thread stall unique to that runtime. **A green rerun on iOS 26.5
  is not a diagnosis** of the historical failure.

No product, harness, snapshot, or bootstrap change is warranted from this
evidence alone. The existing `executionTimeAllowance = 60` on the test body is
already the scoped regression against the 337.731s class of stall — a future
recurrence will fail deterministically at 60s with a genuine XCTest watchdog
activity retained by `-test-timeouts-enabled YES`. No additional regression
test is added; the doc records the rationale.

### Scope exclusions

No product navigation, snapshot references, service/bootstrap behavior, or
mutation retry safeguards are changed. The separate
`testTabBarShowsFourOperatorDestinations` assertion belongs to #2571.
