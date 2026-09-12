---
post_title: "Release channels and authorization"
author1: "Parker"
post_slug: "release-guide"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["release", "deployment"]
ai_note: "Implementation guidance; not owner approval or deployed-policy evidence."
summary: "One branch-bound release authority, durable allocation, immutable identity and activation requirements."
post_date: "2026-09-12"
---

## Release channels and branches

`consolidated-release.yml` is the sole server release entry point. Dispatch
stable on `main`, insider on `development`; the schedule explicitly dispatches
the same workflow on development (independent of the default branch), selecting ordinary
insider on the repository's `development` default branch. It fails closed if
the default changes. Branch pushes and direct tag pushes do not publish.
The reusable Docker workflow requires the signed record from this exact
workflow/run/attempt; arbitrary reusable callers cannot authorize a release.

| Channel | Base authority | Canonical version | Source tag |
| --- | --- | --- | --- |
| Stable | `main:VERSION` | `X.Y.Z` | `vX.Y.Z` |
| Insider | `development:VERSION` | `X.Y.Z-insider.N`, `X.Y.Z-beta.N`, `X.Y.Z-rc.N` | canonical version prefixed with `v` |

`VERSION` contains exactly `vX.Y.Z` and an optional final newline. Numeric
components have no leading zeros. Prerelease N is positive, never caller-assigned.
The optional dispatch `version` is an assertion against the allocated result,
not a version authority. Omit it for normal allocation. The `stage` field must
be empty for stable; insider defaults to `insider`.

Stable is the installation default. Insider requires separate administrator
opt-in and a reduced-stability warning; running a release workflow does not
enroll or update any host. Versions and channels do not prove compatibility.

TestFlight remains independent under `ios/vX.Y-{alpha,beta,rc}.N`. Historical
`v1.0-beta.*` identities and their `ios/*` aliases retain their original objects.
No migration rewrites existing source tags, registry tags, or historical evidence.

## Durable allocation and exact source

The proposed storage is the protected `release-ledger` Git branch, containing
`state.json`. `RELEASE_LEDGER_ANCHOR` pins its owner-approved ancestry checkpoint.
Each transaction creates a single-parent commit from the observed ledger head
and updates the ref with `force: false`. Competing sibling commits cannot both
fast-forward: the loser rereads and retries. Workflow concurrency is an additional
serialization measure, not the allocator.

One global decimal counter covers beta, insider and RC, across bases and
approved workflow migrations. Allocation keys include repository, workflow ref,
run ID, attempt, full source SHA and base. Same-key retries reuse the entire
record; new attempts reserve a larger N. Failed reservations remain consumed.
Re-run all jobs for a new insider attempt: build, authorization, SBOM and digest
artifact names are attempt-scoped, and consumers never reuse another attempt's
outputs. A failed-job-only rerun cannot inherit an older authorization.
Stable has no N: a new run/attempt cannot rebuild an already reserved stable
identity. Resume byte-identical transfer within its original attempt, or qualify
a reviewed new base; never replace a stable identity with new build bytes.
Big integers are compared numerically, not lexically or through floating point.
No timestamp, run-number concatenation or local tag scan allocates identities.

SemVer orders stages **beta < insider < rc** for a fixed base. Stage changes
must follow that order; after RC, bump the reviewed base before returning to
ordinary insider. All stages share the durable counter, so switching stages
never recycles N. Failed RC reservations also establish the stage high water.

Admission verifies trusted repository/event/workflow, exact canonical branch
HEAD, VERSION and required exact-SHA checks. HEAD drift before authorization
fails; afterward all builds use the authorized SHA, not moving HEAD. An
annotated tag object is stored durably before its public ref is created.
Consumers check both object ID and peeled commit. Missing, moved or recreated
tags fail. No tag force-update/delete API is used. Continuous tag protection
prevents a delete/recreate of the identical object between observations.

Read-only admission uses `github.token` only for source, ledger and qualification
checks. It does **not** call Administration APIs. After environment approval,
authorization obtains the protected publisher App token and verifies live branch
rules, tag/ledger rulesets and environment restrictions before reserving anything.
Missing App credentials or any denied protection read fails without publication;
the CLI never falls back to `github.token` for authorization or ledger writes.

The immutable record retains the verified policy responses, approved App ID,
repository/channel/branch and verification time in `protection`. Its exact bytes
(including protection evidence) are signed with the existing Cosign GitHub OIDC
flow. Same-key retries retain the original evidence rather than rewriting it.
The Docker admission job verifies the exact workflow certificate identity and
record bytes; every downstream build/publisher depends on that successful gate.
Consumers validate the evidence and its binding to the immutable ledger record,
then recheck read-only source VERSION and tag-object/peeled-commit state. They do
not need Administration permission. The final pointer writer uses an App token
without Administration scope; any future live policy revalidation must obtain
an Administration-capable App token first.

This is evidence of policy **at authorization**, not a claim that downstream
jobs observed current administrative policy. Continuous protections and the
owner-only activation/recovery requirements below remain mandatory. This is an
authorization prerequisite, not the #2660 signed complete-manifest format or
its offline trust policy.

## Shared build identity and complete-set boundary

Every job consumes the same record. `release-control.mjs consume` emits:

- `src/ReleaseIdentity.props`, imported by `src/Directory.Build.targets` in
  both native and canonical Docker builds. Informational version is
  `canonicalVersion+sha.<fullCommit>`; assembly/file versions project
  `baseVersion.0` (components must fit .NET's numeric limits).
- Frontend `public/release-identity.json`, embedded in build-time `version.json`.
  Package version and runtime API responses cannot replace the frontend identity.
  Both generation and Vite consumption explicitly allow only `releaseId`,
  `channel`, `canonicalVersion`, `baseVersion`, `sourceBranch`, `sourceTag`,
  `sourceCommit`, `authorizedBranchHead`, `buildId`, `buildAttempt`,
  `workflowIdentity` and `identitySha256`, plus frontend `service`, `commit`
  and `buildTime`. Vite also sanitizes the copied `dist/release-identity.json`.
  Protection evidence, ruleset/environment/reviewer IDs and any future private
  fields are not frontend assets. The root authorization record remains complete
  and unchanged for signing and downstream verification; `identitySha256`
  continues to hash that full record, not the public projection.
- OCI version/revision/source/created and release/channel/run/attempt/workflow/
  record-hash labels, identical across API, frontend, slicer-host, discovery,
  OrcaSlicer worker and monolith, and every declared platform.

Native/optimized images and the canonical multistage monolith consume identical
projections. Local builds without the generated file retain their existing
development metadata.

The complete platform set, labels, source tag, signatures and SPDX attestations
are checked before immutable version-tag publication. Corresponding source,
notices, SBOMs, identity and digest records are publicly verified first; uploads
never clobber differing bytes. Existing version tags must resolve to the exact
candidate digest or publication fails before any version-tag write.

The final ledger transaction compares the expected channel pointer and stores
the entire validated set atomically. It rejects stale source even if an old
attempt somehow has a higher N, version regression, and same identity/different
bytes. A failed build/qualification/set check leaves the previous pointer intact.
The ledger's candidate pointer is **not** authenticated update discovery.

**#2660 owns signed managed eligibility and publication aliases.** These outputs
say `managedEligible: false`; a source-only public release is not an install
candidate. This workflow does not move `stable`, `latest`, `insider`, or major/
minor aliases. Historical aliases remain untouched until #2660 implements
complete-manifest publication and alias isolation. Installer application defaults
remain legacy inputs, not a competing authority for managed releases; generating
digest-pinned installer references belongs to that signed-manifest consumer.

## Stable qualification and candidate lifecycle

Stable requires an owner-reviewed ledger qualification at the exact main SHA:
tests, compatibility, migrations and recovery must pass. Promotion references
an existing immutable insider set/hash/source, plus reviewed main source-tree
changes. Stable rebuilds every image with stable identity; retagging insider
bytes fails complete-set identity checks. A direct stable hotfix instead records
an explicit non-promotion reason and equivalent qualification.

No permanent extra channel branch exists. Optional `release/vX.Y.Z` branches
carry an owner, qualified source, target, creation time and owner-bounded expiry.
They receive full-safe CI and never publish. Merge reviewed stabilization into
main, requalify the resulting SHA, then reconcile fixes into development and
active candidates without regressing development VERSION. Delete only after
publication or documented abandonment and merge-back evidence; retain tags,
source and recovery artifacts. `validateCandidate` provides the executable
policy fixture. CI invokes `validate-release-candidate.mjs` for candidate refs;
their `.github/release-candidate.json` must match the branch, reference an
ancestor qualified source, and stay within `RELEASE_CANDIDATE_MAX_DAYS`.
Protected branch policy and owner review govern lifecycle deletion actions.

## Owner activation and continuity recovery — currently blocked

Read-only live API evidence on 2026-09-12:

- Ruleset **12465886 `Main`** is **disabled**, has an empty include scope, and
  only deletion/non-fast-forward rules. It does not enforce release policy.
- Only the unprotected `copilot` environment exists.
  `release-stable`, `release-insider` and `refs/heads/release-ledger` return 404.
- No `RELEASE_*` variables are configured. Repository access reports admin,
  but #2668 retains explicit owner approval of storage/continuity/publisher policy.
  No live ruleset/environment was changed and no publisher app was provisioned.
- Reading the application package metadata returns **403**, requiring
  `read:packages`. The current credential cannot attest package ACL cutover;
  repository admin access is not proof of registry permission.

Owner acceptance must name policy/VERSION reviewers, publisher and bypass owners,
approve this Git-CAS storage and recovery design, and choose candidate expiry.
CODEOWNERS currently names `jpapiez`; that is existing ownership, not approval
of this new policy.
The ledger is a data-only coordination ref, never an additional release source
branch. Required non-self environment approval also needs an eligible reviewer;
admin API access alone does not supply that reviewer or approve the new policy.

Before enabling:

1. Inventory historical tags/releases and preserve original identities. Record
   the last historical stable base and a trusted sequence floor at cutover.
2. Create a reviewed ledger seed after the approved ancestry checkpoint with
   `schema: 1`, `anchor`, decimal `counter`, `lastHistoricalStable`,
   `reservations`, `identities`, `pointers`, and `qualifications`. Set
   `RELEASE_LEDGER_ANCHOR` to that checkpoint. The workflow never auto-initializes.
3. Enforce main/development code-owner review, non-force/non-delete rules and
   exact-SHA status checks. Protect `release-stable`/`release-insider` with
   non-self reviewer approval and only their respective branch allowed.
4. Activate `release-canonical-tags` (`v*`, no update/delete, **no bypass**) and
   `release-ledger-continuity` (ledger branch, no force/delete, **no bypass**).
   Separate `release-tag-creators` and `release-ledger-writer` rules restrict
   creation/update to one explicitly approved publisher App.
5. Provision its scoped `RELEASE_PUBLISHER_APP_ID` and environment-only
   `RELEASE_PUBLISHER_PRIVATE_KEY`. It needs contents write plus check,
   administration and Actions read permissions for verification.
   Do not reuse an unrestricted repository PAT.
   Application GHCR writes separately require `RELEASE_REGISTRY_USER` and an
   environment-only `RELEASE_REGISTRY_TOKEN` with package-write scope, not
   repository-content scope. Remove inherited/repository Actions **write**
   access to all six application packages and reserve their names for the
   designated registry principal; retain read access as needed. A protected job
   alone does not constrain another workflow's `GITHUB_TOKEN`, so this package
   ACL cutover is mandatory owner evidence, not implied by environment setup.
   Infrastructure package ownership is unchanged.
6. Read the effective policies back and rehearse denied publication before
   first authorized publication, including rejected writes with a generic
   repository workflow token. Release jobs request no repository-token contents
   or package write scope: the protected App publishes source assets and the
   protected package credential publishes application images.

Missing state, invalid ancestry, counter rollback or lost reservations block
publication. Recovery is owner-only: stop publishers, compare retained ledger
history, signed authorization records and release evidence, restore a proven
high water without changing old reservations, and review a continuity checkpoint
migration. Never reset N after a base/workflow change. An unprovable floor means
publication remains disabled. No normal workflow has a reset/bypass operation.

## Validation

Run from the repository root:

```text
node --test scripts/ci/tests/test-release-tag-triggers.mjs scripts/ci/tests/test-daily-development-images.mjs
```

Fixtures execute admission denials without writes, positive stable/insider/
beta/RC paths, numeric ordering, atomic contention, retry/attempt/migration
semantics, tag peeling/movement, same-identity byte conflicts, complete-set/
platform checks, stale-source races, promotion and candidate lifecycle policy.
YAML/compliance checks and the focused Vite metadata test remain required.
No PR may open before fresh exact-head high-risk panel approval.
