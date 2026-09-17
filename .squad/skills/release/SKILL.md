---
name: release
description: Use the manual VERSION and Git tag server release workflow.
confidence: high
---

## Server release authority

Read [the release guide](../../../docs/RELEASE_GUIDE.md).
Only an explicitly authorized owner manual dispatch of
`consolidated-release.yml` on `development` publishes server releases.
Stable selects source from `main`; insider selects source from `development`.
Implementation/review tasks do not authorize a live dispatch, merge or deployment.

## Operator flow

1. Review the selected branch's `VERSION` (`vX.Y.Z`).
2. Choose channel and explicit version: `X.Y.Z` or `X.Y.Z-insider.N`.
   Optionally select a full channel-branch ancestor source SHA.
3. Run Consolidated Release once and inspect its summary/release URL.

Native Actions concurrency, duplicate checks and permanent Git tags replace
the retired ledger/allocation/signed-operation/abandonment architecture (#2745).
Do not recreate that machinery or require missing old authorization artifacts.
If a version is already used, use a new one; never overwrite/delete its tags.
A fresh dispatch may retry only a still-unused version. Job reruns are rejected.
The existing owner-only channel environment protection and credentials remain.

## Safety boundaries

GitHub release publication happens after required builds, assets and image tags.
Partial failures remain explicit; a draft or moving alias is not a complete release.
Use pinned digests from the last successfully published release.
These releases are manual-install-only, not signed managed-update candidates.
Do not weaken installation authorization, active-print checks, update safety,
repository review or CI to simplify publication.

Preserve historical remote tags, ledgers, artifacts and run records. In particular,
`v0.2.3-insider.1` is used forever. Never rerun the consumed tag diagnostic or
failed abandonment, reconstruct signing authority, or edit grants/secrets.
The retired shell release helpers remain disabled.
