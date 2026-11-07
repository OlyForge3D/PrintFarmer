---
name: "printfarmer-ios-toolchain"
description: "Diagnose PrintFarmer iOS builds, simulator selection, snapshots, and Xcode-host routing. Use for iOS CI or local simulator failures."
---

## Pinned evidence

- The authoritative toolchain is Xcode **26.6 (17F113)**, iOS **26.5
  (23F77)**, on macOS **26.2-26.x**. CI uses the explicit `macos-26` runner
  and verifies the Xcode version and build.
- Xcode 26.6 is unsupported on macOS 27 or later. The resolver documents a
  possible DTServiceHub rejection before XCTest connects; treat this as a host
  compatibility problem, not an app failure.
- Xcode **27.2 (27B52)** on macOS 27 is a local-exploration path only. It is
  not CI, release, or snapshot-acceptance evidence. Do not use it to replace
  the pinned workflow or refresh snapshot baselines.

## Procedure

1. Route `needs:xcode` work only to a host whose declared capabilities include
   `xcode`; iOS simulator builds require macOS, so do not attempt them on the
   Windows development host. See the [Ralph host rules](../../../.squad/ralph-instructions.md).
2. Record the actual host and toolchain before testing:

   ```bash
   cd mobile
   sw_vers -productVersion
   xcodebuild -version
   ```

   For pinned local evidence, use macOS 26.2-26.x with Xcode 26.6 (17F113).
3. Use the shared resolver rather than a name-only simulator destination:

   ```bash
   ../scripts/ci/resolve-ios-simulator.sh --udid
   xcrun simctl list runtimes -j
   xcrun simctl list devices available -j
   ```

   If no destination resolves, inspect the installed runtime and available
   devices; install iOS 26.5 (23F77) and create an available matching simulator.
   Device/runtime preferences only order candidates; they cannot approve a
   different build. Do not erase or terminate another user's busy simulator.
   Keep Xcode/simulator work serialized on a host.
4. Keep evidence with the test result: Xcode version, commit SHA, resolver
   output, runtime/device lists, test log, `.xcresult`, `.events.jsonl`, and
   `.timing.json`. Follow [mobile test guidance](../../../mobile/AGENTS.md#simulator-testing).
5. Regenerate snapshot baselines only for an intentional, reviewed visual
   change, using the pinned toolchain and the
   [snapshot policy](../../../mobile/PrintFarmerTests/Views/__Snapshots__/README.md).
   Never re-record to hide runtime drift, a flaky test, or a failing assertion.

## References

- [iOS README and exploratory Xcode workflow](../../../mobile/README.md)
- [CI toolchain and runtime policy](../../../docs/CI.md#ios-toolchain)
- [XCUI retry and timeout policy](../../../mobile/docs/xcui-timeout-diagnosis.md)
