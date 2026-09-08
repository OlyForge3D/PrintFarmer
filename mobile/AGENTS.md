# Agent Instructions

Use GitHub issues for all task tracking. Create and manage issues via:

```bash
gh issue create --title "Your issue title" --body "Details"
gh issue list
gh issue view <number>
```

For more information, see GitHub CLI documentation: https://cli.github.com/manual/gh_issue

## Simulator Testing

Use the shared resolver, not a name-only destination. The approved default is
**iOS 26.5 (23F77)**; runtime preferences cannot approve another build.
Install that runtime in Xcode Settings > Components and create a matching
available simulator in Window > Devices and Simulators. Xcode, `xcrun`, and
Python 3 are required. `GITHUB_ENV` is optional; `--udid` emits only the local
destination on stdout and diagnostics on stderr.

From the repository root, run the focused snapshot class with retained evidence:

```bash
cd mobile
(
  set -euo pipefail
  mkdir -p build
  run_dir="$(mktemp -d "$PWD/build/snapshots.XXXXXX")"
  xcodebuild -version | tee "$run_dir/xcode.log"
  git rev-parse HEAD | tee "$run_dir/commit.log"
  xcrun simctl list runtimes -j > "$run_dir/runtimes.json"
  xcrun simctl list devices available -j > "$run_dir/devices.json"
  simulator_udid="$(../scripts/ci/resolve-ios-simulator.sh --udid 2>"$run_dir/destination.log")" ||
    { cat "$run_dir/destination.log" >&2; exit 1; }
  cat "$run_dir/destination.log"
  xcodebuild test -project PrintFarmer.xcodeproj -scheme PrintFarmer \
    -destination "platform=iOS Simulator,id=$simulator_udid" \
    -only-testing:PrintFarmerTests/PrinterControlsSectionSnapshotTests \
    -parallel-testing-enabled NO \
    -resultBundlePath "$run_dir/Snapshots.xcresult" \
    2>&1 | tee "$run_dir/test.log"
)
```

For all unit tests use `-only-testing:PrintFarmerTests`; select
`PrintFarmerUITests/<Suite>` explicitly for XCUI. Run the same snapshot command
with `export IOS_SIMULATOR_DEVICE_FAMILY=iPad` inside the subshell for separate
iPad-host evidence; never reuse iPhone results as iPad certification.

For environmental drift, compare identical test/reference blobs with the same
pinned Xcode, runtime/build, host family, scale and locale. Not every failure is
environmental; investigate XCUI failures separately. Preserve strictness,
zero-skip expectations and existing PNGs. The
[snapshot guide](PrintFarmerTests/Views/__Snapshots__/README.md) describes
intentional baseline changes, not permission to re-record for #2536/#2572.

## Session Completion

**When ending a work session**, ensure all work is pushed to remote:

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work via `Closes #N` in PR body
4. **PUSH TO REMOTE** - Commit and push your changes:
   ```bash
   git pull --rebase
   git push
   git status  # MUST show "up to date with origin"
   ```
5. **Verify** - All changes committed AND pushed to remote

**CRITICAL RULES:**
- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing—that leaves work stranded locally
- If push fails, resolve and retry until it succeeds
