# Decision: Consolidated Release Pipeline

**Author:** Parker (DevOps)  
**Date:** 2026-03-26  
**Status:** Implemented

## Context

PrintFarmer previously had separate release mechanisms:
- **Main app**: `release.yml` triggers on `vX.Y.Z` tags → Docker images + GitHub Release
- **Mobile (iOS)**: `testflight-beta.yml` triggers on `v*-beta*`/`v*-rc*` tags → TestFlight upload
- **Versioning**: Single `VERSION` file at repo root, `bump-version.sh` handles semver

The mobile app was merged into this monorepo from `PFarm-Ios` (PR #273). A unified release path was needed.

## Decision

### Single Entry Point: `consolidated-release.yml`

One `workflow_dispatch` workflow orchestrates both release targets:

- **Input**: Version tag (`vX.Y.Z`, `vX.Y.Z-beta.N`, `vX.Y.Z-rc.N`)
- **Skippable targets**: Either Docker or iOS can be skipped per-run
- **Tag conventions**:
  - `v1.2.3` → Production (Docker images published, iOS requires separate App Store submission)
  - `v1.2.3-beta.N` → Beta (Docker pre-release + TestFlight internal)
  - `v1.2.3-rc.N` → Release candidate (Docker pre-release + TestFlight external groups)

### Version Sync: `scripts/sync-monorepo-version.sh`

- Reads `VERSION` file as single source of truth
- Syncs `MARKETING_VERSION` in Xcode `project.pbxproj`
- Called during release validation before tagging
- Supports `--check` mode for CI verification

### Existing Workflows Preserved

- `testflight-beta.yml` still works independently on `v*-beta*` tag push (backward compat)
- `release.yml` still works independently for Docker-only releases
- `docker-publish.yml` called as reusable workflow from consolidated release

## What's Still Needed (TODOs)

1. **App Store Connect secrets** must be configured in GitHub repo settings:
   - `MATCH_PASSWORD`, `MATCH_GIT_URL`, `MATCH_GIT_BASIC_AUTHORIZATION`
   - `APP_STORE_CONNECT_API_KEY_ID`, `APP_STORE_CONNECT_API_ISSUER_ID`, `APP_STORE_CONNECT_API_KEY_CONTENT`
   - `TESTFLIGHT_EXTERNAL_GROUPS` (optional)
2. **`REPO_PAT`** secret needed for tag push (existing requirement from `release.yml`)
3. **App Store stable release**: Currently only beta/RC trigger iOS builds. Stable (`vX.Y.Z`) App Store submission requires a separate manual workflow or Fastlane lane — left as future work.
4. **`cliff.toml`**: If git-cliff config doesn't exist, changelog generation gracefully degrades to git log.

## Impact

- **Parker** owns workflow maintenance
- **All team members** can trigger releases via GitHub Actions UI
- **Mobile team** no longer needs separate repo or tooling for releases
- **Tagging convention** is shared — one tag covers both platforms
