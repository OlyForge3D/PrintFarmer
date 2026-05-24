# Decision: Consolidated Release — Web/API + iOS from Single Repo

**Date:** 2026-03-25  
**Author:** Dallas (Lead)  
**Status:** Approved (architecture validated, execution path defined)

## Context

Jeff previously requested consolidating the iOS mobile app release alongside the main PrintFarmer web/API release from this single monorepo (PFarm1). The mobile code was subtree-merged from `PFarm-Ios` into `mobile/` and the TestFlight workflow (`testflight-beta.yml`) was promoted to the repo root. That chat was lost — this decision reconstructs and formalizes the consolidated release strategy.

## Current State Assessment

### Already Done ✅

1. **Mobile code in monorepo** — `mobile/` directory contains full Swift/SwiftUI iOS app (subtree merge from PFarm-Ios, commit `bcb99196e`)
2. **TestFlight CI workflow** — `.github/workflows/testflight-beta.yml` is fully operational at repo root, triggers on `v*-beta*` / `v*-rc*` tags, builds from `mobile/` working directory
3. **VERSION file** — Single source of truth at repo root (`v0.2.2` currently)
4. **Version sync script** — `scripts/sync-monorepo-version.sh` syncs VERSION → Xcode `MARKETING_VERSION` in `mobile/PrintFarmer.xcodeproj/project.pbxproj`
5. **Bump script** — `scripts/bump-version.sh` handles semver bumps
6. **Release beta script** — `mobile/scripts/release-beta.sh` (has merge conflict markers — stash `temp-ios-release-cutover` contains the fix)
7. **Docker publish workflow** — Triggers on `release` branch push and `v*` tags for web/API containers
8. **Main release workflow** — `release.yml` creates tags, generates changelog (git-cliff), builds containers, creates GitHub Release
9. **Draft release workflow** — `draft-release.yml` for creating draft releases
10. **Auto-bump workflow** — `auto-bump-release.yml` for version bumping
11. **Squad labels** — `area:ios` label exists and is actively used; `squad:hudson` (iOS Dev) and `squad:gorman` (iOS Networking) agents assigned

### Missing / Blocked ❌

1. **Merge conflict in release-beta.sh** — Lines 17-44 have `<<<<<<< Updated upstream` / `>>>>>>> Stashed changes` markers. The stash `temp-ios-release-cutover` has the resolution but was never applied.
2. **No unified release workflow** — `release.yml` only handles web/API (container builds + cosign verification). iOS TestFlight is a separate workflow triggered by different tag patterns (`v*-beta*` vs `v*`).
3. **No `cliff.toml`** — The release workflow references it but the file is empty/missing. Changelog generation may silently produce nothing.
4. **Tag strategy conflict** — Web/API releases use `vX.Y.Z` tags; iOS beta uses `vX.Y.Z-beta.N` tags. These are compatible but no orchestration exists to cut both from one action.
5. **P0 iOS blockers** — Issues #280, #281, #282, #283 (printer controls foundation) are marked `priority:p0` "Blocking release". These must land before first iOS release.
6. **No iOS CI gate** — No workflow runs Xcode build/tests on PR to validate the Swift code doesn't regress. TestFlight only runs on tag push.

### Label/Roster Mismatches ⚠️

- `squad:dallas` and `squad:🏗️ dallas` both exist as labels — duplicates may confuse automation
- `squad:⚛️ ripley` and `squad:ripley` both exist — same issue
- Labels reference `squad:hudson` and `squad:gorman` which are valid agents in `.squad/agents/`
- No `area:release` or `area:ci` label for release-specific issues — would help triage

## Architecture Decision

**Single tag triggers both release flows.** The consolidated strategy:

1. **Version bumps** use `scripts/bump-version.sh` → updates `VERSION` → `sync-monorepo-version.sh` propagates to Xcode project
2. **Beta releases** (`vX.Y.Z-beta.N` tags): Trigger both `testflight-beta.yml` (iOS) and `docker-publish.yml` (containers) — docker-publish already matches `v*` tags
3. **Production releases** (`vX.Y.Z` tags): Trigger `release.yml` (containers + GitHub Release) and a new **App Store submission** step or separate workflow
4. **release-beta.sh** becomes the canonical script for cutting a beta across both platforms from one command

## Execution Path (Ordered Slices)

### Slice 1: Fix the Stash (5 min) — Immediate

- Apply stash `temp-ios-release-cutover` to resolve merge conflict in `mobile/scripts/release-beta.sh`
- Validate script runs cleanly with `bash -n`
- Commit to development branch

### Slice 2: iOS CI Gate (30 min) — Before next iOS merge

- Create `.github/workflows/ios-ci.yml` that runs on PR changes to `mobile/**`
- Job: Xcode build + unit tests on `macos-latest`
- Blocks PR merge if Swift code breaks
- Prevents regressions between iOS dev and TestFlight release

### Slice 3: Unified Tag Orchestration (1 hr)

- Update `docker-publish.yml` to also trigger on `v*-beta*` tags (it already triggers on `v*` which technically matches, but verify)
- Ensure `testflight-beta.yml` and container pipeline both fire on beta tags
- Add `cliff.toml` with conventional commit config so changelog generation works
- Validate: push a `v0.2.3-beta.1` tag → both iOS and container builds should start

### Slice 4: Release Script Cleanup (30 min)

- Move `mobile/scripts/release-beta.sh` → `scripts/release-beta.sh` (it's a repo-level operation now)
- Update the script to:
  - Run `sync-monorepo-version.sh` before tagging
  - Push tag that triggers both TestFlight and Docker workflows
  - Print status URLs for both pipelines

### Slice 5: Resolve P0 iOS Blockers (Multi-session)

- Issues #280-#283 must be completed by Hudson/Gorman
- These are feature work, not release infrastructure — but they gate the first public beta
- Dallas reviews when PRs land

### Slice 6: Production Release Workflow (Future)

- Extend `release.yml` to wait for both container build AND TestFlight upload
- Or: Create `release-ios-production.yml` for App Store submission (separate from TestFlight beta)
- This is post-first-beta work

## Dependencies

```
Slice 1 (stash fix) → Slice 4 (script cleanup)
Slice 2 (iOS CI) → independent, do ASAP
Slice 3 (tag orchestration) → depends on Slice 1
Slice 5 (P0 blockers) → gates first beta release, not infrastructure
Slice 6 (prod workflow) → after first successful consolidated beta
```

## Risks

- **macOS runner availability** — TestFlight builds require `macos-latest`; GitHub-hosted macOS runners have limited concurrency
- **Secrets configuration** — TestFlight secrets (MATCH_PASSWORD, APP_STORE_CONNECT_API_*) must be configured in the OlyForge3D/PrintFarmer repo, not the old PFarm-Ios repo
- **Xcode version** — Workflow selects Xcode 26+ which may not yet be available on GitHub runners; fallback to 16.x exists
