# Decision: iOS PR CI & Deterministic Changelog

**Author:** Parker  
**Date:** 2025-07-25  
**Status:** Implemented

## Context

The consolidated release workflow generates changelogs via `git-cliff` but had no `cliff.toml` — meaning output was non-deterministic (depended on git-cliff's built-in defaults which may change between versions). Additionally, iOS code under `mobile/` had no PR-level CI gate — changes could land without a build verification.

## Decisions

1. **iOS PR CI workflow** (`ios-pr-ci.yml`) triggers on `pull_request` when `mobile/**` changes. It builds Debug and runs `PrintFarmerTests` on GitHub-hosted macOS with the latest available Xcode. UI tests (`PrintFarmerUITests`) are excluded from PR CI because they are flaky on simulators and slow — they run in the release path instead.

2. **`cliff.toml`** pinned at repo root. Commits are grouped by conventional-commit type, sorted oldest-first within groups, and commit messages are sorted alphabetically within each group for deterministic output. `chore(release)` commits are skipped to avoid noise.

3. **No trigger overlap** — `ios-pr-ci.yml` uses `pull_request` on path `mobile/**`; `consolidated-release.yml` uses `workflow_dispatch` only. No risk of duplicate runs.

## Impact

- All squad members: PRs touching `mobile/` will now require a green iOS build before merge.
- Release engineer: changelog output is now reproducible regardless of git-cliff version updates.
