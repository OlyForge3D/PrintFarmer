---
name: release-beta
description: "Release a new TestFlight beta build of PrintFarmer iOS. Use when: releasing beta, cutting a release, shipping to TestFlight, tagging a new build, merging development to main for release."
argument-hint: "Optionally provide the beta number, e.g. 64"
---

# Release Beta

Cut a new TestFlight beta of PrintFarmer iOS by merging `development` → `main`, tagging, and pushing to the release remote.

## Prerequisites

- Working tree must be clean (no uncommitted changes)
- All changes intended for release must be on the `development` branch and pushed to `origin`
- Git remotes configured:
  - `origin` → `jpapiez/PrintFarmerMobile` (development repo)
  - `release` → `OlyForge3D/PrintFarmerMobile` (triggers Xcode Cloud build)

## Procedure

### 1. Determine the beta number

Find the latest tag and increment:

```bash
git tag --sort=-creatordate | head -1
```

The next beta number is the current highest + 1. If the user provided a beta number, use that instead.

### 2. Review what's being released

Show the user what commits are on `development` but not yet in the latest tag:

```bash
git log --oneline <latest-tag>..development
```

Summarize the changes and confirm with the user before proceeding.

### 3. Update CHANGELOG.md

Add a new section at the top of `CHANGELOG.md` (after the `# Changelog` header) for the new version. Follow the existing format:

```markdown
## [v1.0-beta.XX] — YYYY-MM-DD

### Added
- **Feature name** — Description.

### Fixed
- **Fix name** — Description.

### Changed
- **Change name** — Description.
```

Categorize commits into Added/Fixed/Changed sections. Omit empty sections.

### 4. Commit the changelog

```bash
git add CHANGELOG.md
git commit -m "docs: update changelog for v1.0-beta.XX"
```

### 5. Run the release script

```bash
./scripts/release-beta.sh <beta-number>
```

This script:
1. Fetches latest from both remotes
2. Checks out `main` and merges `development`
3. Strips forbidden paths (`.squad/`, `.ai-team/`, etc.) from main
4. Creates tag `v1.0-beta.<number>`
5. Pushes `main` and the tag to the `release` remote (OlyForge3D)
6. Returns to the original branch

### 6. Push development to origin

After the release script completes, push the changelog commit:

```bash
git push origin development
```

### 7. Verify

- Confirm `git status` shows clean working tree
- Direct the user to monitor the Xcode Cloud build at: https://github.com/OlyForge3D/PrintFarmerMobile/actions

## Error Handling

- **Dirty working tree**: Commit or stash changes first
- **Tag already exists**: The beta number is already taken; increment and retry
- **Push rejected**: Pull/rebase first, then re-run
- **Merge conflicts**: Resolve conflicts on `main`, then continue the script manually
