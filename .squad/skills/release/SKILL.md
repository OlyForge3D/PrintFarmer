---
name: release
description: Follow the canonical branch-bound PrintFarmer release workflow. Use when the user asks to cut, ship, or create a server release.
confidence: high
---

## PrintFarmer release authority

Read [the release guide](../../../docs/RELEASE_GUIDE.md) before attempting
publication. `.github/workflows/consolidated-release.yml` is the sole server
release entry point. Stable selects source from `main`; insider selects source
from `development`.
The workflow itself is always dispatched from `development`; the selected
channel determines whether build source is pinned from `main` or `development`.
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

Dispatch `consolidated-release.yml` only from `development` and select the
matching channel. Leave `source_sha` blank to pin that channel branch HEAD once or enter
that full SHA or a trusted ancestor. The workflow definition commit remains a
separate binding. Do not provide a mode or version;
tag, allocator value, CI run ID, comment ID or formatted attestation. Approve
the single `release-<channel>` transaction environment. The workflow starts
canonical qualification, collects evidence, allocates identity and publishes
automatically.

Qualification receipts are bound to the dispatch run, immutable attempt-one
transaction, workflow commit, GitHub Actions check suite, namespaced jobs,
exact source checks, evidence timestamps and live branch policy. They expire
after 30 minutes and are reverified after approval.
`release-stable` and `release-insider` are the only publication environments.
The dispatch has only channel and optional source SHA, and the selected
environment supplies the single approval for the complete protected
transaction. There is no alternate live diagnostic or non-publishing release
ceremony; validation is automated and fail closed.

## Retired paths and recovery

`scripts/release.sh` and `scripts/publish-to-public.sh` always exit 2, including
dry-run/help invocations. Neither performs a version bump, branch merge, orphan
snapshot, tag creation, force push, release creation or asset/container upload.
Do not restore their former dual-history or `--clean-history` behavior.

Never bypass a denial with manual canonical tags, GitHub release commands,
history rewrites or counter resets. Same-run reruns recover the immutable
attempt-one transaction and exact existing reservation bytes. Missing or
mismatched recovery artifacts fail closed; reruns never allocate a second
identity or skip qualification/approval. Follow the release guide's owner-only
continuity recovery procedure when evidence is missing or invalid.
