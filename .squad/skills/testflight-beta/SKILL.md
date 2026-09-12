---
name: testflight-beta
description: Cut a new PrintFarmer iOS TestFlight beta build. Use when the user asks to kick off, trigger, or release a new mobile/iOS beta build.
confidence: high
---

# PrintFarmer iOS TestFlight Beta Skill

Use this skill whenever the user asks to kick off a new mobile beta build,
TestFlight build, TestFlight beta, or iOS beta release.

## The Actual Mechanism (verified 2026-09-10/11)

`.github/workflows/testflight-beta.yml` triggers automatically on **any pushed
tag matching `v*-beta*` (or `v*-alpha*` / `v*-rc*`)**, or via manual
`workflow_dispatch`. **Pushing the tag is the entire trigger** — no separate
dispatch step is needed once the tag exists on `origin`.

```yaml
on:
  push:
    tags:
      - 'v*-alpha*'
      - 'v*-beta*'
      - 'v*-rc*'
  workflow_dispatch:
    inputs:
      environment: { default: 'internal', options: [internal, external] }
```

**Beta tags are cut directly from `development` HEAD — NOT from `main`.**
Verified: `v1.0-beta.100/101/102/103` are all ancestors of `origin/development`
and are **not** ancestors of `origin/main` (main last moved 2026-07-25; betas
have continued weekly since). Do not assume a main-merge is required.

## How to Release a Beta

From the **repo root**, with `origin` fetched:

```bash
git fetch origin development --quiet
git tag v1.0-beta.<N> origin/development   # <N> = next integer after the latest existing tag
git push origin v1.0-beta.<N>
```

Then confirm the workflow fired:

```bash
gh run list --workflow=testflight-beta.yml --limit 3 \
  --json databaseId,status,conclusion,headBranch,event,createdAt,url \
  -R OlyForge3D/PrintFarmer
```

### Finding the next beta number

```bash
git tag -l 'v*-beta.*' | sort -V | tail -1
```

Increment the trailing integer by 1. The leading version (`v1.0` at time of
writing) does not need to track the repo-root `VERSION` file — beta tags are
their own independent counter, separate from `scripts/release.sh`'s
semantic-version releases.

## Anti-Patterns / Known-Stale Artifacts (do not use)

- **`mobile/scripts/release-beta.sh <N>`** — looks plausible but pushes to a
  git remote named `ios-release` that does not exist in this repo, and
  requires `main` to be exactly synced with `origin/main` (a merge-to-main
  workflow that beta releases do NOT actually use). Treat this script as
  dead/stale until someone fixes or removes it — do not debug the missing
  remote, just use the tag-push method above instead.
- **`docs/IOS_BETA_RELEASE_CHECKLIST.md`** — written for a specific past epic
  (#705/#724) and is a human-gated release-readiness checklist (QA sign-off,
  explicit "Jeff's go-ahead," APNs topology verification), not a general
  step-by-step for every beta. It does not describe how recent betas
  (100-103) were actually cut. Consult it only if the user explicitly wants
  the full native-push readiness gate re-run; do not treat it as the default
  trigger procedure.
- **`fastlane beta` run directly from `mobile/`** — there is no
  `mobile/fastlane/Fastfile` in this repo. Fastlane only runs inside the
  GitHub Actions job (`testflight-beta.yml`), using secrets that exist only
  in that environment. Do not try to invoke fastlane locally.

## What Happens After the Tag Push (informational)

The GitHub Actions job builds/archives/signs (via `fastlane match appstore
--readonly`, team `ZPKA84F3TY`, bundle id `com.olyforge3d.printfarmer.ios`)
and uploads to TestFlight. Verify success via:

```bash
gh run list --workflow=testflight-beta.yml --limit 1 \
  --json status,conclusion,url -R OlyForge3D/PrintFarmer
```

A `prerelease: true` GitHub Release is auto-created on success; the build
then appears in App Store Connect for the configured TestFlight groups.

## Verification After Trigger

```bash
git tag -l 'v*-beta.*' | sort -V | tail -1   # new tag present
gh run list --workflow=testflight-beta.yml --limit 1 --json status,conclusion,url -R OlyForge3D/PrintFarmer
```
