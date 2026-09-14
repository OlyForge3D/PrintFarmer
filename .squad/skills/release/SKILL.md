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
matching channel. Leave `source_sha` blank to pin the branch HEAD once or enter
that full SHA or a trusted ancestor. The workflow definition commit remains a
separate binding. Select `release`; do not provide a version,
tag, allocator value, CI run ID, comment ID or formatted attestation. Approve
the single `release-<channel>` transaction environment. The workflow starts
canonical qualification, collects evidence, allocates identity and publishes
automatically.

Qualification receipts are bound to the dispatch run/attempt, workflow commit,
GitHub Actions check suite, namespaced jobs, exact source checks and live branch
policy. They expire after 30 minutes and are reverified after approval.
`release-publisher-<channel>` must already exist with no reviewers, no
administrator bypass, and only the channel's canonical branch.

Follow that run through authorization, immutable source/tag/assets and complete
image-set verification. A successful source-only release is not a managed-update
candidate. Record the workflow run, source SHA and resulting canonical identity.
TestFlight remains independent in the `ios/` namespace.

For a safe validation, select `rehearsal` in the same workflow. Its
`release-rehearsal-<channel>` environment is separate from both approval and
publisher environments. Rehearsal uses
the same source and qualification path but has no publisher/registry credential
references, cannot invoke reservation or publication controls, and emits only
a `release-rehearsal-only` receipt with `publicationAuthorized: false`.

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
