# AttributeGraph async-layout livelock repro (#3067)

A dependency-free SwiftUI app that reproduces the iOS 26 launch-time render
livelock found in [#3035](https://github.com/OlyForge3D/PrintFarmer/issues/3035).
On the iOS 26.5 simulator, a regular-width `NavigationSplitView` that mounts
while the CPU is saturated keeps the main thread inside one
`CA::Transaction::commit` until the saturation ends. It is the sample project for the Apple Feedback report and a
quick recheck on new runtimes. It is not part of the PrintFarmer app, its Xcode
project or its tests.

See [Launch-time render livelock (#3035)](../../docs/xcui-timeout-diagnosis.md#launch-time-render-livelock-3035)
for the mechanism and PrintFarmer's exposure.

## Run

Requirements: Xcode with an iOS Simulator SDK and an installed iOS 26.5 (23F77)
runtime with an available iPad simulator.

```bash
cd mobile/diagnostics/ag-async-layout-repro
./build.sh
./repro.sh --matrix            # the shared resolver picks the approved iPad
xcrun simctl shutdown all      # repro.sh leaves the simulator booted
```

Use `./repro.sh --udid <udid>` to target another simulator or runtime. For one
configuration, pass launch environment pairs:

```bash
./repro.sh --label minimal REPRO_STARVE_SECONDS=8 \
  REPRO_VARIANT=nobinding,nostyle,plainsidebar,plaindetail
```

`ReproApp.swift` documents every `REPRO_*` knob. Logs are written to
`build/runs/` (ignored by git). Each run must start with the app stopped and
end with the app's `RESULT` line within `--timeout` seconds (default 40).
Otherwise `repro.sh` exits nonzero.

## What it does

1. At launch it queues 2 × active-core spinning blocks at utility QoS for 8s.
2. After a 0.5s splash, it mounts a `NavigationSplitView` shell.
3. A user-interactive watchdog thread posts to the main queue every 20ms and
   prints each main-queue delay over 250ms, then a `RESULT` line.

## Results

Xcode 27.0 (27A266a). The app was built with the Xcode 27 simulator SDK, with a
deployment target of iOS 17. The host Mac had 10 cores, measured on
2026-09-25.

| Runtime / device | Configuration | Worst main-queue delay |
| --- | --- | --- |
| iOS 26.5 (23F77), iPad Pro 13-inch (M5) | split shell, async default (4 runs) | 6.25s, 7.35s, 6.37s, 9.30s (10s starvation) |
| iOS 26.5, iPad | split shell, `AG_ASYNC_LAYOUTS=0` (3 runs) | 0.20s, 0.20s, 0.21s |
| iOS 26.5, iPad | `NavigationStack` shell, async default | 0.14s |
| iOS 26.5, iPad | split shell, no starvation | 0.17s |
| iOS 26.5, iPad | bare `NavigationSplitView { Text } detail: { Text }` | 7.51s; 0.11s with `AG_ASYNC_LAYOUTS=0` |
| iOS 26.5, iPad | each variant alone: no binding, no style, no detail stack, plain sidebar, plain detail | 7.27s to 7.55s |
| iOS 26.5, iPad | starvation starts 2s after the shell settles; a new detail type mounts at 4s | no stall |
| iOS 26.5, iPad | 9 utility spinners (fewer than cores), 5 or 2 spinners | no stall |
| iOS 26.5, iPad | 10 utility, 20 default-QoS or 20 user-initiated spinners | 7.30s, 7.32s, 7.33s |
| iOS 26.5, iPad | 9 user-initiated spinners (plus the main thread = all cores) | 7.26s |
| iOS 26.5, iPad | 20 background-QoS spinners | no stall |
| iOS 26.5, iPhone 17 (compact, collapsed split) | split shell, async default | 0.33s |
| iOS 27.0 (24A434), iPad Pro 13-inch (M5) | split shell, async default (3 runs), 40 spinners, bare split | 0.84s worst; no livelock |

A `sample` of a stalled run put 1174 of 1174 main-thread samples in one
`CA::Transaction::commit` → `_UIHostingView.layoutSubviews` →
`ViewGraphRootValueUpdater.render`, under
`NavigationSplitRepresentable.updateUIViewController` and
`NavigationStackCoordinator.updateNavigationController`. That is the same
signature as the CI crash reports in #3035.

## Conclusions

These describe the simulator runs above: one 10-core host, iOS 26.5 and 27.0
simulator runtimes. Real devices were not tested.

- Every stall in these runs had three things in common: an expanded
  (regular-width) `NavigationSplitView` mounting for the first time in the
  process, async layouts, and enough work at utility QoS or higher to keep the
  utility layout queue off the CPU for the whole mount. Runs that left that
  queue a thread and a core did not stall. Other triggers on real devices are
  not ruled out.
- In every stalled run, the main thread recovered when the starvation ended.
  Recovery paths on other hardware or loads were not tested.
- A bare split view with no app state reproduces it, so app value types are
  not the trigger here, and reducing them would not have removed these stalls.
- The iOS 27.0 simulator runtime did not reproduce it under the same load.
- PrintFarmer's production exposure is unknown until the TestFlight/App Store
  hang reports are checked (#3067).
