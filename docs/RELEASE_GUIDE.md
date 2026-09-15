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

`consolidated-release.yml` is the sole server release entry point and its trusted
workflow definition always runs from `development`. Stable selects build source
from `main`; insider selects build source from `development`. The operator cannot
select a different control ref. The schedule dispatches the same trusted workflow
for ordinary insider releases. Branch pushes and direct tag pushes do not publish.
The reusable Docker workflow requires the signed record from this exact
workflow/run/attempt; arbitrary reusable callers cannot authorize a release.

`scripts/release.sh` and `scripts/publish-to-public.sh` are retired and exit
with status 2 for every invocation, including `--dry-run` and `--help`.
They do not merge, force-push `main`, create tags, publish GitHub releases or
upload assets/images. There is no private-to-public snapshot publishing path.
Review `VERSION` on the selected canonical branch and dispatch
[`consolidated-release.yml`](../.github/workflows/consolidated-release.yml).
Do not replace these helpers with direct Git or GitHub release commands.

The supported administrator journey is:

1. Open **Consolidated Release** and select `stable` or `insider`.
2. Leave **source_sha** blank to pin that branch's current HEAD once, or enter
   a full lowercase 40-character SHA that is either that HEAD or its trusted
   ancestor. The workflow definition remains pinned independently.
3. Click **Run workflow** on `development` and approve the one
   pending `release-stable` or `release-insider` transaction environment.
4. Follow the generated Actions summary. Qualification, evidence collection,
   allocation, ledger/tag work, publication and pointer advancement are
   automatic and fail closed.

The consolidated journey does not require separately dispatching CI,
qualification, or evidence-recorder workflows. Do not supply CI run IDs,
comment IDs, tags, fixture values, evidence values, or allocator values to the
consolidated journey, and do not author formatted commit comments or statuses.
An explicit source outside the selected canonical branch's ancestry is rejected;
once admitted, later branch movement does not retarget the source commit.

Qualification is bound to the current release run ID, immutable attempt-one
transaction, GitHub Actions App, check suite, workflow commit, and namespaced
reusable-workflow jobs. The
same authorization step re-reads that API evidence, exact-source required
checks, review status, evidence timestamps, and live strict branch policy after
environment approval. Receipts last 30 minutes and underlying evidence must
predate collection, remain within its own freshness window, and not be future
dated. Delayed approval beyond the receipt window requires a new dispatch.

By owner decision, release validation is limited to automated tests,
static analysis, code review, and fail-closed checks. No rehearsal or alternate
live diagnostic workflow, mode, environment, receipt, fixture, probe, or
operator ceremony exists.

`release-stable` and `release-insider` are the only publication environments.
Each real run creates exactly one deployment to the selected environment. The
same protected job verifies live policy, mints credentials, reserves identity,
creates the canonical source tag, signs and publishes the complete set, and
advances the channel pointer last.

| Channel | Base authority | Canonical version | Source tag |
| --- | --- | --- | --- |
| Stable | `main:VERSION` | `X.Y.Z` | `vX.Y.Z` |
| Insider | `development:VERSION` | `X.Y.Z-insider.N` | canonical version prefixed with `v` |

`VERSION` contains exactly `vX.Y.Z` and an optional final newline. Numeric
components have no leading zeros. Prerelease N is positive, never caller-assigned.
The release identity is allocated automatically; the dispatch does not accept a
version, stage, tag, or allocator input.

Stable is the installation default. Insider requires separate administrator
opt-in and a reduced-stability warning; running a release workflow does not
enroll or update any host. Versions and channels do not prove compatibility.

## Signed release-set consumer contract

The signed `release-manifest.json`, its signed envelope and the exact canonical
`release-notes.md` bytes define one complete immutable release set. Both
**Manual Update Now** and administrator-enabled **Auto-update** must download,
verify, and consume that same set before installation; neither may select a
different channel, tag, image subset, notes file, or unsigned metadata.

The release manifest's closed `consumption` object is metadata for consumers,
not an authorization grant. It records that Manual Update Now needs a
one-time host approval and Auto-update needs a bounded administrator standing
permission governed by #2665/#2666. The one protected publisher approval is
strictly publication-only: it cannot enable host outbound update checks,
select a host channel, approve a host update plan, or enroll any installation
in Auto-update.

The publisher loads one canonical `release-metadata/<base-version>.json` from
the qualified source. Its signed digest binds explicit source-release/channel
paths, minimum updater, frontend/mobile/backend/worker requirements, schema
read/write ranges and hashes, PostgreSQL/SQL Server AppDbContext and
SlicerDbContext migration heads, ordered backup/pull/verify/migrate/restart
inputs, and the rollback class. Missing, placeholder, malformed, or
non-canonical metadata blocks publication. This is a **producer contract**:
#2666 must enforce it when constructing the one immutable host update plan for
Manual Update Now and administrator-opt-in Auto-update. #2660 neither checks,
enrolls, nor updates a host.

The durable channel pointer is the authenticated discovery record for that
immutable set. It binds release ID, canonical version, channel, source commit,
allocation key, identity digest, manifest digest, envelope digest, and their
combined binding; it is written only after the public signed manifest has been
validated. A pointer cannot be reused for different signed bytes. Insider
sequences follow the durable ledger counter, while stable publication has its
own positive durable channel sequence; lower, equal, cross-channel, or
conflicting pointer sequences fail closed. Stable promotion records the exact
persisted insider pointer's release, source, and manifest/envelope digests,
never an unsigned complete-set hash.

The versioned trust policy is also signed by digest into the manifest. It fixes
the trust root, permitted signer identities and validity windows, certificate
freshness, revocation epoch/lists, and rotation overlap. #2666 must apply those
inputs at an explicit trusted verification time; reject expired, revoked,
unknown, replayed, downgraded, or rotation-invalid release sets; and preserve
durable per-channel high-water state. Those are consumer obligations, not a
publisher capability or authority to contact, enroll, or update hosts.

Before manifest signing, the publisher captures and verifies Cosign signature
and SPDX attestation results plus their downloaded signature/DSSE bundle
materials for every index and platform digest. The signed manifest binds the
exact raw verification, bundle, and predicate byte digests; formatted predicate
JSON is compared semantically, while its original bytes remain tamper-bound.
Bundle evidence is persisted as the raw Cosign NDJSON stream, never as a
reconstructed JSON array. The capture normalizes only a missing final newline
on the signature stream to one LF before concatenating that stream with the
attestation stream; all entry bytes and other separators remain unchanged.
Staging partitions that combined stream at the native signature/attestation
boundary and requires each persisted bundle byte-for-byte equal to its raw
partition before hashing or storage.
Retries and the final pre-alias verification must reproduce those exact
subject/platform-bound artifacts or publication fails closed.

Canonical authenticated release notes are an asset in that same set. The
single-dispatch pipeline generates them from the bounded previous-release-tag
to selected-source range, associated merged PRs, the matching `CHANGELOG.md`
entry, and `release-metadata/<base-version>.json`. They are hashed in the
signed manifest and presented before either journey installs.
The `CHANGELOG.md` entry heading must be exactly `## X.Y.Z` or
`## [X.Y.Z]`, with an optional ` - YYYY-MM-DD` suffix; exactly one canonical
header may match the release version.
Every release notes file has Features, Fixes, Breaking changes, Compatibility,
Migration, Downtime, Backup, and Recovery sections. A release with no
release-specific change must say `None.` or `N/A` explicitly. Compatibility,
migration, downtime, backup, and recovery are mandatory non-empty,
version-controlled metadata: missing data fails publication rather than
defaulting to a generic safe claim. No routine user notes, signing input, or
extra approval is accepted.

### Pinned source artifact line endings

The three metadata-hashed source artifacts are intentionally pinned to LF by
`.gitattributes`; their canonical Git blobs and manifest digests must not be
rewritten. If a Windows checkout reports CRLF working bytes, first ensure there
are no local edits to those exact paths, then refresh only them:

```powershell
git checkout -- scripts/docker/configs/security-config.json `
  scripts/docker/database-templates/postgres.yml `
  scripts/docker/compose-templates/docker-compose.common.yml
```

Confirm each reports `w/lf` with `git ls-files --eol -- <path>`. Do not use
repository-wide `git add --renormalize .`; it creates unrelated churn and is
not a release-metadata recovery step.

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

### Ledger schema recovery and migration

`state.json` is a closed schema. A ledger snapshot that predates
`channelSequences` or the signed manifest/envelope pointer fields is **not**
silently defaulted, reset, or accepted as current state. Recovery requires an
owner-reviewed release-control transaction on the protected ledger branch which preserves
the pinned anchor, every immutable reservation/identity/tag reference, and the
historical stable floor, then initializes channel sequences from those retained
records. `release-control` recognizes only the pre-sequence closed schema at
ledger read time, validates and normalizes it in memory, and persists that
single-parent transition through the normal compare-and-set on the next protected
transaction; it has no standalone reset command. The migration must be validated
as a single-parent ledger transition
before any new reservation, pointer, alias, or public-release write. If that
proof is unavailable, publication remains blocked and the owner must recover
the ledger from its protected history; creating a fresh ledger is not recovery.

One global decimal counter covers beta, insider and RC, across bases.
Workflow migration requires an owner-reviewed code/policy change; arbitrary
replacement workflow identities are rejected. Allocation keys include repository,
workflow ref, run ID, channel, stage, full source SHA and base. Same-run reruns
recover the immutable attempt-one transaction, reservation and evidence, bind
the current attempt, and revalidate every retained byte. They do not skip
qualification or approval and cannot allocate a conflicting identity. Missing
or mismatched recovery artifacts fail closed. Failed reservations remain consumed.
Stable has no N: a new run cannot rebuild an already reserved stable identity.
Resume byte-identical transfer in the same run, or qualify a reviewed new base;
never replace a stable identity with new build bytes.
An active insider reservation that cannot be completed may be terminally abandoned
only by selecting `abandon` in the same Consolidated Release dispatch and passing
its immutable allocation key as `reservation_target`; that dispatch calls the
existing protected Docker publisher and never invokes publication. The publisher
recovers the original signed authorization by its original run identity, then uses
the publisher App token to read the current protected environment approval. The
immutable abandonment authorization binds the active allocation key, source
commit, canonical version, channel, original authorization hash, exact current
workflow run/attempt/job/environment/target, normalized approval time, and a
current owner-allowlisted approver. The allocation key is the explicit approval
target and is echoed by the dispatch input; it selects the insider protected
environment and concurrency lane, never a mutable tag or source ref. Abandonment
is restricted to attempt 1 because GitHub approval history is run-scoped and
cannot prove an approval belongs to a later rerun attempt. GitHub's approval-history response has no
approval timestamp, so the normalized time is the authenticated protected
publisher job start time for the exact run and attempt, which occurs only after
environment approval. Missing, forged, stale, mismatched, or
wrong-environment approval evidence fails closed. The ledger projects only these
public-safe bindings; raw approval, reviewer, and protection data are never
projected. Abandonment consumes the original identity and sequence permanently,
does not publish a pointer or aliases, cannot be reversed, and permits a later
allocation only as the next identity. A missing, stale, forged, mismatched, or
already activated authorization fails closed.
Abandonment verifies the original signed ledger authorization and the current
protected environment policy, but intentionally does not require the original
source tag, `VERSION`, branch head, or source qualification to remain available.
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

Admission verifies trusted repository/event/workflow, pins the selected canonical
source and checks VERSION plus required exact-SHA checks. Later ordinary forward
branch movement neither fails nor retargets the release. Authorization and
pre-publication checks instead require the pinned source to remain an ancestor
of the current canonical head and reject trust revocation, pointer regression
or version regression. All builds use the pinned source SHA. An
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
optional `lastHistoricalStable`, `channelSequences`, and the `reservations`,
`identities`, `pointers`, `stages`, and `qualifications` maps. An
owner-reviewed schema migration and recovery procedure is required before
changing an existing durable ledger shape; recovery cannot recreate or weaken
retained allocation, pointer, or sequence evidence. Every transaction validates the complete
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
complete reservation through the signed release identity, canonical version,
channel, source commit, allocation key, identity digest, manifest digest,
envelope digest, and combined manifest-envelope digest; stage high-water
entries bind to real insider identities.
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
can reproduce it. Every ledger read/write recomputes it, but the consumer
discovery pointer binds the signed manifest and envelope rather than this
unsigned complete-set hash. `identitySha256` continues to cover the original
full authorization.
CAS and immutable-tag semantics are unchanged.

Owner-entered qualifications are strict public schema-1 records keyed by the
exact source commit. They contain `schema: 1`, matching `sourceCommit`, boolean
`reviewed`, `tests`, `compatibility`, `migrations`, and `recovery` claims (all
`true`), and `mode`. `mode: promotion` additionally requires `promotionOrigin` containing the
persisted insider pointer's `allocationKey`, `releaseId`, `sourceCommit`,
`manifestSha256`, and `envelopeSha256` of the qualified immutable insider set,
plus `treeEvidence`:

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
Same-run retries require the retained original transaction, qualification and
authorization files; if any are lost, fail closed rather than recreating evidence.
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
notices, SBOMs, identity, manifest, and digest records are published and
externally verified first; uploads never clobber differing bytes. Existing
version tags must resolve to the exact candidate digest or publication fails
before any version-tag write.

Each release also publishes a version-addressed `release-manifest.json` and a
separately signed `release-manifest.envelope.json` with its Cosign bundle. The
versioned manifest binds the authorization projection, canonical lifecycle
(publication, expiry, cadence, release-notes and signing identity), source-tag
and workflow/build provenance, every immutable image/index and platform digest,
signature and SPDX SBOM subject and verified trust-policy digest, identity labels, complete
service/platform compatibility, storage/configuration/template/updater steps,
provider migration heads, downtime/backup requirements, rollback strategy, and
the SHA-256 of the exact canonical `release-notes.md` asset generated before
manifest signing. The release is created from that same asset and its uploaded
bytes are checked against the signed hash before publication proceeds.
The envelope carries the manifest SHA-256 outside the manifest itself, together
with release ID, version, channel, source commit, and authorization hash. An
offline consumer must verify the envelope bundle against the pinned publisher
workflow identity, recompute the SHA-256 over the exact canonical serialized
manifest bytes, then validate that every
manifest identity, lifecycle provenance, evidence subject and platform label
agrees. Compatibility and migration claims are eligible only when this full
closed record validates; unknown, partial, mixed-subject, or invalid evidence
is rejected before signing and cannot become an update candidate.

The protected job signs the manifest envelope only after complete-set inspection
and pushed image signature/SPDX verification. It verifies published release
asset bytes on retries, then publishes immutable version tags and advances the
durable channel pointer last. The ledger pointer is the sole channel pointer;
stable/insider discovery aliases remain isolated and cannot substitute for the
signed, immutable manifest. Before promotion, it downloads the public manifest
and envelope again, verifies the signed envelope, byte-compares all three
manifest assets (manifest, envelope, bundle), and recomputes the manifest
digest from the downloaded bytes. If a retry finds an existing release, it
must recover and byte-compare those original assets and reuse the existing
bundle; it never re-signs or replaces public release artifacts. The manifest
schema rejects unknown complete-set, image, platform, and identity-label
fields rather than silently projecting them away.

Before source construction or upload, a pre-existing draft must have exactly
the source asset set or that set plus the signed manifest triplet. A new draft
is re-read and must be empty before its first upload. Empty, partial, duplicate,
mixed, or unexpected inventories fail before any release mutation. A complete
retry verifies the downloaded notes, manifest, envelope, and bundle against the
authorized bytes and Cosign trust identity before immutable tags can move. The
only authoritative manifest/envelope bytes are the signed
`release-authorization` artifacts; source-asset preparation does not create a
second unsigned copy.

The first attempt persists the complete signed triplet as a run-bound Actions
artifact before any public upload. A retry must recover the earliest unexpired
triplet from that same run, require its exact sorted inventory and byte-for-byte
match with the newly authorized manifest/envelope, verify its bundle, and reuse
it without re-signing. Missing, partial, expired, unexpected, or mismatched
recovery artifacts fail closed; public release assets are not a recovery source.

Before public assets, tags or version tags are written, publication preflight
revalidates ancestry, trust protections, version order, complete-set bytes and
the expected pointer snapshot. The final ledger transaction compares that
snapshot against the actual current verified branch head and stores the entire
validated set atomically. Ordinary forward movement is allowed; source ancestry
loss, trust revocation, pointer/version regression and same identity/different
bytes are rejected. A failed build/qualification/set check leaves the previous
pointer intact.
The ledger's candidate pointer is **not** authenticated update discovery.

After complete public-asset verification and immutable version-tag promotion,
the publisher derives aliases only from the validated signed record. Stable
releases may advance exact, major, minor, and `latest` aliases only to a
strictly newer stable SemVer value; an older hotfix line or an insider value
cannot replace them. Insider, beta, and RC releases publish only their exact
immutable tag; they never move stable aliases. Every alias is read again after
publication, so a concurrent conflicting write fails before the durable
channel pointer advances.

Before aliases or the durable channel pointer can move, the publisher performs
a fresh unauthenticated version-addressed GitHub release GET after undraft. It
requires the exact complete inventory, downloads and byte-compares every
public asset, validates the signed manifest/envelope/notes binding, and
rechecks every manifest-pinned index and platform Cosign signature,
attestation, normalized DSSE bundle, and SPDX predicate against the registry.
Evidence staging rejects a revoked release or signer, an issuer/identity
mismatch, absent signed transparency material, transparency time outside the
certificate or signer rotation window, a pre-revocation epoch entry, or a
certificate older than the control policy maximum. These are publication
preconditions only; #2666 remains responsible for consumer-side trusted-time,
replay, rollback, and update execution enforcement.

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
- `release-stable` and `release-insider` are the only protected publication
  environments.
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
check-suite fields. Check/status evidence is collected in pages of 100 with
stable totals, unique IDs and exact-source pagination links. Missing pages,
changed totals, duplicate IDs, redirects and foreign links fail closed.

The PR producer still posts only to the reviewed PR head. Release admission
additionally requires the canonical qualification below; an ordinary PR verdict,
owner override or carried-across-sync status cannot substitute for it. The low-level
check adapter understands both status vocabularies, but release admission and
each allocation retry require the completed canonical workflow audit chain.

### Retired manual canonical qualification history

> The procedure below is retained only as historical design context. It is not
> a supported operator path. `consolidated-release.yml` now starts the reusable
> canonical CI graph and collects exact-SHA checks/review evidence automatically.
> Do not dispatch these retired workflows or create formatted commit comments.

The qualification mechanism has three deliberately separate parts:

1. Manually dispatch the existing **CI** workflow on the canonical branch:
   `main` for stable, `development` for insider. This executes the full-safe
   selector, tooling, frontend build/lint/tests, .NET build/tests, provider tests,
   migration drift and dependency validation, plus `path-casing`,
   `Contract drift gate` and `Build (iOS)` in the **same CI run/check suite**.
   Wait for every job to finish successfully before reviewing.
2. Review the **actual canonical SHA**, after CI completes, and post the fresh
   confirmation described below as a comment on that commit. Do not copy a
   PR-head verdict, infer review from tree equality, or name invented reviewers.
3. Dispatch **Qualify canonical release** from the repository's **default
   branch**, supplying the channel, CI run ID, commit-comment ID and native PR
   number (`0` in single-maintainer mode). No SHA/ref input selects the target:
   the verifier independently resolves the live canonical HEAD.

For example, these commands only start validation and qualification; neither
invokes the release publisher:

```bash
gh workflow run ci.yml --repo OlyForge3D/PrintFarmer --ref main
# After CI finishes and the fresh canonical commit review is recorded:
gh workflow run qualify-canonical-release.yml --repo OlyForge3D/PrintFarmer \
  --ref development -f channel=stable -f validation_run=<ci-run-id> \
  -f review_comment=<commit-comment-id> -f native_pr=0
```

The second command uses the current default (`development`), even for stable.
If the default changes to `main`, select `main` there instead. Other default
branches fail closed. Requalify after either the source or trusted default HEAD
changes. Do not dispatch the verifier from a feature branch or an arbitrary SHA.

The commit confirmation is an exact, unfenced six-line body, without extra text
or a final newline. Only the canonical LF body or its fully CRLF-transformed
equivalent is accepted; mixed LF/CRLF, lone CR, blank lines, duplicate fields
and extra text remain invalid. Replace the two placeholders with the full lowercase SHA
and decimal CI run ID; the attempt is always `1`:

```text
Canonical-Qualification: v1
Source-SHA: <full-canonical-sha>
CI-Run: <ci-run-id>
CI-Attempt: 1
Approval-Mode: single-maintainer
Review: owner-confirmed-self-attested
```

Use the commit page or `POST /repos/OlyForge3D/PrintFarmer/commits/{sha}/comments`
under the reviewer's own authenticated account, then retain its comment ID.
The owner must actually confirm the fresh Squad review of that canonical SHA;
the declaration is **self-attested**, not native independence, separation of
duties, or a four-eyes control. The initial implementation accepts `jpapiez`
only, verified live as an administrator. Adding another owner-confirmation
account requires a reviewed policy change, not a workflow input.

In `separation-of-duties`, change the last two values to
`separation-of-duties` and `native-code-owner-non-self`, and provide `native_pr`.
The commenter must hold live write-or-better permission, be a canonical
CODEOWNER, and have a fresh native `APPROVED` review on that PR's exact canonical
head SHA after CI. They must differ from the PR author and both CI/qualification
initiators. All of these identities must have valid, non-empty GitHub logins;
missing, deleted, or malformed identity evidence fails closed. Comparisons are
case-insensitive. The PR author and CI/qualification initiators may share an
account, but none may be the reviewer. Native review identities must also be
valid before approval/change-request reconciliation.
Later change requests or dismissal invalidate evidence.
The PR must belong to this repository and target the selected canonical branch.
The implemented ownership parser supports the repository's final user-only
catch-all `* @login` rule (which overrides earlier rules). Team ownership or a
pattern-only layout requires reviewed support; it never silently falls back.
A squash predecessor is **not** the canonical SHA. If no appropriate exact-head
native PR evidence exists, separation-of-duties stays blocked: obtain genuinely
eligible native review through an owner-approved branch/merge process, never
replay a pre-squash approval or switch modes merely to evade the requirement.

Every CI job must succeed, including the required named jobs. Live applied
branch rules must retain strict checks, all release build contexts,
`squad/pre-pr-verdict`, `path-casing`, `Build (iOS)` and `Contract drift gate`,
in both approval modes and on both channels. Removing any mandatory context
blocks qualification and release consumption even if its CI job/check is green.
Every additional configured check must also have executed in that CI run:
exactly one job and one check in its suite must match; job URL, check-suite ID,
SHA and optional integration ID must agree. Separate workflow runs are not
accepted, even when they have green checks with identical names on the same SHA.
No ruleset contexts are removed or status results copied.

Manual CI executes the existing path-casing command on Linux and the existing
contract-drift corpus/self-tests on Linux. Producer coupling examines the selected
commit's first-parent delta (including a squash commit's complete change), rather
than treating dispatch's absent event diff as an empty change. An unavailable
parent/diff fails closed. This is not a review of all historical changes.
The iOS job runs the same marketing-version checks and **real unsigned Release
archive** as the PR build on macOS; there is no iOS selector or skip path for
manual CI. It needs Xcode/package access, not signing or publishing credentials.
Structural tests keep these execution steps equivalent to their PR workflows.

Only `workflow_dispatch` with `github.ref` exactly `refs/heads/main` or
`refs/heads/development` executes these canonical jobs and emits required names.
Every other ref/event, including a feature-branch manual dispatch on a PR head
SHA, gives these unselected jobs distinct `Canonical … (not selected)` names,
so they cannot shadow the standalone PR required checks. The manual summary
gate uses the same canonical-ref condition. Matching tag names are not branches.
The existing iOS PR selector still may skip Xcode on unrelated PRs; such a green
PR check is **not** canonical archive evidence. Manual CI does not run Windows
builds, iOS simulator unit/XCUI tests or TestFlight packaging. Linux and macOS
results must not be represented as Windows or simulator validation. A newly
required context outside this graph blocks qualification until reviewed execution
support is added; never substitute an unrelated run.

CI and review evidence expire 24 hours after CI creation. Only attempt 1 is
accepted; **all reruns, including failed-jobs-only reruns, require new CI and a
new confirmation**. Any newer CI run for the SHA or newer qualification for
the channel supersedes the old evidence, including failed/cancelled runs.
A CI run may be named by only one qualification run. Edited confirmations,
missing/unknown modes, mode drift, unavailable permission reads, truncation,
partial checks and stale HEADs fail closed. CI runs/jobs, checks, statuses,
commit comments and native reviews are collected in pages of 100, with a
100-page budget per collection, including any empty terminal probe.
Counted lists must agree on totals across pages;
uncounted lists require a short terminal page (an exactly full final page needs
an additional empty-page read). On that verified empty uncounted probe only,
with no `next` link, `last` may point to the preceding page. Links accept only
`/repos/OlyForge3D/PrintFarmer/` or `/repositories/1044049720/` on the GitHub API
origin, with the same endpoint and query filters. Links are validated, never
followed; every request is synthesized locally. Pagination cannot change the
repository, source SHA, run, attempt or query filters. Qualification history
remains time-filtered from the selected CI creation, not an unbounded lifetime count. Policy lists
retain their existing single-page bounds. Exceeding a bound requires a reviewed
extension rather than deleting audit evidence.
The macOS archive must finish within that same 24-hour window; runner queue
time does not extend evidence lifetime. Land this graph on each canonical branch
before qualifying that channel. A previous 38-job test execution lacks the three
executions and cannot be repaired with extra statuses: dispatch new CI and review
the new exact HEAD. Local tests verify the graph and evidence rejection, not a
live canonical CI/archive execution.

**Stable activation blocker (live read, 2026-09-13):** `main` currently requires
the three release build contexts and `squad/pre-pr-verdict`, but lacks
`path-casing`, `Build (iOS)` and `Contract drift gate`; `development` requires
all three already. Stable qualification remains blocked until the graph is
merged to `main` and the owner adds those three contexts to its live strict
ruleset, retaining all existing requirements. Read back the applied policy,
then run fresh canonical CI and review. This revision does not change live
rulesets or authorize activation; do not weaken the verifier to accept the
current stable policy.

**Evidence writer:** `record-canonical-qualification.yml` runs from the trusted
default branch after qualification completes. It revalidates the entire chain,
then posts only `squad/pre-pr-verdict` on the resolved canonical SHA, with
`QUALIFIED (self-attested)` or `QUALIFIED (native non-self)` and the actual writer
run URL. That run links to the qualifying run, whose title identifies CI and
review records. No reviewer identities or private raw policy are written to
normalized output; the public audit links themselves are not private.
Failed/cancelled qualification is reconciled to a bounded failure status when
the trusted source can still be resolved. HEAD movement during posting retracts
success. GitHub has no atomic HEAD/status/run-completion transaction: cancellation
immediately after POST can leave a visually green raw status. **It is never
release authority**: admission additionally requires the writer to have completed
successfully and rereads live HEAD, runs, jobs, review, mode and status provenance.
In-progress, cancelled, superseded or forged evidence is rejected before any
reservation, including each CAS retry. Do not use the status color alone.

The verifier is read-only. Only the separate evidence writer has `statuses: write`;
neither has release environments, publisher App credentials, package/content
writes, OIDC, deployments, ledger access or downloaded executable artifacts.
Checkout is pinned to the trusted workflow SHA with credentials unpersisted.
The API client allowlists reads and one fixed status context; it rejects redirects
and all publish, tag, ref, dispatch and deployment writes. Candidate CI code
executes only in the existing read-only CI workflow, never with the writer token.
`push` and `repository_dispatch` cannot invoke qualification; `workflow_run`
payloads are hints that must match live repository/workflow/run data.

This path changes no release protection profile, signed identity schema/digest,
ledger schema or publication policy. #2679/#2683/#2685 branch/environment/tag,
publisher, ledger-continuity and package-isolation controls remain mandatory.
After merge, #2668 still requires owner-approved ledger seed/anchor/continuity
and package ACL verification. Publisher App/registry credential
provisioning remains a **private, separate owner step**. No secret value is
needed to qualify; never put credentials in commit comments, issues or artifacts.

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
ACL isolation and automated negative-path tests remain mandatory.

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
   manual reviewer approval under the explicit mode above and allow only the
   immutable `development` workflow control branch. Configure the variable and any owner-approved
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
   Store these credentials only on `release-stable` and `release-insider`.
   Those are the single human gates and the only publication environments.
   Their deployment branch policy allows the immutable `development` workflow
   control ref for both channels; the selected source remains independently
   pinned to `main` for stable or `development` for insider.
6. Read the effective policies back and verify the automated fail-closed tests
   before first authorized publication. Release jobs request no
   repository-token contents or package write scope: the protected App
   publishes source assets and the protected package credential publishes
   application images.

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
node --test scripts/ci/tests/test-release-transaction.mjs scripts/ci/tests/test-github-evidence-pages.mjs
```

Fixtures execute admission denials without writes, positive stable/insider
paths, numeric ordering, atomic contention, retry/attempt/migration
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
consumer, App-only fixture modules, and separate `ios/` TestFlight writers.
This is a source regression check, not a substitute for repository protection
or runtime authorization.
Both retired server helpers execute against sentinel publication commands for
normal, dry-run, help and force arguments; every call exits 2 without invoking
those commands. Use Git Bash rather than WSL bash for these tests on Windows.
YAML/compliance checks and the focused Vite metadata test remain required.
No PR may open before fresh exact-head high-risk panel approval.
