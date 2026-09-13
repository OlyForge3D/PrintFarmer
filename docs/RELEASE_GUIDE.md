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

`scripts/release.sh` and `scripts/publish-to-public.sh` are retired and exit
with status 2 for every invocation, including `--dry-run` and `--help`.
They do not merge, force-push `main`, create tags, publish GitHub releases or
upload assets/images. There is no private-to-public snapshot publishing path.
Review `VERSION` on the selected canonical branch and dispatch
[`consolidated-release.yml`](../.github/workflows/consolidated-release.yml).
Do not replace these helpers with direct Git or GitHub release commands.

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

One global decimal counter covers beta, insider and RC, across bases.
Workflow migration requires an owner-reviewed code/policy change; arbitrary
replacement workflow identities are rejected. Allocation keys include repository, workflow ref,
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

Every **new** stable canonical version and insider base must exceed the current
effective stable floor: `pointers.stable.canonicalVersion`, or the immutable
`lastHistoricalStable` when no stable pointer exists. Admission checks the floor,
and each reservation transaction checks it again against its own freshly read
state before mutation or persistence, including after a lost CAS. A concurrent
stable advancement can therefore invalidate an earlier admission without
allowing source-tag, release, asset or container publication. A losing CAS may
leave unreachable ledger objects, never a persisted reservation or source tag.
Exact existing reservations remain idempotent even after stable advances above
their base; this exemption never permits a new attempt or changed admission.

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

Raw policy responses stay **only in memory** during App-token verification:
never in files, logs, outputs, bundles or uploads. The immutable record contains
only a strict, normalized `protection` attestation (schema 5):
repository/channel/branch, ISO `verifiedAt`, the
`printfarmer-release-protection/v4` profile, `approvalMode`, `approvalAssurance`,
boolean policy claims and a SHA-256
digest of those normalized fields. Claims assert branch deletion/rewrite
prevention, PR-only flow without bypass, conversation resolution, the required
self-attested review status and exact-SHA checks, canonical environment branch
restriction, manual approval with administrator bypass blocked, immutable canonical tags, ledger continuity and
exclusive writes by the owner-approved publisher. No actor/App IDs, reviewer
identities, raw rules or hashes of private API responses survive normalization.
`codeOwnerApprovalRequired` and `nonSelfApprovalRequired` are `false` in
`single-maintainer` mode and `true` in `separation-of-duties` mode.
`selfAttestedReviewRequired` means the existing Squad review gate (including its
explicit owner override), not an independent approver. It and every other claim
must remain `true`.
The mode, assurance and claims are bound into the normalized digest and signed
authorization. Earlier evidence, including schema 4/v3, missing modes, unknown fields and
contradictory claims fail closed; there is no compatibility default. Existing
public ledger projections remain unchanged and retain their original hashes.
Retries require the original signed artifact and the same approval mode;
a mode change needs a new attempt, never a rewritten reservation.
Stable qualification retains exact-SHA pass assertions plus reproducible
promotion tree evidence or a hotfix rationale digest. The same qualification
is bound into the signed record; free-form owner text is not copied.

**All Actions artifacts in this public repository are treated as broadly
readable.** File permissions and artifact access controls are not a confidentiality
boundary. Authorization writers reject unknown fields and malformed/weakened
claims before persistence. Complete-set writers allow-list image labels.
The normalized record's exact bytes are signed with the existing Cosign GitHub
OIDC flow. Same-key retries retain the original attestation rather than rewriting
it. Attempt-scoped artifacts and `.artifacts/release-authorization/` contain only
these public-readable records and bundles; the directory remains excluded from
Git and Docker contexts.

Artifact writers use fixed, allowlisted paths and reject linked directories,
symlinks, hardlinks and non-regular output files before truncation. Workflow
outputs are single-line, valid-name records appended only to the existing
`set_output_<UUID>` command file under the trusted runner's
`RUNNER_TEMP/_runner_file_commands` directory; local runs without `GITHUB_OUTPUT`
do not emit a command file. Output destinations must not be supplied by release
inputs or downloaded artifacts.

GitHub requests use only allowlisted methods and repository-relative routes
under `https://api.github.com/repos/OlyForge3D/PrintFarmer/`, encode path
components, and reject redirects. Commit references require full lowercase
SHA-1 values and tags require the canonical release grammar. After signature
and consumer verification, outbound references come from the matching public
ledger entry, not directly from the downloaded authorization JSON. VERSION
components are parsed as arbitrary-precision integers before constructing the
canonical version; the strict grammar still rejects leading zeros.

Workflow inputs/outputs, release assets, ledger reservations, tag annotations
and frontend metadata still use **only the public projection** and its
`identitySha256`, not the attestation. Every consuming job downloads its own
attempt's artifact and verifies the exact workflow certificate identity and
full-record hash before emitting metadata. Verification failures never echo
payloads. Consumers validate the normalized profile, claims, timestamp and
digest offline, plus their binding to the immutable ledger hash,
then recheck read-only source VERSION and tag-object/peeled-commit state. They do
not need Administration permission. The final pointer writer uses an App token
without Administration scope; any future live policy revalidation must obtain
an Administration-capable App token first.

Public release identity is separately signed; its bundle authenticates the
projected JSON, not the full authorization record. The public complete-set asset and ledger
reservations contain only projected identities and approved image labels, while
retaining full-record hashes and publicly reproducible set hashes. Unknown
authorization fields are rejected, not silently approved. The normalized complete set and original authorization bundle remain
available to downstream consumers without relying on artifact confidentiality.
Ledger schema 1 explicitly permits only `schema`, `anchor`, decimal `counter`,
optional `lastHistoricalStable`, and the `reservations`, `identities`, `pointers`,
`stages`, and `qualifications` maps. Every transaction validates the complete
ledger before mutation and again before persistence. Source tagging additionally
projects the complete ledger and binds the original signed record before
**any write**, including `POST git/tags`, then rechecks before creating the public
ref. Unknown top-level fields in in-memory inputs are omitted on projection;
persisted ledger reads reject unknown top-level fields in **every** snapshot
after the pinned checkpoint, not just the head and its immediate parent.
Malformed retained fields fail with
policy errors, never raw property-access exceptions.

Each read walks the complete single-parent chain from the observed ledger head
back to `RELEASE_LEDGER_ANCHOR`, loading and validating every snapshot and every
parent-to-child transition before returning. Existing qualifications must remain
byte-identical under `JSON.stringify`, including nested evidence and field order.
Immutable reservations, admission/record identities, tag claims and complete sets,
nonregressing pointers and stages are checked at every edge. The seed's
`lastHistoricalStable` presence and exact value are immutable: later commits
cannot add, delete, lower or replace this historical floor. Each post-seed
transaction may add at most one reservation. The counter delta must be zero or
one; an increment requires exactly one new insider reservation whose sequence
equals the new counter, with no other reservation additions. An unchanged
counter permits no new insider sequence; a single qualified stable reservation
or a non-allocation transaction leaves the counter unchanged. Gaps, jumps,
duplicate sequences and unbound counter changes fail closed. These checks do not
reinterpret the owner-approved seed's initial counter floor.
A new reservation at each historical edge must also exceed its **parent's**
effective stable floor. A structurally valid direct insertion below that floor
therefore blocks the entire read before any writes. Existing reservations are
not compared to a later floor, so legitimate history and exact retries survive
stable advancement. A multi-commit fast-forward cannot hide an earlier
deletion/replacement, even if a later commit restores the original state.
Merge/octopus commits, cycles, missing
objects, truncated trees and an unreachable checkpoint block reads, allocation
and source-tag publication before any POST/PATCH.

There is no history depth cap or reliance on the compare API's paginated commit
list. All required Git reads must succeed; rate limits and unavailable history
fail closed rather than skipping old snapshots.

Reservation, admission and record variants are closed: every required field must
exist with its declared type, and unknown or explicitly undefined fields fail.

| Variant | Required fields |
| --- | --- |
| Admission | `repository`, `channel`, `baseVersion`, `sourceBranch`, `sourceCommit`, `authorizedBranchHead`, `buildId`, `buildAttempt`, `workflowIdentity`, `workflowCommit`; insider also requires `stage` |
| Private authorization | Admission fields plus `schema`, `releaseId`, `canonicalVersion`, `sourceTag`, `allocationKey`, `created`, `protection`; insider also requires `sequence`, stable requires `qualification` |
| Projected ledger record | Private identity fields, excluding `protection`/`qualification`, plus `buildTime` and `identitySha256` |
| Private reservation | `admission`, `record`; insider also requires `sequence` |
| Public reservation | Private reservation fields plus `identitySha256` |

Stable records/admissions must not contain `stage` or `sequence`. Repository,
workflow ref and branch are fixed by channel. Tag, base, canonical version,
release ID and sequence must agree; source, authorized head and workflow commit
must be the same lowercase 40-hex SHA. Run/attempt/sequence are positive decimal
strings without leading zeros; timestamps are canonical UTC ISO milliseconds.
Allocation keys are recomputed, admission must equal the record, and the public
reservation/record authorization hashes must agree. `created` equals `buildTime`.
Private protection must predate or equal authorization.

Progress fields are constrained variants: `tagObject` is a 40-hex SHA;
`tagPublished`, when present, must be `true` and requires `tagObject`.
`set` and `setHash` must occur together. Pointers bind to an existing matching
complete reservation; stage high-water entries bind to real insider identities.
Adjacent ledger commits still forbid loss or alteration of immutable
reservations, tags, sets and pointer identities. Qualifications are append-only:
every prior source-SHA entry must remain byte-for-byte identical under
`JSON.stringify` in the child, including field order, `promotionOrigin`,
`treeEvidence` and hotfix `reasonSha256`. This also applies after a stable
reservation consumes the qualification and to entries not yet consumed.
Deletion or replacement fails during ledger read, before any Git POST/PATCH;
new source-SHA qualifications may be appended without rewriting old evidence.
Projection validates and copies qualifications without reordering their fields.
A counter seed alone cannot
fabricate a pointer or stage without its supporting reservation.
Complete sets use the same idempotent projection for original authorization
records and already-projected ledger records. Only declared components and their
expected platforms are traversed; missing or extra component/platform keys,
malformed digests, mismatched identities and noncanonical identity-label values
are rejected before any Git write, including writes that only allocate
or retry another release. Labels are emitted from the canonical public identity
and retained authorization hash, never from caller-selected values or keys.
Unknown fields within sets, images, platforms and label maps are omitted, not
copied. `setHash` is SHA-256 of UTF-8 `JSON.stringify(writePublicSet(record, set))`,
using the projector's fixed field/component/platform order. It covers the exact
public set, not an unavailable private payload; public assets and persisted sets
can reproduce it. Every ledger read/write recomputes it and verifies pointer
binding. `identitySha256` continues to cover the original full authorization.
CAS and immutable-tag semantics are unchanged.

Owner-entered qualifications are strict public schema-1 records keyed by the
exact source commit. They contain `schema: 1`, matching `sourceCommit`, boolean
`reviewed`, `tests`, `compatibility`, `migrations`, and `recovery` claims (all
`true`), and `mode`. `mode: promotion` additionally requires `promotionOrigin`
containing only `allocationKey`, `releaseId`, `sourceCommit`, and public `setHash`
of the qualified immutable insider set, plus `treeEvidence`:

- `schema: 1`, `originTree`, `sourceTree`: exact Git tree IDs.
- `metadataChanges`: either empty or one `{path: "VERSION", before, after}`
  entry containing the old/new blob SHAs. No other path is exempt.
- `diffSha256`: `hash({schema, originTree, sourceTree, metadataChanges})`
  using the release policy's SHA-256/JSON function and this field order.

At authorization (including retries), the workflow fetches both immutable
commit trees recursively, rejects truncation/duplicate paths, and compares every
leaf's path, type, mode and object SHA. Changes outside `VERSION` fail, including
workflow, code, symlink, submodule, executable-bit, addition and deletion changes.
Both `VERSION` blobs must parse to the same target base; the exception permits
only stable version-file formatting, not a new feature version or arbitrary
metadata. The computed evidence must equal the owner's evidence exactly.
Main HEAD is checked again after comparison, before allocation. Main must be a
distinct commit from the qualified insider source; stable then rebuilds images
with its own identity, rather than retagging insider bytes.

`mode: hotfix` instead requires `reasonSha256`, the immutable digest returned by
`hotfixReasonDigest(nonSecretReason)`. It hashes a trimmed, whitespace-normalized
20–2000-character single-line explanation of why insider promotion is unsuitable.
The owner retains the corresponding non-secret rationale for audit; neither
secrets nor free-form text belong in public artifacts. A boolean is not rationale.
Unknown fields, raw policy objects, reviewer/publisher identities and legacy
`sourceTreeReviewed`, `nonPromotionApproved`, `hotfixReason` or string-valued pass
claims are rejected, not auto-approved or migrated. Owners must review legacy
qualification inputs and public-set hash semantics before enabling publication.
Existing immutable live evidence must not be rewritten to pass the new schema:
use the owner-approved continuity recovery process if such evidence exists.
Same-attempt retries require the retained original authorization file; if it is lost,
fail closed and rerun all jobs with a new attempt rather than recreating evidence.
Artifact retention therefore bounds attestation recovery.

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
  fields are not public assets. Generation and Vite share one typed allow-list;
  malformed known fields fail rather than being dropped or coerced.
  For both stable and insider releases, the verified authorization consumer
  exports `frontend_identity`; the shared Docker workflow passes it to
  the native standalone `npm run build` and the monolith's
  `Dockerfile.multistage` frontend stage as `PRINTFARMER_RELEASE_IDENTITY`.
  Both publishing jobs reject empty output before building. The monolith passes
  only that public output as a build argument (visible in build provenance),
  never the full authorization record. Its frontend stage also rejects missing
  identity whenever `BUILD_VERSION` is not the local `development` sentinel.
  Vite validates the same inputs in both paths; the monolith copies the resulting
  assets into `wwwroot`. This allow-listed output
  copies the identity fields above (excluding `identitySha256` and `buildTime`)
  and maps the canonical reservation's `allocationKey` to `allocationIdentity`.
  It never exports protection or qualification evidence, or synthesizes promotion
  evidence. Vite validates it independently and embeds it in the bundle and
  nested `releaseIdentity` metadata (with absent promotion origin represented
  as `null`). Both inputs' shared identity fields must agree or the build fails.
  Local builds without an authorization output retain a null runtime identity.
  The root `release-identity.json` is projected; the normalized authorization
  remains complete and unchanged under `.artifacts/release-authorization/`.
  `identitySha256`
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
an existing immutable insider set/hash/source, plus the authorization-verified
tree comparison described above. Stable rebuilds every image with stable identity; retagging insider
bytes fails complete-set identity checks. A direct stable hotfix instead requires
an immutable `reasonSha256` and equivalent qualification; its owner-reviewed
non-promotion rationale stays outside public ledger/artifact payloads.

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
branch. Manual environment approval always needs an eligible reviewer and an
explicit approval mode; admin API access alone does not approve the new policy.
Live activation under #2668 must wait until #2682 is merged.

### Explicit release approval configuration

Set repository variable `RELEASE_APPROVAL_MODE` to exactly one of the values
below. There is **no default**: missing, misspelled, whitespace-padded and unknown
values block admission and authorization before any API read or write.
Admission emits its validated mode as a job output. The protected authorization
job independently resolves the variable and requires exact equality with that
output before any API access. Environment overrides must match the repository
value; a mismatch or missing admission output blocks reservation. Align the
configuration and rerun all jobs. The output is a consistency check, not a
replacement for protected authorization policy. Never accept this policy from
dispatch input or infer it from the number of maintainers.

| Mode | Required native branch PR policy | Required environment policy | Normalized assurance |
| --- | --- | --- | --- |
| `single-maintainer` | Zero native approvals, code-owner review disabled, last-push approval disabled | Manual required-reviewer gate, `prevent_self_review: false`; every configured reviewer must be an owner-approved user | `owner-confirmed/self-attested` |
| `separation-of-duties` | At least one native approval and native code-owner review; GitHub does not let PR authors approve their own PR | Manual required-reviewer gate, `prevent_self_review: true`; at least one configured eligible user/team reviewer | `non-self-review-enforced` |

**Both modes require PR-only branch flow**, resolved review conversations,
stale native approval dismissal on push, strict up-to-date required status checks,
and deletion/force-push prevention on `main` and `development`. In the REST
ruleset `pull_request` parameters, set `required_review_thread_resolution: true`
and `dismiss_stale_reviews_on_push: true`; use actual booleans and an integer
`required_approving_review_count` (0 in single-maintainer, 1–6 otherwise).
Single-maintainer must explicitly set `require_code_owner_review: false` and
`require_last_push_approval: false`; an unsatisfiable native review requirement
is rejected, not described as a stronger assurance.

Use active branch rulesets with explicit canonical ref includes (or `~ALL`),
no exclusions, and **empty bypass lists**. The adapter reads effective rules
and their referenced ruleset details, rejecting missing, truncated, conflicting
or bypassable evidence. An owner merges through the PR/check/conversation gates,
not a ruleset bypass. The existing gate's explicit `APPROVE (owner)` override
can satisfy the review status, but is owner confirmation, not an independent
authorization of an owner-authored PR. Owner administrative power to change
configuration remains a trust boundary: stop publishers, privately record and
review any emergency policy change, then restore and reverify protections.
There is no normal force-push/delete or release-environment bypass.

### Exact-SHA required-check contract

Set `strict_required_status_checks_policy: true` and require these exact contexts
in the effective branch rules:

- `CI tooling tests`
- `.NET build`
- `Frontend build & tests`
- `squad/pre-pr-verdict`

The first three are GitHub Actions **check runs**, not check-suite conclusions.
The last is a **commit status** produced by `squad-review-verdict.yml` using
`repos.createCommitStatus` and `squad-verdict-gate.mjs`'s `verdictContext`.
A green workflow job cannot substitute for that status. Read-only admission and
protected authorization query the selected full SHA's check runs and combined
commit status; authorization also checks every additional configured context.
Latest required runs must be completed/successful at that SHA and the latest
review status must be successful with an exact-head `REVIEWED (self-attested)`,
`REVIEWED (self-attested, carried across sync)` or `APPROVE (owner)` description.
`NOT_APPLICABLE` is not review evidence. The API response SHA, not the shortened
description alone, establishes the full-SHA binding. Raw descriptions/identities
are never emitted in normalized evidence or errors.

For check-run integration bindings, the adapter verifies `check.app.id`;
commit-status responses contain no integration ID, so leave that optional
ruleset binding unset for `squad/pre-pr-verdict` and other status-only contexts.
An unprovable integration binding fails closed rather than inventing status or
check-suite fields. Paginated evidence at the 100-entry cap fails closed.

**Activation prerequisite:** the current Squad producer posts to an open PR's
head, not a later squash/merge commit. A PR-head verdict must never be copied to
a different canonical release SHA. The inspected baseline had no status on its
canonical SHA; release remains blocked until an owner-approved exact-canonical-SHA
qualification path supplies genuine review evidence. #2668 owns that activation
work; this change neither posts statuses nor changes the verdict producer or live
configuration. Required checks alone do not manufacture missing review evidence.

### Environment approval and cutover

**Both modes require administrator bypass to be disabled.** In each release
environment, deselect **Allow administrators to bypass configured protection
rules**. Read back the environment using the publisher App: the REST
`GET /repos/{owner}/{repo}/environments/{environment_name}` response must contain
`can_admins_bypass: false` as a boolean. `true`, omission, null, string values
and failed reads all block authorization before reservation or source-tag writes.
Required reviewers alone do not prove that manual approval cannot be bypassed.

This response field is supported by the REST API and its
[environment SDK model](https://github.com/google/go-github/blob/master/github/repos_environments.go),
although the rendered REST documentation omits it. GitHub documents the
[administrator bypass control](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments).
The verifier uses the live control, not an invented field or run-wide approval
history that cannot bind approval to the current attempt. Normalization adds
only `environmentAdminBypassBlocked: true`; raw environment data stays in memory.
Disabling bypass is an activation requirement, not a claim of independent
approval or proof that administrators cannot later change configuration.

In single-maintainer mode, `jpapiez` is the approved owner reviewer. To delegate,
the owner must explicitly approve the users in private activation evidence and
provision the optional **environment secret** `RELEASE_OWNER_APPROVED_REVIEWERS`
as a JSON array of GitHub user logins. The owner remains allowed; an unset/empty
secret adds no delegates. Invalid JSON, empty arrays or malformed logins fail
closed. Do not put identities in repository variables, workflow YAML, logs or
public evidence. This secret is an owner-provisioned allowlist, not independent
proof of who approved it; access to environment-secret administration is a
trust boundary. Keep the owner's approval record private and review changes.

GitHub accepts any one required reviewer, so **every** configured reviewer must
be on that allowlist in single-maintainer mode; adding an unapproved user beside
the owner is not sufficient. Teams are rejected in that mode because team
membership does not establish explicit approval of each possible reviewer.
In separation-of-duties mode, GitHub's configured user/team reviewer object
provides eligibility evidence and the environment prevents the initiator from
approving; an empty/malformed reviewer list never qualifies.

Single-maintainer approval is **owner-confirmed/self-attested**, not separation
of duties, four-eyes control or independent approval. The normalized evidence
attests the checked environment policy, not the identity of a particular
approver. Switching modes changes the branch and environment approval policy: exact SHA and
branch restrictions, App isolation, immutable tags, ledger continuity, package
ACL isolation and negative rehearsals remain mandatory.

Before enabling:

1. Inventory historical tags/releases and preserve original identities. Record
   the last historical stable base and a trusted sequence floor at cutover.
2. Create a reviewed ledger seed after the approved ancestry checkpoint with
   `schema: 1`, `anchor`, decimal `counter`, `lastHistoricalStable`,
   `reservations`, `identities`, `pointers`, `stages`, and `qualifications`
   using the strict public schemas above, including verifiable public-set hashes
   and tree/rationale evidence for stable qualifications. Do not seed dangling
   stage/pointer references or carry legacy boolean-only qualifications. Set
   `RELEASE_LEDGER_ANCHOR` to that checkpoint. It is an **exclusive pre-seed
   boundary**, not a ledger snapshot: the checkpoint commit must resolve, but its
   tree/state and parents are not traversed or validated as ledger history. It
   may be a root or merge commit. Its immediate child must be a fully valid,
   single-parent seed; no continuity comparison is possible against the
   non-ledger checkpoint. The owner approves that seed's initial floor and
   evidence. A head equal to the checkpoint has no seed and is rejected.
   Every subsequent snapshot and edge back through the seed is mandatory.
   The workflow never auto-initializes.
3. Enforce the mode-specific main/development PR policy, non-force/non-delete
   rules without bypass, conversation resolution and all exact-SHA checks above.
   Do not enable native approval requirements in single-maintainer mode.
   Protect `release-stable`/`release-insider` with
   manual reviewer approval under the explicit mode above and only their
   respective branch allowed. Configure the variable and any owner-approved
   delegate secret; disable administrator bypass and read back each environment's
   actual reviewer/self-review settings and boolean `can_admins_bypass: false`
   before enabling publication.
4. Activate `release-canonical-tags` (`v*`, no update/delete, **no bypass**) and
   `release-ledger-continuity` (ledger branch, no force/delete, **no bypass**).
   Separate `release-tag-creators` and `release-ledger-writer` rules restrict
   creation/update to one explicitly approved publisher App.
5. Provision its scoped `RELEASE_PUBLISHER_APP_ID` and environment-only
   `RELEASE_PUBLISHER_PRIVATE_KEY`. It needs contents write plus check,
   commit-status, administration and Actions read permissions for verification.
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
History fixtures include three-snapshot retained/restored qualification mutations,
intermediate merges and invalid snapshots, missing/truncated objects, explicit
checkpoint semantics and a 1,005-snapshot chain. Read, allocation and source-tag
paths assert zero POST/PATCH calls on rejection; executed admission/authorization
also reject evidence rewrites before writes.
Approval fixtures cover both modes on both channels, absent/invalid mode,
missing/manual-reviewer gates, self-review contradictions, unapproved delegates,
malformed owner configuration, mode-change retries and redaction. Both modes
execute the real admission/authorization/consumer flow with fake API transport;
single-maintainer policy denials prove no reservation/tag/ledger writes.
Branch fixtures reject native-review mode mismatches, every missing required
context, non-strict checks, malformed parameters and bypasses (including the
owner and publisher App). Exact-SHA fixtures reject wrong/missing SHA, pending
or failed latest statuses/runs, truncated responses, `NOT_APPLICABLE`, and
green check-run/check-suite substitutes for the review status. Schema/profile
downgrades and mode/claim contradictions fail even with recomputed digests.
Stable-floor fixtures cover initial historical floors, current pointers,
direct valid-schema history insertions, stable advancement between admission
and allocation, and advancement during a losing CAS. Exact old reservations
remain retryable; new stale reservations cannot reach publication.
The same suite scans repository executable scripts, actions and workflows for
tag creation, force pushes and direct release/API publication. Its explicit
writer inventory permits only the guarded ledger adapter, authorized Docker
consumer, and separate `ios/` TestFlight writers. This is a source regression
check, not a substitute for repository protection or runtime authorization.
Both retired server helpers execute against sentinel publication commands for
normal, dry-run, help and force arguments; every call exits 2 without invoking
those commands. Use Git Bash rather than WSL bash for these tests on Windows.
YAML/compliance checks and the focused Vite metadata test remain required.
No PR may open before fresh exact-head high-risk panel approval.
