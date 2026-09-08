## XCUI timeout diagnosis (#2573)

### Historical evidence

The retained `/tmp/gorman-2519-test.log` lines 4740-4742 recover the exact
duration-to-test mapping reported in [#2535](https://github.com/OlyForge3D/PrintFarmer/pull/2535)
and the [corrected breakdown](https://github.com/OlyForge3D/PrintFarmer/issues/2536#issuecomment-5573669887).
The original result bundle named at line 4667 is no longer present. That log
contains test summaries, not the failing XCUI activity, source location, or
diagnostic stacks. None of those missing details is inferred below.

| Duration | Full test identity (`PrintFarmerUITests/`) | Historical failure site/message | Runtime/toolchain | Classification | Disposition |
| --- | --- | --- | --- | --- | --- |
| 339.100s | `ShiftTasksUITests/testP7DismissClearsOnlyCurrentErrorWithoutTaskMutation` | Unavailable; source report says query timeout | iPhone 17 clone 3, iOS 27.0 beta per source PR; Xcode 26.6 per source PR | Unresolved | [Owned follow-up #2577](https://github.com/OlyForge3D/PrintFarmer/issues/2577) |
| 337.731s | `ShiftTasksFailedRefreshUITests/testFailedStateHostsRefreshableScrollContainerAndRecoversCanonically` | Unavailable; source report says query timeout | iPhone 17 clone 5, same source run | Unresolved | [Owned follow-up #2578](https://github.com/OlyForge3D/PrintFarmer/issues/2578) |
| 314.184s | `OperatorShellUITests/testAdvancedControlsGatedBehindPrinterDetail` | Unavailable; source report says query timeout | iPhone 17 clone 1, same source run | Unresolved | [Owned follow-up #2579](https://github.com/OlyForge3D/PrintFarmer/issues/2579) |

Neighboring *passing* tests took 277-323 seconds in the same parallel run. This is
consistent with shared runner/runtime trouble, but neither establishes contention
nor rules out an app main-thread stall. Without the missing activities/stacks,
the historical cases cannot be classified as infrastructure defects.

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

### Scope exclusions

No product navigation, snapshot references, service/bootstrap behavior, or
mutation retry safeguards are changed. The separate
`testTabBarShowsFourOperatorDestinations` assertion belongs to #2571.
