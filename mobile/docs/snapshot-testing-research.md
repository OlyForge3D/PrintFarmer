# Snapshot testing research — issue #289

Status: **research / pending approval**. No code changes in this PR yet.

## Current state

- `mobile/Package.swift` declares one dependency: `keychain-swift`. No `swift-snapshot-testing`.
- `mobile/PrintFarmer.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved` does not exist.
- `PrintFarmerTests/` contains no `__Snapshots__` directory and no use of any snapshot framework. The "snapshot" references in `MockPrinterService` and `PrinterServiceProtocol.getSnapshot(id:)` are about camera image data, unrelated to UI snapshot testing.
- Acceptance criteria reference `PrinterBackendCapabilities` fallback shapes already present on `development` (`Moonraker`/`PrusaLink`/`OctoPrint` = full, `FlashForge` = no bed temp / no fan, `SDCP` = no movement / no temp).

## Recommendation

Add [`pointfreeco/swift-snapshot-testing`](https://github.com/pointfreeco/swift-snapshot-testing) (~1.18.x) to the test target. Standard SwiftUI snapshot stack; matches the assumption in the issue text.

### Required changes (follow-up commit, once approved)

1. `mobile/Package.swift`:
   - Add `.package(url: "https://github.com/pointfreeco/swift-snapshot-testing", from: "1.18.0")` to `dependencies`.
   - Add `.product(name: "SnapshotTesting", package: "swift-snapshot-testing")` to the `PrintFarmerTests` testTarget dependencies.
2. `mobile/PrintFarmer.xcodeproj/project.pbxproj`:
   - `XCRemoteSwiftPackageReference` for `swift-snapshot-testing`.
   - `XCSwiftPackageProductDependency` for `SnapshotTesting`.
   - Link to the `PrintFarmerTests` target (Frameworks build phase + product dependencies array). Easiest path: do the add via Xcode UI on a machine with a working SDK, then commit the resulting pbxproj diff.
3. `mobile/PrintFarmerTests/Views/PrinterControlsSectionSnapshotTests.swift` (new):
   - Six tests covering the matrix: `{moonraker, flashforge, sdcp} × {idle (visible), printing (hidden)}`.
   - Capability fixtures via `PrinterBackendCapabilities.fallback(for:)` for each backend.
   - Printer fixtures with fixed UUID, name, and state so output is deterministic.
   - Image-mode snapshots on `UIHostingController(rootView: PrinterControlsSection(...))` sized to `iPhone(.iPhone15Pro)` configuration.
4. `mobile/PrintFarmerTests/__Snapshots__/PrinterControlsSectionSnapshotTests/` directory — populated on first CI run with `isRecording = true`, then flipped to `false` in a second commit.

### CI / local build caveat

Local `xcodebuild` simulator builds on this dev box are blocked by an iOS 26.5 SDK / CoreSimulator drift (see Hudson's history.md). That means snapshot baselines must be captured on CI (or a machine with the matching SDK). The two-commit flow (record → verify) accommodates this.

### Why not a lightweight in-house alternative?

Considered: rendering with `UIHostingController` and snapshotting a `String(describing:)` walk of the view tree. Rejected — string snapshots of SwiftUI bodies are not stable across iOS minor releases and miss layout regressions. `swift-snapshot-testing` is the standard tool; introducing it once is cheaper than maintaining a bespoke harness.
