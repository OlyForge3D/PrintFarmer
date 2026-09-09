# Issue #2582 Worker Xcode Capability Evidence

## Provenance

- Repository: `OlyForge3D/PrintFarmer`
- Base SHA: `ee1d6b1341c7ce3e99f9f9338c276a084a095194`
- Observed HEAD: `ee1d6b1341c7ce3e99f9f9338c276a084a095194`
- Branch: `ralph/pf-2582-xcode-capability-20260909-a1`
- Ralph launch token: `8e6e3430-48cc-4a2e-b6cb-f01d24258478`
- Ralph job marker: `pf-2582-xcode-capability-20260909-a1`
- Ralph fence: `114`
- Actual session identity: `60ef760c-7ba3-4ebe-885d-e5293d4927c7`
- Worker identity: `Jeff Papiez`
- Timestamp: `2026-09-09T08:25:16.303-07:00`

## Authorized command evidence

Only the requested capability and resolver commands were attempted. No build, test,
simulator mutation, app launch, native-input experiment, issue closure, or PR action
was performed.

| Command | Result |
| --- | --- |
| `printf 'PATH=%s\n' "$PATH"` | Exit `0`; `PATH=/usr/local/bin:/System/Cryptexes/App/usr/bin:/Users/jpapiez/.nvm/versions/node/v24.20.0/bin:/usr/bin:/bin:/usr/sbin:/sbin:/var/run/com.apple.security.cryptexd/codex.system/bootstrap/usr/local/bin:/var/run/com.apple.security.cryptexd/codex.system/bootstrap/usr/appleinternal/bin:/pkg/env/global/bin:/Library/Apple/usr/bin:/usr/local/share/dotnet:~/.dotnet/tools:/opt/homebrew/bin` |
| `if [ -n "${DEVELOPER_DIR+x}" ]; then printf 'DEVELOPER_DIR=set\n'; else printf 'DEVELOPER_DIR=unset\n'; fi` | Exit `0`; `DEVELOPER_DIR=unset` |
| `/usr/bin/xcode-select -p` | Not executed: terminal permission layer returned `Permission denied and could not request permission from user`; no process exit status |
| `/usr/bin/xcodebuild -version` | Not executed: terminal permission layer returned `Permission denied and could not request permission from user`; no process exit status |
| `/usr/bin/xcrun simctl list runtimes -j` | Not executed: terminal permission layer returned `Permission denied and could not request permission from user`; no process exit status |
| `/usr/bin/xcrun simctl list devices available -j` | Not executed: terminal permission layer returned `Permission denied and could not request permission from user`; no process exit status |

## Resolver result

The required command was run exactly once:

```text
IOS_SIMULATOR_DEVICE_FAMILY=iPad scripts/ci/resolve-ios-simulator.sh --udid
```

- Exit status: `72`
- Stdout: empty
- Stderr: `xcrun: error: unable to find utility "simctl", not a developer tool or in PATH`
- Resolved iPad: none
- Prior authorized UDID `014FF738-9D3B-4261-B00E-D7A1E5B16E33`: could not be checked because the authorized devices JSON command was blocked before execution; no presence is asserted.

## Remaining prerequisite

The remaining native-input/XCUI prerequisite is a worker child environment where the
approved Xcode toolchain is executable and exposes `simctl`, with the approved iOS
26.5 (23F77) runtime and an available iPad simulator. Once that capability is
available, rerun the authorized capability probes and resolver before any separately
authorized experiment. This report does not authorize or perform that experiment.
