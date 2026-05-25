### 2026-05-24: Mobile pre-build discipline — learnings from beta.73 failure

**By:** Hudson (iOS Developer)
**Triggered by:** TestFlight build v1.0-beta.73, run 26382724572

## What happened

`PrinterBackendCapabilities.swift` was created under `mobile/PrintFarmer/Models/`
during the controls-section work but was never added to the Xcode project target.
The file existed on disk; `grep` finds it; `swiftc -parse` passes. But xcodebuild
ignores files not registered in `project.pbxproj`, so the type was invisible to
the compiler, causing cascade errors across every view that referenced it.

## Xcode project registration checklist

Every new `.swift` file in the mobile app needs four entries in `project.pbxproj`:

1. `PBXFileReference` (in the file references section)
2. `PBXBuildFile` (in the build files section, references the `PBXFileReference` UUID)
3. Group children entry (in the appropriate `PBXGroup`)
4. Sources build phase entry (in the app target's `PBXSourcesBuildPhase`)

Missing any one of these silently drops the file from compilation.

## Capabilities API note

`PrinterBackendCapabilities` is the domain model for per-backend feature flags
(`supportsMovement`, `supportsBedTemperature`, `supportedAxes`, etc.). It lives in
`mobile/PrintFarmer/Models/PrinterBackendCapabilities.swift` and is populated via
`PrinterService.getBackendCapabilities(printerId:)`. It is NOT renamed or moved —
the beta.73 errors were purely a project-registration miss, not a type rename.

## Local build rule — environment caveat

The "build locally before commit" directive (`.squad/decisions/inbox/copilot-directive-mobile-prebuild.md`)
requires xcodebuild to succeed. In this dev environment:

- **CoreSimulator drift** (1051.49 < 1051.54) prevents device/simulator targeting
- **Xcode SPM** passes `-c safe.bareRepository=explicit` programmatically; this
  overrides `git config --global safe.bareRepository all` and blocks package
  resolution for `keychain-swift` and `swift-snapshot-testing`

Until the environment is updated, the practical substitute is:
- `swiftc -parse` on all changed `.swift` files plus their direct dependencies
- Verify pbxproj has all four registration entries for any new file
- Push and rely on CI (TestFlight workflow) for the full xcodebuild gate

## Recommended fix for dev environment

```bash
# Update Xcode to match installed CoreSimulator, or:
sudo softwareupdate --all --install --force
# Then re-open the project in Xcode to trigger fresh package resolution
```
