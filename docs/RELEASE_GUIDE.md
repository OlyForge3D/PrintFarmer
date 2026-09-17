---
post_title: "Manual server releases"
author1: "Parker"
post_slug: "release-guide"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["release", "deployment"]
ai_note: "Implementation guidance, not authorization to publish or install."
summary: "VERSION, a manual Actions run, six container images, and a GitHub release."
post_date: "2026-09-16"
---

## Release a version

1. Review `VERSION` on `main` for **stable**, or `development` for **insider**.
   It contains the base version, for example `v1.2.3`. Signed releases require
   a nonzero major version; change the base through
   the normal reviewed PR process when needed; keep monorepo versions synchronized.
2. As **jpapiez**, open **Actions > Consolidated Release > Run workflow** on
   `development`. Choose the channel and enter
   `1.2.3` for stable or an unused `1.2.3-insider.N` for insider, such as
   `1.2.3-insider.3`. The base must match the selected source's `VERSION`.
   Leave `source_sha` blank for that channel branch's current HEAD, or supply
   its full 40-character ancestor SHA.
3. Run it once. The summary identifies the pinned source, check results and
   release URL. A successful release contains generated GitHub release notes, pinned image
   references, corresponding source, license notices, SBOMs, and a signed
   `update-manifest.json` plus its `update-manifest.sigstore.json` bundle.

There is no release ledger, reservation, signing ceremony, qualification receipt,
counter recovery or abandonment step. The explicit version and permanent Git tag
are the identity. GitHub Actions serializes server release runs; GitHub rejects
duplicate tag creation. Branch/tag pushes do not invoke the publisher.

This follows the ordinary version/tag approach used by
[Desktop](https://github.com/OlyForge3D/PrintFarmerDesktop/blob/development/.github/workflows/release.yml)
(tag must match `package.json`) and
[mobile](../.github/workflows/testflight-beta.yml)
(`ios/v<version>-beta.N`, with an optional explicit number).
Server tags remain separate from the mobile `ios/` namespace.

## What the workflow does

The read-only first job validates the live owner dispatch, approved channel
environment, source ancestry, VERSION and unused version. CI runs its full
selected-source checks. One job in the existing `release-stable` or
`release-insider` environment builds and verifies the following set:

| Image suffix | Docker target | Platforms |
| --- | --- | --- |
| api | api-runtime | linux/amd64, linux/arm64 |
| frontend | frontend-runtime | linux/amd64, linux/arm64 |
| slicer-host | slicer-host-runtime | linux/amd64, linux/arm64 |
| printer-discovery | printer-discovery-runtime | linux/amd64, linux/arm64 |
| orcaslicer-worker | orcaslicer-worker | linux/amd64 |
| monolith | monolith-runtime | linux/amd64, linux/arm64 |

Repositories are `ghcr.io/olyforge3d/printfarmer-<suffix>`. Builds push by digest
with BuildKit provenance and SBOMs. Every expected platform and its source/version
labels are verified; ARM64 runtime smoke checks precede release creation.

Only after all builds succeed does the publisher create the permanent
`v<version>` Git tag and a draft GitHub release, upload and check its asset
inventory, publish versioned image tags, and advance permitted stable aliases.
The manifest is canonical JSON with schema `1`, binds all six OCI index
digests plus every declared platform child digest, and sets
`managedUpdateEligible: true`. The exact bytes are signed with keyless Cosign
through GitHub Actions OIDC and verified three times before release visibility:
twice by the workflow (sign, then re-verify) and once more by
`publish-release.mjs` itself immediately before the `gh release upload` call,
so nothing can substitute an unsigned or mismatched manifest between those
workflow steps and the actual upload. Its `sequence` field is a collision-free,
stable-dominant encoding (see [installation readiness](DEPLOYMENT_UPDATE_STRATEGY.md)).

The cross-language wire contract uses exactly these service IDs: `api`,
`frontend`, `slicer-host`, `printer-discovery`, `orcaslicer-worker`, and
`monolith`. Top-level `platforms` is the bare union
`["linux-amd64","linux-arm64"]`; each service declares a subset of that union,
and `orcaslicer-worker` declares only `linux-amd64`. `platformDigests` is a flat string map keyed by the bare platform union:
`linux-amd64` and `linux-arm64`. Service `platforms` arrays remain subsets of
that union; `orcaslicer-worker` is amd64-only. A service-namespaced digest map
is not accepted by the C# consumer.
The canonical byte fixture is
`scripts/ci/fixtures/update-manifest.golden.json`, with its Draft 2020-12 schema
at `scripts/ci/fixtures/update-manifest.schema.json`. Node tests regenerate the
fixture and compare every byte, including the trailing line feed; C# consumers
can deserialize those same checked-in bytes.

`minimumUpdaterVersion` is required in every generated manifest. Until a higher
compatibility floor is approved, the publisher intentionally emits `0.0.0`;
this makes the no-additional-floor policy explicit instead of silently treating
an omitted field as permissive. Producer validation rejects omission.

The language-neutral golden contract lives at
`scripts/ci/fixtures/release-version-sequence.golden.json`; its JSON Schema is
`scripts/ci/fixtures/release-version-sequence.schema.json` (`schemaVersion: 1`).
Valid vectors provide the version, parsed numeric components/channel suffix,
and a lossless decimal-string expected sequence. Invalid vectors provide the
rejected version and required error text. Ordering vectors and distinct groups
cover stable/insider precedence and historical collision boundaries. Consumers
must calculate with `BigInt` or a signed 64-bit integer (`long`/`Int64`, not C#
`int`), reject values above either signed `Int64` or JavaScript's safe-integer
maximum, and emit the manifest `sequence` as an exact JSON integer.

It publishes the GitHub release **last**. Insider releases are prereleases and
have only their exact version image tag: they never advance `latest`, major or
minor tags. Stable additionally retains `stable-X.Y.Z` and advances `X.Y`,
`X` and `latest` only without version regression or cross-channel movement.
An older stable version does not replace GitHub's latest release.

## Failed or concurrent runs

The native `server-release` concurrency group serializes both channels without
canceling the running build. GitHub may replace an older pending run with a newer
pending run; it is not a durable FIFO queue. Inspect the run status.

A failed build may leave untagged digest-addressed images. Failure after tag
creation may leave version tags, some changed aliases or a draft release.
Registries cannot atomically move aliases across six repositories: the failure
summary explicitly does not claim a complete publication. Use the last successful
release's **pinned digests**, not moving aliases, when selecting a complete set.

Rerunning jobs is rejected. Use a **fresh manual dispatch** after fixing the cause.
If the Git tag, release or any immutable image tag already exists, choose a new
version (for stable, update VERSION through a PR). No overwrites, tag deletion,
draft reuse, rollback of aliases, reconstruction of authority or automatic cleanup
is attempted. Build results are retained as an ordinary Actions artifact for 30
days; they are diagnostic files, never credentials for retry.

## Existing protections and configuration

Keep the approved `single-maintainer` mode, owner-only manual dispatch and
development-only routing. Both existing release environments retain their
branch policy, disabled administrator bypass and no-second-reviewer configuration.
This change neither alters protections nor edits App grants, environments or secrets.

The approved publisher App/installation remains PrintFarmer-only. The workflow
requests Contents/Workflows write and Actions/Administration read, and mints its
short-lived token **after** the long build. Only the publication job requests
`id-token: write`, and the official Cosign installer is pinned to `v3.9.2`
while the binary is pinned to `v3.0.6`.
Cosign verification requires issuer
`https://token.actions.githubusercontent.com` and the exact development workflow
identity
`https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development`
for both channels. The workflow also checks `GITHUB_WORKFLOW_REF` against that
actual dispatch ref, so no
other workflow, repository, branch, or fork identity is accepted.
Existing `RELEASE_PUBLISHER_APP_ID`,
`RELEASE_PUBLISHER_PRIVATE_KEY`, `RELEASE_REGISTRY_USER`, `RELEASE_REGISTRY_TOKEN`
and `RELEASE_APPROVAL_MODE` configuration is reused. No ledger anchor is needed.
Repository PR review and CI requirements remain unchanged.

## Publication is not installation authorization

`container-images.json` remains the informational image inventory and explicitly
sets `managedUpdateEligible: false`. `update-manifest.json` is the separate,
signed managed-update contract; its signature authenticates the publisher but
does not implement installation or apply.
The workflow publishes those metadata assets but does not install them, update a
ledger pointer, enroll a host, contact an installation, stop a printer or perform
a deployment.

Builds report the version and full source SHA without inventing an allocation,
stable sequence or signed release identity. Existing UI inventory consequently
does not acquire managed readiness; its canonical identity fields may read
`Unknown`. Informational version/source remain in `version.json` and
`release-identity.json`, not a signed identity embedded in the UI.
The host updater currently exposes a metadata
provider interface, not a production GitHub-feed adapter; its signature, complete-set,
installation approval, active-print and runtime safety checks are unchanged.
Any consumer requiring the old signed set must reject its absence, not treat
ordinary GitHub release notes or `container-images.json` as an authorized plan.
See [installation readiness](DEPLOYMENT_UPDATE_STRATEGY.md).

## Historical data and retired tools

Issue #2745 supersedes publication-only ledger/allocation/operation-signing and
qualification-receipt requirements. The old publisher, manual qualification
workflows and mutation/recovery commands are removed from active source.
Remote ledger records, runs, artifacts and tags remain historical audit data.
No migration or recovery of them is a prerequisite to this workflow.

In particular, **`v0.2.3-insider.1` and `v0.2.3-insider.2` remain permanently
manual-only**. Their unsigned
reservation is not repaired or abandoned. Do not rerun the consumed diagnostic
35169805018 or failed abandonment 35177228925, reconstruct the missing
`release-authorization-1` artifact, reset counters, or mutate remote history.
Historical implementation is available in Git history, not an alternate active
publisher. `scripts/release.sh` and `scripts/publish-to-public.sh` remain retired
and exit 2; do not restore their former history-rewriting behavior.

## Validation

Run `node --test scripts/ci/tests/test-publish-release.mjs` from the repository
root, plus the related compliance, build-metadata and provenance tests. Workflow
tests exercise the real job/input/output graph and positive/negative release
steps using fake GitHub/registry boundaries. Validate YAML with actionlint and
the repository YAML lint configuration. Never dispatch a live release as a test.
