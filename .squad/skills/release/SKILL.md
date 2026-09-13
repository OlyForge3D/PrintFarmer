---
name: release
description: Follow the canonical branch-bound PrintFarmer release workflow. Use when the user asks to cut, ship, or create a server release.
confidence: high
---

## PrintFarmer release authority

Read [the release guide](../../../docs/RELEASE_GUIDE.md) before attempting
publication. `.github/workflows/consolidated-release.yml` is the sole server
release entry point: stable runs on `main`, insider on `development`.
Branch pushes, direct tag pushes and stabilization branches do not publish.
The reusable Docker workflow is an authorized consumer, not a second entry point.

## Prerequisites

- Obtain explicit authorization to publish; implementation and review tasks do
  not authorize a live release.
- Review `VERSION` on the selected canonical branch. It contains `vX.Y.Z`;
  do not calculate the release from local tags or a private/public remote pair.
- Verify exact-source qualification, owner-approved protection and publisher
  configuration, and pinned ledger continuity as described in the release guide.
- New stable versions and insider bases must exceed the effective stable floor.
  The ledger allocates insider sequence numbers, not the operator.

## Publication

Dispatch `consolidated-release.yml` on the selected canonical branch with the
matching channel. Leave `version` unset for normal allocation; it is only an
assertion against the durable result. Stable selects no stage; insider defaults
to `insider` and also supports the guide's beta/RC progression.

Follow that run through authorization, immutable source/tag/assets and complete
image-set verification. A successful source-only release is not a managed-update
candidate. Record the workflow run, source SHA and resulting canonical identity.
TestFlight remains independent in the `ios/` namespace.

## Retired paths and recovery

`scripts/release.sh` and `scripts/publish-to-public.sh` always exit 2, including
dry-run/help invocations. Neither performs a version bump, branch merge, orphan
snapshot, tag creation, force push, release creation or asset/container upload.
Do not restore their former dual-history or `--clean-history` behavior.

Never bypass a denial with manual canonical tags, GitHub release commands,
history rewrites or counter resets. Exact existing reservations may retry
without changing their identity; new attempts cannot reuse a reserved stable
identity. Follow the release guide's owner-only continuity recovery procedure
when evidence is missing or invalid.
