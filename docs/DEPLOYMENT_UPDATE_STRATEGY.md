---
post_title: "Deployment visibility and safe update strategy"
author1: "Parker"
post_slug: "deployment-update-strategy"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["deployment", "updates", "design"]
ai_note: "AI-assisted repository audit synthesis; not implementation approval."
summary: "A staged plan for truthful service inventory, compatible release alerts, and recoverable operator-approved updates."
post_date: "2026-09-12"
---

## Recommendation and scope

Deliver **read-only installed-version inventory first**, followed by compatible
release alerts. Make an **operator-approved, host-run updater** the first
execution path. Prefer an explicitly enrolled **external pull reconciler** for
later unattended operation. A UI-triggered host helper is optional, last, and
must use the same recovery engine rather than a second deployment mechanism.

The updater architecture is a proposal, not a supported updater or permission
to deploy. The branch/channel publication baseline below is already implemented
in the inspected local worktree; it is not evidence of merged or deployed CI.
Evidence is the supplied deployment, backend, frontend, and architecture audits,
anchored to repository revision
`8b2c0dfc0cdd4e1d3f633bcbcf4db4ac05ea9a3f`, plus the existing uncommitted
release-workflow, release-guide and test changes inspected on 2026-09-12.
“Current” denotes audited behavior or explicitly identified local implementation.
Target updater contracts, routes and remaining delivery increments are
**proposed**; channel-policy decisions fix their defaults and safeguards.

Scope: single-host Docker Compose, monolith and split-service deployments,
optional/local/remote workers, external databases, and offline installations.
The [provider matrix](DEPLOYMENT.md#database-configuration) permits PostgreSQL
and SQL Server for Compose; SQLite is native/local only, and MySQL is not
supported. Native development remains native and has visibility, not automatic
container replacement. Multi-host worker upgrades require separate enrollment.

Non-goals: Kubernetes orchestration, printer firmware updates, database engine
major upgrades, arbitrary third-party add-on updates, zero-downtime promises,
rewriting the installer, or implementing production changes in this task.
Physical printer actions are never reversible through deployment rollback.

### Required release-channel decisions

The user-approved channel policy is a requirement, not an unresolved default:

- Support distinct `stable` and `insider` release frequencies. `stable` is the
  default for both existing and new installations. Missing legacy selection
  initializes to stable; an installed prerelease, mutable tag or daily build
  never enrolls an installation into insider implicitly.
- `insider` requires explicit administrator opt-in and confirmation of a clear
  warning: **Insider updates may arrive more frequently and have reduced
  stability compared with stable releases.** Keep that state visible after
  enrollment. Channel choice does not enable outbound checks or execution.
- Selection governs discovery and the complete coordinated application image
  set. Never mix stable and insider service images, even when protocol ranges
  permit version skew. External infrastructure retains its declared constraints.
- Channel switches require compatibility/preflight, confirmation, audit history
  and no silent downgrade. Returning to stable is not permission to install an
  older or schema-incompatible release; wait for a supported stable target or
  use an explicitly approved, compatible recovery plan.
- Manual/offline metadata preserves and verifies the selected channel. Cadence,
  notifications, rollback/recovery and tests must distinguish both channels.

This extends the approved planning scope without authorizing implementation,
deployment or privileged automation. Exact operational intervals and permission
mechanics remain maintainer decisions; the stable default and insider safeguards
do not.

### Implemented branch/channel publication baseline

**Revision status (2026-09-12, #2668):** the original audit descriptions below
are historical baseline evidence, not the current branch implementation.
The [release guide](RELEASE_GUIDE.md) now documents one consolidated authority,
strict stable/insider/beta/RC grammar, protected Git-CAS reservations, signed
exact-SHA authorization, shared assembly/frontend/OCI identity and complete-set
candidate CAS. Direct tag publication, legacy release helpers and the daily
registry publisher have been removed. Existing historical aliases are unchanged.
The branch remains **activation-blocked** on explicit owner approval and missing
live rulesets/environments/ledger/publisher setup. #2660 still owns signed
managed eligibility, image aliases and generated installer references; no
source-only release or unsigned candidate pointer is an update candidate.

**Canonical qualification (#2686):** the
[non-publishing qualification path](RELEASE_GUIDE.md#non-publishing-canonical-qualification)
requires new full-safe manual CI on live `main`/`development` HEAD and a fresh
commit/run-bound review confirmation. Default-branch verification and a separate
bounded status writer establish a live, completed audit chain; no PR verdict is
copied to a squash SHA and no tree-equality review inference is permitted.
Single-maintainer confirmation is honestly owner-confirmed/self-attested.
Separation-of-duties additionally requires eligible native code-owner/non-self
approval on that exact canonical SHA; missing evidence remains blocked.
Newer runs, reruns, mode drift, edited review, failed checks or HEAD movement
invalidate qualification. Publication admission revalidates the chain before
reservation and each CAS retry rather than trusting a green status alone.
Qualification has no release credentials, environments, OIDC or ledger writes.
It does not complete #2668 activation: ledger/package rehearsals and private
owner publisher-credential provisioning remain separate.

**Approval corrections (#2682, #2684):** activation under #2668 requires the
explicit `RELEASE_APPROVAL_MODE` policy. `single-maintainer`
requires manual approval by the owner or an explicitly owner-approved user,
with self-review prevention disabled; its assurance is honestly
owner-confirmed/self-attested, never separation of duties. The alternative
`separation-of-duties` mode requires self-review prevention and at least one
eligible reviewer, plus native branch code-owner review and at least one
non-self native PR approval. Single-maintainer instead requires zero native
approvals and no code-owner/last-push approval requirement; its PR-only branch
flow uses the exact-SHA `squad/pre-pr-verdict` status and build checks,
conversation resolution and no bypass/force-push/deletion. Both modes retain
these branch/check controls and require the live environment response to explicitly
report `can_admins_bypass: false` before reservation; required reviewers alone
are insufficient. Missing/unknown modes and admission/authorization mode drift
fail closed. Mode-specific normalized schema 5/v4 claims distinguish
`selfAttestedReviewRequired` from `codeOwnerApprovalRequired` and
`nonSelfApprovalRequired`, include administrator bypass prevention, and expose
no reviewer identities or raw policy evidence. Earlier evidence fails closed.
The current review producer targets open PR heads, not subsequent squash commits:
canonical-SHA review evidence remains an explicit #2668 activation prerequisite,
never a status copied from another SHA or a fabricated independent approver.
See [approval configuration](RELEASE_GUIDE.md#explicit-release-approval-configuration)
for private delegation evidence and cutover. All other #2679 controls, including
package ACL isolation and negative rehearsals, remain unchanged.

The local #2668 implementation establishes the following publication policy,
as documented in the [release guide](RELEASE_GUIDE.md#release-channels-and-branches):

- Stable dispatch uses `main` and `vX.Y.Z`. Consolidated release rejects
  branch/channel/version mismatches; the legacy release workflow is stable-only.
- Insider dispatch uses `development` and `vX.Y.Z-insider.N`. Mobile prerelease
  tags use the separate `ios/vX.Y-beta.N`, `ios/vX.Y-alpha.N`, or
  `ios/vX.Y-rc.N` namespace and cannot trigger container publication. `VERSION`
  supplies the server numeric base; the insider suffix is supplied at dispatch.
- Docker final promotion adds `stable`, `latest`, major and minor pointers for
  stable tags; insider promotion adds only the `insider` channel pointer
  alongside the exact prerelease tag. Both Docker metadata blocks include
  `org.printfarmer.release-channel`.
- No permanent extra release branch is needed. Optional short-lived
  `release/vX.Y.Z` branches receive full-safe CI, merge to `main` before stable
  publication, and merge stabilization fixes back to `development`.

This is the implemented baseline, **not completion of every #2668 criterion**.
It does not implement a durable insider allocator, exact-SHA authorization for
every publishing entry point, verified complete signed sets, compatibility
manifests, installed inventory, update checks, or an updater. The evidence and
remaining publication-path gaps below delimit what can be claimed.

**Issue reconciliation completed:** #2658, #2660, #2661 and #2668 now use a
disjoint release namespace: server insiders use `vX.Y.Z-insider.N`, while
TestFlight uses `ios/vX.Y-{alpha,beta,rc}.N`. An isolated mutable `insider`
pointer is discovery convenience only, never canonical installed identity;
canonical identity is the prerelease version plus immutable digests and
provenance. Complete exact-SHA/all-entry-point authorization, durable allocation
and publication-bypass closure, complete signed coordinated sets and end-to-end
verification remain implementation gaps. #2668 remains a native epic child
and blocks #2660. Graph PASS verifies relationships, not implementation closure.

### Canonical release identity and single version authority

The following coordinated identity contract is normative. Its #2668 identity
and authorization prerequisite is implemented on this revision branch, but
owner acceptance, live activation and #2660 distribution remain blocked.
It must preserve the disjoint server/mobile namespaces and channel-pointer isolation.
Maintainer acceptance of counter/ownership choices gates this future work,
not the already implemented stable/main or insider/development dispatch policy.

1. **Authored input:** root `VERSION` at the exact source commit is the sole
   release-base authority, written as `vX.Y.Z` with valid SemVer numeric
   components. Strip exactly the leading `v` for canonical `baseVersion`.
   On `development`, it names the next intended stable base and must exceed
   the last published stable base. A reviewed version change, not a tag or
   component project file, selects that base.
2. **Trusted derivation:** one authoritative release workflow resolves the
   source commit, branch, base and build identity once. Stable is `X.Y.Z`,
   tag `vX.Y.Z`, and only publishes from a protected `main` commit. The
   requested tag must exactly match that commit's `VERSION`; mismatch fails
   before write credentials or publication. Insider is
   `X.Y.Z-insider.N`, tag `vX.Y.Z-insider.N`, only from `development`.
   Server `beta.N` and `rc.N` are also insider identities; all stages share N
   and progress in SemVer order `beta < insider < rc`, without same-base stage
   regression. Mobile
   alpha/beta/RC identities remain independent under `ios/` and are excluded
   from server release discovery. `N` is one positive decimal integer, without
   leading zeros, reserved by a
   durable atomic insider allocator shared by all authorized entry points.
   It increases across base changes and workflow replacements; gaps are valid.
   The allocation key is trusted repository + workflow identity + run ID +
   run attempt + full source SHA + base version. Retrying the same key returns
   its existing reservation; a new run/attempt reserves a greater N.
   Store the reservation before creating the source tag; never recycle failed
   reservations. Record run ID, attempt and full SHA separately, not in the tag.
   Already published bytes may only be reused identically, never overwritten.
3. **Derived authority:** emit one immutable release-identity build record
   (`releaseId = channel:canonicalVersion`, `channel`, `canonicalVersion`,
   `baseVersion`, `sourceBranch`, `sourceTag`, `authorizedBranchHead`,
   `sourceCommit`, `buildId`, `buildAttempt`, `workflowIdentity`). All jobs
   consume this record; none re-derive versions
   from wall clocks, tags, branch-name guesses or individual project defaults.
   The signed release manifest is the distribution authority binding this
   identity to artifacts. Tags and release-page titles are checked outputs,
   not competing inputs. Changed bytes require a new identity.
4. **Propagation:** backend `AssemblyInformationalVersion` carries
   `canonicalVersion+sha.<fullCommit>` and assembly metadata carries the
   canonical channel/release/build fields. Numeric assembly/file versions may
   be a documented base-version projection, never the installed identity.
   Frontend build metadata embeds the same canonical fields, not its package
   version or a request-time API version. API, frontend, discovery, slicer-host,
   managed workers and monolith all share identical release/channel/version/
   source/build metadata; their individual image digests naturally differ.
5. **Publication outputs:** `org.opencontainers.image.version` equals
   `canonicalVersion`; OCI `revision`, `source`, `created` and namespaced
   release/channel/build labels bind the record to every image and platform.
   Immutable version tags, release records and signed manifest carry the same
   identity; provenance names the verified source/workflow and digest subjects.
   Verify both canonical and optimized Dockerfile paths. Installer app-image
   selections in `container-versions.conf` become generated manifest references,
   not a second app-version authority; it retains infrastructure-default
   ownership. Generated Compose pins `repository@sha256:...`.

**Canonical installed identity** is the verified channel + canonical version +
release ID + source commit + manifest digest and per-service running platform
digests, with observation provenance. `latest`, `stable`, `insider`, major/minor
tags and date aliases are **mutable discovery aliases only**. Resolve an alias
through authenticated metadata once, then record the canonical identity and
immutable index/platform digests before planning or installing. A tag alone,
even `vX.Y.Z`, is insufficient installed evidence. Never resolve an alias
again during apply, rollback or restart. Unknown legacy identity remains
unknown; do not relabel it from the selected channel.

### Branch enforcement, source-first publication and promotion

- Protect `main`, `development`, version/tag namespaces and publishing
  environments with reviewed changes, required checks, restricted publishers,
  and no unreviewed force-push/tag replacement. Release-policy/workflow changes
  need designated-owner review; record allowed actors and bypass policy.
  Publication must verify trusted repository, resolved commit and branch
  relationship at runtime for push, tag, manual, scheduled and reusable calls.
  A matching tag name alone cannot prove origin. Fork/PR/feature-branch events
  never acquire publishing/signing authority. Short-lived stabilization
  branches may merge into `main` and back to `development`, never publish.
- Serialize publication per channel and reject stale build/sequence pointer
  advancement, including an older run rerun after a newer release. Required
  tests, version validation and source-first gates precede image publication:
  source commit and immutable source tag must be remotely available first.
  A draft/incomplete release record is not a discoverable install candidate.
  Then build every service/platform, verify labels/digests, SBOM and provenance,
  publish/verify attestations, and publish signed manifest and channel pointer
  last. Partial failure leaves the previous pointer intact. Insider cannot
  advance stable aliases, including `latest` when retained as a stable alias.
- Promotion selects an immutable insider manifest/digests and records the
  candidate's commit, test evidence and compatibility/migration results.
  Merge reviewed source into `main`; require the main source tree to match
  the approved candidate except reviewed stable-version/metadata changes.
  Any other change needs renewed qualification. Validate main's `VERSION`
  and protected stable tag, then **rebuild the complete stable set from main**
  with stable metadata and rerun source-first, compatibility, migration,
  recovery and publication gates at that exact main SHA.
- Do not retag insider images as stable: embedded frontend/backend/OCI
  identities would still say insider. A new stable manifest records
  `promotionOrigin` (insider release ID, canonical version, commit, manifest
  digest and qualification evidence) alongside the new main commit and
  stable digests. Stable and insider releases remain independently verifiable;
  promotion never mutates old manifests or enrolls installations. Main fixes
  and version changes are reconciled back into development through review.

For the remaining coordinated-identity work, the maintainer names the authoritative workflow,
release-policy owners and allocator storage/transaction/continuity mechanism.
The one-integer grammar and allocation semantics above are fixed; the proposed
protected Git-CAS implementation is not implicitly owner-approved. Missing or rolled-back allocation
state blocks publication until trusted continuity is restored, even after a
base bump. Do not approximate N with concatenated counters, timestamps or a
local tag scan. New attempts of older source commits cannot advance insider
discovery: require the selected current development SHA at admission and
reject stale-source publication at pointer advancement. Serialize admission
and channel advancement, with durable compare-and-set high-water checks;
workflow concurrency alone does not guarantee ordering.
#2660 separately gates signing/trust and offline verification choices.

### Branch topology decision and historical baseline evidence

**Decision: no permanent per-track release branches.** `development` is the
insider integration line; `main` is the stable line. The following follow-up
observations were read on 2026-09-12 at the same HEAD as the audit above, with
pre-existing local workflow/script/doc edits present. Those edits were inspected,
not changed, and are not evidence of deployed enforcement.

| Exact source | Observed behavior and implication |
| --- | --- |
| `.github\workflows\ci.yml` (`on.push.branches`); `scripts\ci\select-dotnet-tests.sh`; `scripts\ci\tests\test-select-dotnet-tests.sh` | Local changes add `release/**` pushes and full-safe selection, including a `release/v1.2.3` fixture. This is candidate validation, not a reason for permanent release channels. |
| `.github\workflows\consolidated-release.yml` (`validate-and-tag`) | Implemented channel input/guards bind stable dispatch to main and insider dispatch to development. Mobile/TestFlight publication is separate. Dispatch captures `github.sha`, checks out that commit and creates the tag explicitly at it; stronger protected-branch authorization evidence remains future work. |
| `.github\workflows\release.yml` (`tag-and-build`, exact-tag Docker wait) | Local stable dispatch checks main and VERSION; the downstream wait matches tag and commit. Preserve that matching but bind tag creation and every checkout to the selected main SHA. |
| `.github\workflows\docker-publish.yml` (`on.push`, `Resolve source metadata`, `Resolve promotion tags`) | Publication accepts only exact stable or insider tag pushes; release-branch and manual-dispatch publication paths are removed. Direct tag publication still needs stronger publisher and protected-branch authorization evidence. |
| `.github\workflows\docker-publish.yml` (both metadata-action blocks and `promote-images`) | Both blocks add `org.printfarmer.release-channel`. Final promotion isolates stable/latest/major/minor from insider and intentionally moves `insider`. The `ios/` namespace cannot enter this workflow. Metadata-action patterns still need executable generated-tag coverage. |
| `.github\workflows\daily-development-images.yml` (`source`, publication jobs) | Resolves/checks exact development HEAD; uses `development-<sha12>` build metadata and `sha-<sha>-run-<id>-attempt-<n>` tags with six-image `image-set.json`. Reuse exact-SHA handling, but migrate identities to canonical insider SemVer before managed discovery. |
| `scripts\bump-version.sh`; `scripts\sync-monorepo-version.sh`; `VERSION` | Bump tool supports only the server `insider` prerelease suffix and a local tag-scan sequence, then commits/pushes the current branch; sync checks only numeric base and web package version. Neither proves channel origin nor implements a durable insider allocator. VERSION currently contains `v0.2.3`. |
| `docs\RELEASE_GUIDE.md`; `scripts\ci\tests\test-release-tag-triggers.mjs` | Guide records the baseline; tests cover stable/insider container triggers, disjoint `ios/` TestFlight triggers, branch guards, isolated promotion text, OCI channel labels and stable-only legacy dispatch. Executable event/alias/commit-race coverage remains future work. |

No inspected workflow requires simultaneous supported stable maintenance lines.
Permanent branches would duplicate VERSION ownership, protection, backports
and publication authorization without solving artifact identity. Narrow
`release/**` to reviewed, optional `release/vX.Y.Z` candidate lifecycle and
full-safe validation, never a third channel; all release branch refs are
non-publishing. A future multi-version maintenance
commitment requires a separate explicit support-policy decision.

### Original tag-validation gaps (superseded by #2668 revision)

Stable tags use `vX.Y.Z`; insider tags use `vX.Y.Z-insider.N`. TestFlight tags
use `ios/vX.Y-beta.N`, `ios/vX.Y-alpha.N`, or `ios/vX.Y-rc.N`. Current server
baseline dispatch regexes accepted decimal numeric components, including zero
and leading zeros. The revision's executable `release-policy.mjs` rejects these,
and all supported server stages use protected durable allocation. Namespace
isolation remains mandatory.
Existing tags and daily artifacts retain their original identity; publication
channel classification alone never proves managed eligibility or enrollment.

The authorization requirements below remain future hardening, not guarantees
provided by the current branch-name guards.

Resolve and peel annotated/lightweight tags to a full commit SHA. Verify the
trusted repository, event, authorized workflow/caller and protected release
authorization record; bind `sourceBranch`, `sourceTag`, `sourceCommit`,
`authorizedBranchHead`, source tree and build identity in signed provenance.
At admission, stable source must equal the protected main HEAD, insider source
the protected development HEAD. A tag commit merely reachable from main is
insufficient: development ancestry may also be reachable. Tag creation uses
that exact selected SHA, and downstream checkout/build uses SHA, not branch HEAD.
If HEAD moves before tag authorization, reselect/requalify; after authorization,
the immutable authorized SHA stays valid without chasing the branch.

| Event | Required runtime gate |
| --- | --- |
| Branch push | Development may admit insider through the allocator; main may validate but cannot publish stable aliases without an authorized exact stable tag. Release/feature/PR/fork refs never publish. |
| Tag push | Strict grammar, VERSION/base equality, peeled tag SHA equals approved source SHA, trusted branch-at-authorization evidence and publisher identity. Direct unauthorized tags fail even when their commit is reachable from main. |
| Manual dispatch | Canonical selected branch/channel only; resolve once, validate VERSION and tag request, allocate insider N centrally. Caller-supplied suffix/ref/sequence is not authority. |
| Schedule | Trusted workflow revision selects/pins development HEAD explicitly; default-branch event context is not provenance. Stable scheduled publication is denied. |
| Reusable call or retry | Validate trusted caller and the same immutable authorization record; no bypass via supplied channel/SHA. Reruns obey allocator immutability and stale-pointer rules; moved/deleted/recreated tags fail verification. |

All gates run read-only before write/signing authority. Restrict branch, tag
creation/deletion/update and publishing environment permissions; require
review/checks at the exact commit, named policy owners and audited bypasses.
Record live ruleset/environment evidence for the remaining #2668 criteria;
this reconciliation changes none.

### Publication aliases and stabilization lifecycle

#2660 extends the implemented isolated channel pointers with a verified
publication allowlist across every metadata/build/promotion
path. For a valid authorized main tag `vX.Y.Z`, image tag `X.Y.Z` is immutable;
`X.Y`, `X`, `latest` and retained `stable` may advance only after complete
stable verification. Compare SemVer within each alias's scope: an older-line
hotfix may advance its own minor alias but cannot regress global latest/stable
or a major alias already pointing to a newer minor. No branch/manual/tag
spelling shortcut may advance them.

For insider releases, retain the exact prerelease image tag and isolated mutable
`insider` discovery pointer; never advance stable exact/minor/major/latest/stable
aliases. Tags under `ios/` never publish container images. A future signed insider **metadata channel pointer** advances last to
a complete manifest; it is distinct from the existing OCI image pointer.
Neither mutable pointer is installed identity: managed installs must resolve
and retain verified immutable manifest/image digests.
Tag enumeration tests must cover both metadata-action blocks and final pushes,
not rely on prerelease handling hidden inside a third-party action.

When isolation is necessary, branch `release/vX.Y.Z` from the selected qualified
development candidate; record owner, target, creation and expiry/removal gate.
Accept stabilization fixes only through review and full-safe CI. Merge to main,
requalify the resulting exact main commit and tag it `vX.Y.Z`; source tree
differences beyond approved release metadata require renewed qualification.
Merge every stabilization fix back to development via reviewed PRs, even if
development has already advanced its VERSION; do not regress its base.
Delete the short-lived branch only after main publication or documented
abandonment, merge-back evidence and recovery/provenance retention are recorded.
If abandoned, still reconcile applicable fixes and retain the decision.

Urgent stable fixes branch short-lived from main, receive equivalent tests and
recovery qualification, merge to main and publish a new exact stable tag.
Record direct-hotfix reason instead of fabricated insider promotion origin.
Merge fixes back to development and any active stabilization candidate; record
equivalent changes when cherry-picking, since ancestry alone cannot prove parity.
Branch deletion never deletes release tags, source archives or recovery digests.

## Current-state evidence

Repository paths below identify configuration authority and follow-up ownership.
They are not claims that tags or API-reported versions prove running digests.

| Area and source anchor | Current finding | Consequence |
| --- | --- | --- |
| `scripts\deploy-docker.sh`, `scripts\deploy-docker.ps1` | Canonical installer entry points; registry mode overrides/pulls API, frontend, and optional discovery only. Worker/slicer-host references can remain local and builds can occur. | Redeployment is not an immutable, complete release-set operation. |
| `scripts\docker\compose-templates\`, `scripts\docker\compose-generator.sh`, `scripts\docker\compose-generator.ps1`, `scripts\docker\compose-replace-db.py` | Compose fragments and generators own topology; root Compose is generated. | Change canonical sources in later implementation, preserve approved overrides, and detect drift. |
| `scripts\docker\dockerfiles\Dockerfile.multistage`, `scripts\docker\container-versions.conf` | Canonical deployment Dockerfile and container version defaults. Release workflows also generate optimized Dockerfiles. | Both build paths need matching provenance; editing a generated root copy is insufficient. |
| `VERSION`, `.github\workflows\release.yml`, `.github\workflows\daily-development-images.yml` | `v0.2.3`; release publishes API/frontend/discovery/Orca worker/monolith, not slicer-host. Daily publishes six images including slicer-host and digest-pinned `image-set.json`. | Reuse the daily concept; it is not a signed compatibility contract or complete stable release. |
| Compose templates for base, discovery, slicer-host, Orca worker, databases, and add-ons | Split topology includes database, API, frontend, nginx, optional discovery, slicer-host, workers, and add-ons. | Inventory selected services, not just API version; absence of an optional service is not failure. |
| `scripts\deploy-docker.sh`, `scripts\backup-production.sh`, [deployment guide](DEPLOYMENT.md) | Redeploy is non-transactional; some readiness timeouts allow continuation. Backup helper is stale/incomplete; offline preparation is not a complete air-gap bundle. | Existing scripts must not be advertised as a safe one-click updater. |
| `src\api\Health\BuildVersion.cs`; main API, slicer-host, and worker `/api/system/version` | Main API/slicer-host expose build versions; worker also reports `workerVersion` and `orcaslicerVersion`. Discovery `/api/discovery/info` hardcodes `1.0.0`. | Distinguish application build, embedded engine version, and placeholder observations. |
| `src\api\Controllers\SystemInfoController.cs`, `src\infra\Services\SystemInfo\SystemInfoService.cs`, `src\infra\Dtos\SystemInfoDtos.cs` | `/api/system/info` assigns API version to monitored services. No image digest, provenance, observation timestamp, or release eligibility contract. | Current status UI does not establish actual per-service binary versions. |
| `src\modules\Farm.Modules.Administration\Services\Admin\AdminOverviewService.cs`, `Controllers\Admin\AdminOverviewController.cs` in that module | Overview uses `system_settings:admin` and graceful unknown states. No application release checker/updater exists; catalog updates are unrelated. | Extend administration read models; do not repurpose catalog update services. |
| `src\slicer\Farm.Slicer.Module.Api\Controllers\WorkersController.cs`; worker registry and pinned-job contracts | Capabilities/digests exist, but `Worker.Version` need not be the worker app build. Jobs can pin worker/version/distribution/digest. | Replacement can strand work; disable is not a drain operation. |
| `src\infra\Data\Migrations\ProviderAwareMigrationRunner.cs`, `src\migrations\`, API and slicer-host startup | Both contexts have provider-aware migrations; both hosts migrate on startup. | Prevent concurrent migration owners and mixed old/new writers. |
| `src\Web\ReactApp\src\features\system\pages\SystemStatusPage.tsx`, `components\SystemPulsePill.tsx` in that feature | Both consume `/api/system/info`; displayed service versions inherit its limitations. | Improve the existing surface instead of inventing a competing version display. |

Persistent storage includes provider databases, application data, model/G-code/
profile storage, Data Protection keyrings, slicer data/artifacts, and private
calibration blobs. Include configured external storage, certificates, and
deployment configuration in recovery scope, without exposing contents to the UI.
See [artifact ownership and routing](MICROSERVICES_DEPLOYMENT_GUIDE.md) and
[migration-safe upgrades](DEPLOYMENT.md#migration-safe-upgrades). The latter is
the controlling recovery guidance where older `DOCKER_DEPLOYMENT.md` procedures
imply that restarting old images alone is sufficient.

Main API owns most API traffic; slicer-host owns slicing, workers, models,
artifacts, and `/hubs/slicer` behind nginx. Frontend assets are their own build.
Default to a coordinated application release; only explicit, tested manifest
compatibility may permit worker or other component skew.

## Target components and authority

```text
Release pipeline -> verified OCI images -> signed release manifest (published last)
                              |
                              v
Trusted metadata reader -> API read models -> React inventory / attention / policy
                              ^
                              | redacted journal snapshots, reconciled over REST
                              |
Operator CLI OR enrolled pull reconciler OR optional fixed-operation host helper
                              |
                    host policy + installation lock
                              |
                 durable host journal -> Docker / storage / database tools
```

The application owns inventory presentation, release-check policy, and approved
intent records. Infrastructure adapters observe services and fetch metadata.
The host executor alone owns deployment side effects, credentials, and durable
recovery state **outside the application database and replaced containers**.
Shared contracts remain additive; do not introduce deployment behavior into
printer/job domain aggregates.

No API, frontend-facing service, or checker gets a Docker socket. Explicitly
reject unrestricted socket access, **including read-only socket mounts and
generic socket proxies**: filesystem read-only does not make Docker API methods
read-only. Discovery's existing read-only socket mount is a hardening prerequisite
for automation, not an approved pattern or something changed by this proposal.

## Installed inventory and UI contract

Propose an authenticated inventory read model, separate from execution rights.
Use `system_settings:admin` initially for detailed deployment observations.
Ordinary authenticated users retain only existing safe application/status
information; never expose host paths, credentials, detailed topology, or backup
references through broad settings reads.

| Proposed field group | Meaning and source |
| --- | --- |
| `installationId`, `deploymentMode`, `observedAt`, `services[]` | Stable opaque local installation identity; mode and timestamp from enrolled inventory. Never derive identity from a hostname or send it during public checks. |
| `serviceId`, `instanceId`, `component`, `required` | Stable service/replica identity and expected topology, including remote workers and disabled optional components. |
| `applicationVersion`, `sourceCommit`, `engineVersion` | Actual service build response; engine version separate from worker app build. Unknown discovery builds stay unknown until reporting is corrected. |
| `configuredImage`, `runningDigest`, `platform`, `provenance` | Desired reference versus observed platform digest. Host observation or verified imported snapshot; service self-report alone cannot attest a container. |
| `observationState`, `observedAt`, `lastSuccessAt`, `source`, `reasonCode` | String states `Observed`, `Stale`, `Unavailable`, `Unknown`, `NotInstalled`; include source/trust and freshness. Missing build data is not version zero. |
| `releaseId`, `channel`, `targetReleaseId`, `eligibility`, `reasons[]` | Separately evaluated release association and `Eligible`, `Blocked`, `Unknown`, or `NotManaged` status. Missing schema/platform/digest evidence prevents eligibility. |
| `selectedChannel`, `observedChannel`, `targetChannel`, `channelState` | Persisted policy versus verified running-set and candidate identity. Observed identity is nullable with source/freshness; never infer it from the selected default or tags. Report pending switch, mismatch and mixed-channel states explicitly. |
| `canonicalVersion`, `sourceTag`, `sourceBranch`, `authorizedBranchHead`, `manifestDigest`, `buildId`, `promotionOrigin` | Verified installed identity and branch-at-authorization evidence, with optional promotion lineage; record running digests and source commit, not a mutable tag as the version. |
| `compatibilityState`, `compatibilityReasons[]` | Proposed `Compatible`, `Incompatible`, `Unknown`, `MixedRelease`, `MixedChannel`; separate protocol/schema compatibility from observation freshness and update eligibility. |

A host observation/import is optional for the first increment: native/custom
installs can report truthful build versions with null digests and unknown
release association. Imported snapshots retain their original timestamp and
verification status; they never masquerade as live observations. Database
engine version and context migration heads are separate inventory concepts.
Report each replica and identify a mixed release set instead of collapsing it.
Legacy/custom installations default selection to stable without relabeling
unverified installed builds as stable. A mixed-channel observation blocks normal
update eligibility and requires a recovery plan, not per-service reconciliation.

UI placement:

- Extend `/admin/status` with a proposed `ServiceVersionsTable`. Keep
  `SystemPulsePill` a status summary, not an intrusive upgrade advertisement.
- Put the first compatible-release notice in Admin Control Center **Needs
  attention**, including checked time, reason, release notes, and dismissal.
  “Unable to check” is not “Up to date.”
- Put release channel/check schedule/notifications in a per-key System settings
  section under `/admin/settings`, following the
  [current settings architecture](SETTINGS_ARCHITECTURE.md). Reuse canonical
  URL parameters and destination registry; no Save All or route aliases.
- Show selected versus observed channel in status and settings, with persistent
  textual insider reduced-stability warning. Default stable on first setup and
  legacy initialization; never preselect insider from the running build.
  Explain each channel's publication frequency, effective check/reminder cadence
  and next check. Editing channel is a privileged policy workflow, not an
  ordinary unrestricted settings save.
- Preview source/target channel, release/version, compatibility, downgrade
  assessment, backup/recovery class and host-policy outcome before confirmation.
  Before execution support exists, permit only confirmed discovery-policy
  changes after read-only compatibility/preflight; selection alone never
  authorizes image replacement. A blocked transition remains blocked visibly.
- Add `/admin/updates` only with operational execution/history support.
  Candidate components: `UpdatePreflightPanel`, `UpdateConfirmationDialog`,
  `UpdateJobStatus`, `UpdateHistoryList`.
- Reuse `src\Web\ReactApp\src\services\api.ts`, permission-scoped query clearing,
  existing Notification facilities, and slicer REST-plus-SignalR reconciliation.
  Toasts are not durable progress. Lost connectivity means observation unknown,
  not operation failed. Re-fetch after reconnect or identity/permission changes.
- Preserve camelCase properties and string enums in REST and SignalR; use
  lowercase event names. Update shared TypeScript contracts and test existing
  iOS consumers without requiring a mobile-only DTO or forced app update.
  Use accessible status text, keyboard-operable confirmation, focus return,
  and non-color-only stale/blocked indicators.

## Signed release metadata and bounded checks

The proposed schema uses camelCase fields and a versioned closed contract;
the table defines required semantics, not an already implemented API.

| Schema group | Required identity and validation |
| --- | --- |
| `schemaVersion`, `releaseId`, `channel`, `canonicalVersion`, `baseVersion` | Match the single derived identity record; stable has no prerelease, insider has the approved numeric prerelease form. Reject conflicting per-service values. |
| `sourceBranch`, `sourceTag`, `authorizedBranchHead`, `sourceCommit`, `buildId`, `buildAttempt`, `workflowIdentity` | Verified branch-at-authorization, canonical tag and full SHA, plus CI allocation/build evidence; self-asserted branch text is insufficient. Preserve offline-verifiable authorization evidence after branches move or are deleted. |
| `images[]` | Component, required/topology scope, repository, index digest, platform digests, identical release/channel/version/source/build labels and digest-bound provenance/SBOM references; include every declared component, including slicer-host. |
| `promotionOrigin` | Required for promoted stable releases: immutable insider release/version/commit/manifest digest and qualification evidence; new stable identity and digests remain separate. Direct stable hotfixes require qualification and an explicit non-promotion reason. |
| `compatibility`, `migrations`, `rollback` | Supported canonical source/target release IDs, protocol ranges, both provider/context schema read/write ranges and migration heads, storage formats and recovery class; channel alone is not a migration path. |
| `sequence`, `publishedAt`, `expiresAt`, `trust`, `releaseNotes` | Channel-scoped anti-replay and freshness, enrolled signer identity and hashed notes; signatures/provenance supplied in a verifiable envelope. |

Compute `manifestDigest` over the exact published manifest bytes and retain it
in the signature envelope, channel pointer, installed evidence and journal.
It is not a self-referential field inside its own hashed content. The channel
pointer binds release ID/version and manifest digest, never just an image tag.
Index digests and platform-manifest digests are distinct; resolve and verify
their relationship, rather than comparing unrelated Docker local image IDs.

A versioned signed compatibility manifest must bind:

- Release ID, semantic version, channel, publication/expiry time, monotonic
  metadata sequence, source commit, release notes hash/link, signing identity.
- Closed channel identity (`stable` or `insider`), channel-scoped sequence and
  pointer, publication cadence metadata, recommended discovery/reminder cadence
  and freshness bounds. Sign these with the complete image-set identity.
  Local policy bounds recommendations; metadata cannot enable checking,
  shorten an approved maintenance window or authorize a switch.
- Complete required/optional service set, OCI repository and immutable digests,
  index plus supported platform digests, build provenance and attestations.
  Include slicer-host; also declare external nginx/database/add-on constraints
  without implying authority to upgrade unrelated images.
- Supported source releases and upgrade hops; API/frontend/mobile protocol
  ranges; worker app, engine/distribution, capability, and artifact compatibility.
- Both contexts' provider-specific migration heads and readable/writable schema
  ranges, storage/artifact format compatibility, and engine prerequisites.
- Configuration/template hashes and schema, minimum updater version, ordered
  migration/service steps, expected downtime, backup requirements, and rollback
  class (`ImagesOnly`, `RestoreRequired`, or `ForwardOnly`).
- Explicit supported cross-channel source/target paths and recovery constraints.
  Channel-local sequence numbers are not comparable across channels and cannot
  alone prove that switching is an upgrade. Bind every managed application
  service/platform digest to one channel's set; no fallback to another channel
  for a missing service. Promotion requires a separately verified complete
  target-channel manifest, never partial tag reassignment.

Publish images and attestations first, verify availability for every promised
platform, then publish the signed manifest and channel pointer last. Reuse
`image-set.json` as an input, not as sufficient evidence. A signed incomplete set
still fails eligibility. Never execute instructions or arbitrary Compose from
metadata: validate a closed schema and fixed operation vocabulary.
Stable and insider publication/pointers are isolated. Insider publication never
advances stable pointers; daily/development artifacts are insider candidates
only after meeting the same completeness/trust contract. Branch/version
publication policy is tracked by #2668, prerequisite to signed publication #2660.

Verifier trust is host/operator-enrolled: pinned issuer/identity or public keys,
rotation/revocation rules, expiry and anti-replay sequence checks. Reject unknown
schema versions, invalid signatures, revoked releases, substitution, and
unsupported updater versions. Require local approval to enroll/change trust.
Do not keep signing keys, enrollment credentials, or proxy secrets in generic
settings. Compromise of an authorized signer remains a residual risk; signature
validation proves authorized origin, not safety of its software.

Replay protection is durable **per enrolled trust root and channel**, not per
mutable policy revision or installed version. Persist monotonic sequence
high-water marks and rejected/superseded metadata identities outside restored
application databases and replaced containers. Retain both channels' state
across policy edits, channel switches and round trips, process restarts and
database restoration. Policy revision changes invalidate stale responses and
approvals but never reset replay protection. Trust rotation must preserve root
continuity; changing trust requires an explicit continuity-preserving procedure,
not a fresh counter that permits old metadata.

Authenticate the signed sequence/channel/identity before advancing state;
untrusted input must not poison the high-water mark. Atomically persist a
new authenticated high-water mark and decision before offering or acting on
metadata, including when later compatibility or policy checks reject it.
Reject lower sequences and equal-sequence identity conflicts. Rejected or
superseded metadata stays rejected even if its version exceeds the installed
version; equal-sequence reuse is permitted only for the identical,
non-rejected/non-superseded verified identity under all current gates.
If replay state is missing, rolled back or cannot be proven current after
restore, block eligibility and execution until a trusted recovery procedure
re-establishes continuity. A restored database, bundle, cache or policy revision
cannot replace or lower the durable record; do not bootstrap it from the
installed version.

Proposed check policy, subject to approval:

- Explicit opt-in to outbound checking; once enabled, channel-specific checks
  with jitter (proposed stable every 24 hours, insider every 6 hours),
  10-second total deadline, conditional HTTP requests, bounded response size,
  exponential backoff, and a five-minute manual-refresh throttle. Validate
  redirects against the same trusted endpoint allowlist and bound concurrency.
- Server-side cache per channel/trust policy; persist last successful verified
  result and `lastAttemptAt`, `lastSuccessAt`, `nextCheckAt`, and error code.
  Proposed stale limits are 48 hours for stable and 12 hours for insider;
  effective intervals/limits require maintainer approval. Display expired
  metadata as historical only; it cannot authorize new execution.
- `stable` is the default for existing/new installations and sees stable only;
  `insider` sees insider only after explicit admin warning/confirmation.
  Use SemVer precedence for canonical versions in the selected channel and
  signed channel-local sequence for anti-replay, not a replacement comparator.
  Tags are discovery hints, never equality/provenance proof.
  Pins block execution and label a newer candidate as policy-held. Channel
  changes never silently downgrade; custom/unrecognized builds are `NotManaged`.
  Cache, dismissal and in-flight results are keyed by installation policy
  revision and channel; durable replay state is independently keyed by enrolled
  trust root and channel as above. A late response from the previous selection
  cannot populate the new channel or make a target eligible. Policy edits
  invalidate stale responses, never the replay high-water marks.
  Persist selection across restart/upgrade; lost or invalid policy fails closed
  instead of inferring insider enrollment from metadata.
- Available means a verified newer candidate; compatible requires the full
  inventory/schema/platform/path evaluation. Display blocked reasons and
  supported intermediate hops, not an enabled update button.
- Dedupe notices by release/channel; allow per-release dismissal and separately
  controlled reminders. Security urgency does not bypass host approval,
  compatibility, pins, or downtime constraints.
  Proposed reminder defaults are weekly for stable and daily for insider,
  independently configurable/disableable within approved policy. Include channel
  and reduced-stability text in insider notices. A dismissal in one channel
  cannot hide another channel's release. Switching changes discovery/reminder
  cadence, not permission for unattended execution or maintenance-window timing.
- Checks send only the fixed channel/metadata request and conditional cache
  headers. No hostname, installation ID, printer/job inventory, credentials,
  or installed topology. Endpoint operators still see source IP, request time,
  and ordinary client headers. Image pulls separately reveal requested digests.
- Disabling checks cancels scheduled/in-flight activity where possible and makes
  zero subsequent check requests; UI renders local inventory/cache only.
  Honor an approved proxy/mirror, not arbitrary user-entered fetch URLs.
  Disabling checks does not terminate an already executing host operation.

### Comparison and offer decision rules

First verify trust, identity consistency, selected channel and policy revision,
and enforce durable trust-root/channel replay state; then compare canonical
versions, then evaluate the explicit compatibility path.
Never compare tag strings, commit hashes or wall-clock dates for precedence.
Stable `1.10.0` is newer than `1.9.0`; insider `1.3.0-insider.10` is newer
than `1.3.0-insider.9`. A new attempt reserves a new N, not another suffix.
Base-version precedence comes first. SemVer `+build` metadata does not affect
ordering, so CI ordering must remain in the prerelease identifiers.

Equal release/version plus differing manifest or image digests is an identity
conflict, not an update; quarantine it. Equal verified identity is current only
with fresh complete evidence. Lower versions are never routine update offers.
Pins hold newer versions; unknown/custom identity cannot be ordered safely.
An increased signed sequence cannot turn a lower version into an upgrade.
Reject replayed metadata even if its version sorts higher.

Do not offer insider on stable, including a higher insider base, without an
explicit confirmed enrollment switch. Compare across channels only for an
explicit transition/recovery preview, never by channel sequence. For example,
`1.3.0-insider.10` to stable `1.2.9` is a downgrade, while stable `1.3.0`
has higher SemVer precedence but still needs a signed supported path and
schema/storage checks. No numeric ordering alone proves migration safety.
Stable `1.3.0` to `1.3.0-insider.11` is also a downgrade; insider
`1.4.0-insider.12` is newer but still requires explicit enrollment and a signed
supported path. Promotion is a distinct preview/offer, not a changed digest
under the insider identity. Use arbitrary-precision numeric comparison for N
or reject values outside a documented supported bound without rounding.
Offline metadata uses exactly the same comparator, freshness and identity
conflict rules. Expired metadata is historical, never “up to date” evidence.

Offline users import a complete bundle: manifest/signatures, offline-verifiable
provenance/trust material, every selected platform image including infrastructure
dependencies, templates/config schema, updater, and recovery documentation.
Verify the same digests and compatibility without network fallback or builds.
Reject unsafe archive paths and oversized imports. Expired/revoked trust needs
an explicit host trust-maintenance procedure, not a skip-verification switch.
After reconnect, refresh trust/metadata and reconcile the actual installed set
before showing eligibility. Failed checks remain unknown, never current.
Bundle metadata, exported host evidence and generated commands bind the selected
channel, complete target set and policy revision. Import never changes enrollment
or silently falls back to stable/insider; wrong/unknown-channel metadata is
rejected for execution and explained. An intentional offline switch uses the
same host preflight, warning, confirmation and audit path, with no network
fallback. Reconnection preserves selection rather than following a default
channel pointer.
Preserve canonical version, release ID, source/main-or-development provenance,
build identity, promotion origin, manifest digest and every index/platform
digest without rewriting signed bytes. Bundle import rejects mutable-only
references and mismatched backend/frontend/OCI identities even if signatures
are valid; supplied trust material cannot enroll its own signer. Test full
network-denied round trips and original-versus-imported identity equality.
Manual/offline import uses the same durable per-trust-root/per-channel
high-water marks and rejected/superseded identity records as online checks.
Neither importing historical recovery artifacts nor restoring application
databases may lower that state. Historical artifacts remain recovery-only
under explicit verified recovery authorization, never fresh update offers.
If continuity cannot be established offline, fail closed without network
fallback or a replay-reset override.

Inventory, plans, apply checks, rollback records, bundles, authorization/audit,
desired state and UI all retain the canonical source tag and signed
branch-at-authorization evidence. Validate tag/channel grammar and exact SHA
agreement independently of a now-moved main/development ref or deleted
stabilization branch. Test forged branch labels, wrong tag suffix, mismatched
peeled SHA and missing authorization evidence in #2659 and #2662–#2667.
Unknown legacy provenance remains visible but cannot authorize managed apply.
Offline verification must not require a live GitHub ancestry query; its trust
envelope carries the originally verified branch/tag authorization.

## Execution choices and security boundary

| Model | Recommended use and limits |
| --- | --- |
| Existing installer run by an operator | Remains the current manual deployment entry point, subject to migration-safe guidance. Its partial pulls/backups/readiness gaps prevent updater guarantees today. |
| Validated host-run updater with UI-generated instructions | First execution delivery. UI displays a plan and, later, a fixed command containing only a validated operation/plan identifier. Operator independently reviews and approves on the host. No arbitrary shell text or secrets from UI input. |
| Enrolled external pull reconciler | Preferred later unattended model. Host polls authenticated desired state, verifies manifest and local maintenance/channel/downtime/backup policy, then uses the same engine. No inbound privileged endpoint; web compromise cannot override host policy. |
| Optional UI-triggered host helper | Last delivery, separate opt-in. Authenticated bounded handoff accepts only fixed operations for its enrolled installation. Still privileged/root-equivalent where it controls Docker, even if its public interface is narrow. |

Assets include host authority, printers, queue fences, database/blob consistency,
keyrings, registry trust, and audit evidence. Actors are farm users,
permissioned operators, host administrators, release signers, and attackers
controlling any of those accounts or network inputs. UI authentication is not
host enrollment, and neither authorizes arbitrary Docker control.

Any execution intent requires a dedicated permission (proposed `updates:execute`),
interactive reauthentication, request-origin/CSRF protection as appropriate to
the existing auth transport, and an expiring approval bound to installation,
manifest hash, topology/config/schema fingerprints, plan, and downtime.
The executor checks its own enrolled identity, nonce/idempotency key, expiry,
and host policy; an API approval is necessary where used but never sufficient.
Reject replay, concurrent plans, arbitrary URLs/paths/commands, registry
substitution, and stale approvals after drift.
Channel changes are privileged policy/enrollment actions even when no update
runs. Require administrator authorization, reauthentication/origin protection,
host-policy agreement where enrolled, and warning acknowledgment for insider.
Bind approval and idempotency to source/target channel, selected-policy revision,
manifest/set hash and downgrade/recovery assessment as well as existing plan
fields. A settings write, imported bundle or compromised desired state cannot
enroll insider or change the host's allowed channel. Persist actor, reason,
warning/confirmation time, prior/new policy, attempted target and accepted/
rejected outcome in audit history; no credentials in these records.

Keep host credentials in OS-protected storage, redact logs, restrict filesystem
access, authenticate handoff (for example mutually authenticated transport),
and avoid broadly reachable host listeners. Rate-limit planning and staging
to protect disk and host availability. Compromised host execution remains a
host-compromise risk, not something a generic socket proxy can contain.
Provide a host kill switch that rejects new work but never blindly kills an
in-progress migration; only stop at safe journal checkpoints.

## Compatibility, lifecycle, and recovery

### Compatibility decision matrix

| Boundary | Required gate |
| --- | --- |
| API / frontend / slicer-host / discovery | Same verified release by default; only explicit tested protocol ranges allow skew. Validate proxy routes and cached frontend assets. |
| Local and remote workers | Worker app build, engine/distribution/digest and capabilities satisfy queued job pins. Drain, retain the compatible worker, or explicitly reconcile jobs; never silently repin. |
| AppDbContext / SlicerDbContext | Provider and migration history match a supported source path; serialize both context migrations in manifest order. Unknown schema blocks execution. |
| Databases / nginx / add-ons | Meet declared engine/config constraints; externally managed components remain operator-owned and can block preflight. No implicit major engine upgrade. |
| React / iOS shared API | Additive camelCase/string-enum contract tests and supported protocol ranges; unsupported clients receive an explicit compatibility outcome. |
| Model, G-code, profile, calibration and slicer storage | Manifest format compatibility plus consistent DB/blob snapshot. Restoring only databases is insufficient. |
| Stable / insider channel transition | One complete target-channel application set; signed supported source/target path, schema/storage/worker checks, explicit confirmation and audit. Version-skew exceptions never permit cross-channel image mixing. An unsupported downgrade remains blocked. |

Coordinate new frontend and API metadata, but also test already-cached frontend
assets against the declared API range during replacement. A cached older UI
may receive an explicit refresh-required compatibility state; it must not
display the backend's version as its own build or send unsupported operations.
Any permitted worker skew is an explicitly listed compatible component release,
not an unlabeled exception inside a supposedly identical coordinated image set.
It remains same-channel; otherwise fence the worker or block execution.

### Durable operation and ordered side effects

Host journal states:
`Planned -> Approved -> Staged -> Drained -> BackedUp -> Migrating -> Applying
-> Verifying -> Completed`; exceptional states are `Failed`, `RolledBack`,
and `NeedsOperator`. Checking metadata is not an update operation.
Record intended checkpoint **before** each side effect, then its observed
outcome. One installation lock, operation ID, idempotency key, step attempt,
and monotonically increasing revision prevent competing or duplicate execution.

REST exposes redacted snapshots/history and a revision/cursor for reconciliation.
SignalR only accelerates refresh. Host-local status remains available while API
containers are stopped; a disconnected browser shows last-known state/time.
After power loss, inspect images, schemas, locks, backups, and journal before
resuming. Never infer success solely from process exit or replay uncertain work.

1. **Plan/preflight:** Verify enrollment, trusted complete release, supported
   source/target path, topology/platform, all replicas/remote workers, configuration
   drift, provider/schema heads, minimum updater, registry access, CPU/memory,
   free storage for images/backups/journal, backup destination, maintenance
   window, and recovery capacity. Validate canonical generated configuration
   without logging secrets. Hash the plan; drift requires reapproval.
   Compare selected, observed and target channel, semantic version and supported
   migration path; do not treat an insider sequence as greater than a stable
   sequence. Both switch directions need preflight and confirmation. A channel
   or policy revision change invalidates staged/approved plans.
2. **Stage before downtime:** Fetch/import and verify the whole selected release
   and recovery artifacts, retain prior digests/configuration, and prohibit local
   builds or mutable tag resolution during application. Failed staging leaves
   the running release untouched.
   Before accepting the stage and again before apply, verify each image's
   actual digest, provenance and release/channel/canonical-version/source/build
   labels against the immutable manifest. A valid signature does not excuse
   mismatched metadata. Persist canonical prior/target identities and manifest
   digests; alias movement cannot change an approved plan.
   Stage a single target-channel set, including required workers; no per-image
   channel fallback. Switching application channels occurs under the same drain/
   maintenance fence so old-channel and new-channel services never operate as
   a mixed set. Remote workers that cannot join the compatible target channel
   must be safely fenced or block the switch, not silently kept active.
3. **Drain:** Fence new print/slice submissions and scheduling, resolve queued
   pinned jobs, wait for active safe completion, and inspect pending/processing
   start/control outbox commands. Disabling a worker does not drain its jobs.
   Stop every API replica, scheduler, outbox publisher, bridge and background
   writer before migrations. Do not cancel active physical printing or replay
   uncertain printer commands automatically. Unresolved work blocks the window
   or requires an explicitly approved operator reconciliation plan.
4. **Back up:** With writers stopped, create provider-native backups of both
   contexts and coordinated snapshots of all associated blobs/artifacts/config/
   keyrings. Record encrypted backup references, checksums, consistency point,
   retention, and restore evidence in the protected journal. External database
   owners must provide equivalent evidence. Fail closed on missing coverage.
   Follow [migration-safe procedures](DEPLOYMENT.md#migration-safe-upgrades);
   do not rely on the current backup helper as proof of completeness.
5. **Migrate:** A single selected owner applies and validates each context in
   manifest order. Existing API/slicer startup migration behavior must be
   explicitly coordinated before automation ships; do not start competing hosts
   and hope migration locks suffice. Use bounded, observable execution; on a
   timeout inspect provider state instead of assuming termination or retry safety.
6. **Apply/verify:** Start only the manifest-compatible set, keep writes fenced,
   and verify observed digests/builds, both migration heads, readiness, nginx
   routes/TLS, frontend assets, auth/key continuity, worker compatibility,
   artifact read/write probes, and queue consumers/reconciler/publisher health.
   Do not send real printer start commands as smoke tests. Readiness timeout
   fails the operation. Reopen writes only after the whole set passes.
7. **Complete:** Reconcile inventory, record approvals/actor, manifest/signature
   identity, prior/target/observed digests, schema transitions, backup references,
   timestamps and outcome. Retain host audit history independently of restored
   application databases. Completion means verified running state, not “pull
   succeeded.” Prune old images/backups only under retention policy.
   Record selected/source/target/observed channel and policy revision. Update
   observed channel only after whole-set verification; preserve truthful
   selected-versus-observed differences on failure or deferred transition.

### Failure and rollback semantics

Before migrations or persistent-format changes, abort or restore prior images/
configuration after proving the previous release still matches state. After
migration, image-only rollback is allowed **only** when the prior release
explicitly supports the resulting schema/storage and the manifest authorizes it.
For the current forward-only migration guidance, default to fix-forward or
coordinated backup restoration; no EF down-migrations.

Key migration and rollback decisions to canonical source/target release IDs
and manifest digests, with the actual AppDbContext/SlicerDbContext provider
heads, read/write ranges and storage formats. Retained tags are not recovery
coordinates. Record the exact prior platform images, schema/backup consistency
point and supported transition; verify the resulting canonical running identity
before declaring `RolledBack`. Same-version/different-digest records are
conflicts requiring investigation, not interchangeable recovery choices.

Retain previous complete manifests/digests, channel selection/policy revision,
configuration and recovery classification for each channel. Same-channel image
rollback still needs schema/storage proof and recorded approval. Cross-channel
recovery (including insider to stable) is never an implicit fallback: require a
supported path, fresh preflight, explicit downgrade/recovery confirmation and
audit; an unsupported downgrade stays blocked regardless of administrator intent.
Do not automatically enroll insider when recovering a historical insider set.
If no safe stable target exists, report `Blocked`/`NeedsOperator`, with supported
wait/fix-forward/restore choices rather than claiming that selecting stable has
restored stability. Preserve channel audit outside restored databases; reconciler
holds after recovery until policy and the observed set are deliberately reconciled.
Preserve trust-root/channel replay high-water marks and rejection/supersession
records independently of the restored databases. Reapproval cannot revive
rejected/superseded release metadata. Missing or rolled-back replay state
requires trusted continuity recovery before eligibility or reconciliation.

| Failure | Required response |
| --- | --- |
| Interrupted pull, invalid signature, absent platform image | No downtime; retain old set; retry verified staging under policy. |
| Drain timeout, incomplete backup | Do not migrate. Resume old release only after proving safe state and releasing fences deliberately. |
| Migration failure or uncertain timeout | Keep writers stopped, preserve diagnostic data, inspect actual schema; mark `NeedsOperator` unless recovery is proven safe. |
| Partial apply or readiness failure | Keep maintenance fence; reconcile observed set and choose permitted rollback or forward recovery. Never continue with mixed writers. |
| Lost API/UI connection | Mark observation unknown; executor continues only under approved host policy. REST/local journal determines real outcome. |
| Host power loss | Reacquire/reconcile the installation lock and durable checkpoint, inspect actual effects, resume idempotently or require operator intervention. |
| Restore required | Stop all writers, restore both DBs and matching blobs/config/keyrings, deploy prior pinned set, verify, then reconcile printers before resuming. |
| Channel mismatch, failed switch or incompatible return to stable | Keep the failed target and selected/observed channel visible; never repair one service from another channel. Reconcile the journal and choose a verified whole-set recovery with fresh authorization; do not replay stale approvals. |

`Failed` does not mean no side effects; retain the failed checkpoint and recovery
classification. `RolledBack` requires a verified prior compatible set, not merely
a container restart. Restoration cannot undo prints or external commands issued
after the snapshot: inspect physical printer state and queue fences manually,
and never clear leases or replay uncertain start/control commands blindly.

## Delivery increments, validation, and gates

Each increment can land independently without enabling its successor. No
production validation runs are implied by this design document.

### I1 — Truthful read-only inventory

- **Value/acceptance:** Actual service builds, engine/build separation, replica
  mismatch and stale/unavailable/unknown states; null rather than fabricated
  digests; permissions respected. No outbound release requests or executor.
  Show selected/observed channel with provenance and pending/mixed states;
  unknown observed identity is not stable. Clearly label insider reduced
  stability. Test legacy/new default selection and insider/mismatch UI fixtures.
- **Likely files:** `BuildVersion.cs`, `SystemInfoService.cs`,
  `SystemInfoDtos.cs`, `SystemInfoController.cs`, service version endpoints;
  `SystemStatusPage.tsx`, `SystemPulsePill.tsx`, shared `types\api.ts`.
- **Validation/gate:** Focused build/version, inventory aggregation,
  serialization/mobile compatibility, denied-access and stale UI tests.
  Include existing `SystemInfoIntegrationTests.cs`, `BuildVersionTests.cs`
  and `SystemPulsePill.test.tsx`. Verify mixed service versions in a fixture.
  Risk: self-report confused with attestation; block that claim in UI copy.
  #2659 also shows canonical version/release/channel/commit/digest, explicit
  compatibility states and promotion lineage when verified; aliases are
  secondary configured-reference information only.

### I2 prerequisite — Branch and version policy (#2668)

- **Implemented locally:** Main/stable and development/insider dispatch guards,
  supported insider tags, disjoint TestFlight tag namespaces, isolated final Docker channel promotion,
  OCI channel labels, optional release-candidate CI, release docs and tests.
  Exact evidence and residual paths are listed above; no claim of deployment.
- **Remaining scope, not baseline capability:** Protected publication;
  one VERSION-derived record, exact stable and insider derivation, immutable
  identity, approved counter ownership and main rebuild promotion as above.
- **Likely files:** `VERSION`, `scripts\bump-version.sh`,
  `.github\workflows\consolidated-release.yml`, `release.yml`,
  `daily-development-images.yml`, `docker-publish.yml`, shared version/build
  metadata inputs, `BuildVersion.cs`, frontend build metadata configuration,
  canonical Dockerfiles and `container-versions.conf`; repository/environment
  rules require separately authorized administrative configuration.
- **Validation/gate:** Branch/event/tag negative tests, counter/rerun/collision
  fixtures, metadata equality across all components, source-first ordering,
  stale pointer race and promotion qualification tests. Record live ruleset/
  environment evidence and maintainer decisions before closure. No privileged
  tests publish from unauthorized refs; test their rejection without writes.
  Also update `scripts\sync-monorepo-version.sh`, `.github\workflows\ci.yml`,
  `scripts\ci\select-dotnet-tests.sh` and its tests, and
  `scripts\ci\tests\test-release-tag-triggers.mjs` with canonical grammar,
  non-publishing stabilization lifecycle and executable event-validation tests.
  Preserve `docs\RELEASE_GUIDE.md`'s disjoint container and TestFlight tag guidance.
  Gate cutover on historical tag inventory, allocator continuity, live
  protections and a credential-free negative-event rehearsal; legacy artifacts
  remain immutable and cannot become managed candidates through relabeling.

### I2 — Complete release contract and publication

- **Dependency:** #2668 blocks #2660 in the native graph. Build on the local
  publication baseline while completing remaining prerequisite implementation;
  inventory vocabulary can be agreed in parallel with I1.
- **Acceptance:** Both stable and insider channels publish verifiable complete
  image sets including slicer-host; every claimed platform exists; publish last;
  documented source/target/provider/worker compatibility and trust rotation.
  Require slicer-host build, signing/SBOM, validation, and promotion coverage
  while preserving existing source-first publication gates.
  Sign channel/cadence metadata and isolate pointers/sequences. Reject mixed
  channel/service/platform sets and cross-channel fallback; test promotion and
  failed publication leave the other channel untouched. Publication frequency
  may differ, but completeness, signing and compatibility gates do not.
- **Likely files:** `release.yml`, `daily-development-images.yml`,
  `.github\workflows\docker-publish.yml`,
  canonical Dockerfile/templates/version defaults; new manifest schema and
  verifier fixtures under `scripts`; deployment/release documentation.
- **Validation/gate:** Schema/signature/provenance, missing-service/platform,
  revoked/expired/replayed metadata and interrupted-publication fixtures.
  Compare local and optimized release build provenance. No eligibility until
  a real complete signed release can be verified. Risk: authorized bad release.

### I3 — Compatible release checks and alerts

- **Dependencies:** I1 + I2. **Acceptance:** Opt-in bounded checks, policy pins/
  channels, last-success and unknown states, deduped attention notices, zero
  network when disabled, verified offline metadata import.
  Stable defaults for existing/new installations; insider requires privileged
  warning/confirmation. Persist selected channel and apply channel-specific
  cadence, freshness and notifications; test late old-channel responses,
  cancellation, offline selection preservation and both switch directions with
  incompatible/lower targets blocked. Selection never grants execution rights.
  I3 owns a read-only host inventory exporter/importer that gathers topology,
  running digests/platforms, configuration fingerprints, database providers and
  both contexts' migration heads for eligibility, with original observation
  timestamps and verification status. Export redacted evidence from the host;
  import verified snapshots without exposing Docker to the API through a socket
  or generic proxy. Reject stale or incomplete evidence for eligibility.
- **Likely files:** New administration checker/read DTOs, `src\infra\Settings\`
  section, `AdminOverviewService.cs`, `AdminControlCenterPage.tsx`,
  `src\Web\ReactApp\src\features\admin\registry\adminDestinations.ts`,
  settings essential manifest, shared API client/query contracts, new read-only
  host inventory exporter/importer and evidence fixtures under `scripts`.
- **Validation/gate:** Focused server/client tests for caching, redirects/SSRF,
  permissions, malformed metadata, timeouts, channel changes, downgrade
  prevention, custom builds and offline behavior. No execution endpoint.
  A supported Compose fixture must reach `Eligible` using verified release
  metadata and fresh, complete host evidence before I4 can begin; fixtures with
  stale or incomplete evidence must not reach `Eligible`.
  Risk: false “current” or “compatible”; unknown evidence must block eligibility.
  Verify durable trust-root/channel high-water marks through policy edits,
  both channel round trips, process restarts and older database restoration.
  Previously rejected/superseded metadata remains ineligible even when its
  canonical version is higher than installed; missing replay state fails closed.

### I4 — Recoverable operator-assisted updates

- **Dependencies:** I1 + I2 + I3's proven host-evidence eligibility gate;
  CLI must still work without the UI. **Acceptance:** Durable journal/lock,
  whole-set staging, drain,
  coordinated backup, serialized migrations, strict verification, safe recovery,
  complete offline bundle, and fixed-command plan export.
  Bind channel/policy revision in evidence, plans, commands and journals;
  preflight/confirm/audit switches and execute only whole single-channel sets.
  Channel-aware rollback must not silently downgrade or reset enrollment.
- **Likely files:** Canonical deploy/generator scripts and Compose templates,
  `backup-production.sh`, migration startup integration, worker/queue admission
  boundaries, new host executor and CLI, `DEPLOYMENT.md`, offline/recovery docs.
- **Validation/gate:** [Deployment tests](DEPLOYMENT_TESTING_CHECKLIST.md),
  Bash/PowerShell parity, monolith/split with PostgreSQL and SQL Server,
  external DB and remote pinned-worker cases. Rehearse backup/restore and power
  loss at every side-effect checkpoint, failed readiness, concurrent requests,
  full disk and incomplete bundles in isolated fixtures. Include native SQLite
  migration tests but do not imply SQLite Compose support.
  Rehearse both channel switches and interrupted transitions, compatible
  same-channel rollback, blocked incompatible insider-to-stable rollback, and
  offline wrong-channel imports without fallback. Verify actor/confirmation/
  policy audit survives DB restore and idempotent retries.
  Manual/offline bundle tests preserve the same replay high-water marks through
  both channel round trips, policy edits, restart and database restoration.
  Reimporting rejected/superseded higher-than-installed metadata stays blocked;
  recovery bundles cannot reset sequence state or authorize fresh offers.
- **Rollout:** Disposable rehearsal, maintainer-operated staging, opt-in pilot,
  then documented manual release. No automation until restore evidence passes.
  Risk: database/blob inconsistency and uncertain physical actions.

### I5 — Opt-in external pull reconciliation

- **Dependency:** I4 recovery proven and host-policy/threat-model approval.
  **Acceptance:** Separate enrollment/trust, bounded polling, approved windows,
  hash-bound desired state, no inbound privileged listener, host kill switch,
  reconciliation after restart, externally readable status.
  Use selected-channel polling cadence, jitter/backoff and approved windows;
  insider enrollment is separate from unattended-update opt-in. Never reconcile
  per-service channel heads into a mixed set. Channel/policy drift holds work
  for reapproval; recovery cannot trigger automatic oscillation between channels.
  Enforce durable per-trust-root/per-channel replay high-water marks independent
  of desired-state policy revisions; retain rejected/superseded identity records
  across policy edits, channel round trips, restarts and database restoration.
  Missing or rolled-back state holds reconciliation for trusted recovery,
  never a reset to the installed version.
- **Likely files:** New host service/package and enrollment tooling, protected
  administration intent/status contracts, deployment documentation.
- **Validation/gate:** Compromised API intent cannot bypass host allowlist;
  replay/drift/expired approval rejected; restart/partition and kill-switch
  tests pass. Remove/replace discovery socket exposure before pilot automation.
  Test separate channel clocks, disabled polling, old-channel desired state,
  mixed-set rejection and restart after a channel switch or recovery.
  Replay previously rejected/superseded metadata after both channel round trips
  and older database restore, including targets newer than installed: hold,
  never apply. Test stale policy responses and replay-state loss separately.
  Risk: privileged executor compromise; maintain the manual/offline fallback.
  #2666 orders verified canonical versions within the selected channel and
  binds desired state to manifest digest. Alias drift alone never triggers
  apply; an equal-version digest conflict holds for investigation.

### I6 — Optional UI-triggered operations

- **Dependencies:** I3 + I4 and I5 enrollment/policy foundation.
  **Acceptance:** Dedicated permission/reauth, immutable confirmation plan,
  durable status/history and audit, host policy cannot be overridden, no generic
  command/container endpoints. Disabled by default and separately enrolled.
  Provide admin channel selection, persistent insider reduced-stability copy,
  explicit warning acknowledgment and source/target confirmation. Show effective
  cadence, eligibility/downgrade/recovery status and durable channel-change
  history, including rejected/deferred/failed transitions.
- **Likely files:** Dedicated administration operation API, protected handoff
  adapter, `/admin/updates` destination and proposed components, shared
  HTTP/query contracts. Use provider migrations only if new persisted
  application records require them, covering every affected context/provider.
- **Validation/gate:** Permission/CSRF/replay and malicious request tests,
  accessible confirmation, permission-change cache clearing, browser reconnect
  during API replacement, audit continuity after restore. Maintainer security
  approval and successful I4 recovery rehearsals required before any pilot.
  Test unauthorized channel saves, canceled/unacknowledged insider opt-in,
  keyboard/screen-reader warnings, stable defaults, blocked return-to-stable,
  source/target drift, and history after reconnect and recovery.
  #2667 displays canonical installed/proposed identity, full digest details and
  promotion origin, plus compatibility, downgrade/migration implications and
  durable prior/target/observed history. Mutable aliases cannot be the title
  version or approval identity.

### Branch, identity and promotion validation matrix

| Scenario | Required outcome / issue owners |
| --- | --- |
| Stable tag on development/feature/fork, manual wrong ref, reusable caller spoof | Denied before publication credentials or artifacts; main ancestry and exact VERSION/tag match required. #2668 |
| Insider request from main; malformed or stale VERSION; mismatched tag | Denied; only development plus the approved derivation publishes insider. #2668 |
| Run retry, attempt change, old-run rerun, counter reset, concurrent pointer writes | Same allocation key reuses N; new attempt reserves greater N. No reuse/reset/overwrite; stale source or lower canonical version cannot advance a pointer. #2668/#2660 |
| Supported insider versus malformed or `ios/` tags and numeric edge cases | Preserve namespace isolation; define stricter numeric/allocator validation separately. Current regexes do not reject zero/leading zeros. #2668/#2660 |
| Bare release, release/vX.Y.Z or arbitrary release refs | Candidate validation only; no signing/publication. Verify owner/expiry, main exact tag, reviewed merge-back, abandonment and deletion evidence. #2668 |
| Main moves between admission/tag/build; tag is moved or recreated | Reject pre-authorization drift; after authorization build only recorded exact SHA and verify immutable tag. Ancestry alone is insufficient. #2668 |
| Generated tags for prerelease, branch/manual run or stable older-line hotfix | Insider exact prerelease tags plus isolated insider pointer; zero stable aliases. Stable aliases require authorized main tag and scoped non-regression; test every metadata and promotion path. #2660 |
| Backend/frontend/OCI/release record divergence; missing slicer-host/platform | Reject even with a valid signature; all coordinated metadata identical, all digest subjects verified. #2660/#2659 |
| Alias moves after discovery, same-version digest substitution | Approved canonical manifest/digests remain fixed; conflict blocks stage/apply/reconcile/rollback, including offline. #2661/#2662/#2664/#2666 |
| Numeric prerelease ordering, base increment, build-metadata-only difference | SemVer ordering, channel-local replay protection and no silent downgrade; build metadata is not precedence. #2661 |
| Promotion changes only allowed metadata versus unqualified source change | Rebuild complete main/stable set; bind insider origin and new main SHA; requalify other changes; no retag-only promotion. #2668/#2660 |
| Cached frontend, old mobile and pinned worker | Explicit tested protocol range/refresh or blocked state; never falsify build identity or silently repin jobs. #2659/#2660/#2663 |
| Migration advance and cross-channel recovery | Canonical manifest-bound path; supported restore/fix-forward only, no EF down-migration or mutable-tag rollback. #2663 |
| Offline round trip, forged branch claim, replaced promotion origin | Original identities/digests/provenance preserved; invalid/mixed/mutable-only metadata rejected with no network fallback. #2664/#2665 |
| Inventory, approval and history screens | Canonical version/channel/commit/digest and origin shown; alias secondary; blocked migration/downgrade and unknown compatibility visible accessibly. #2659/#2667 |

### Channel-specific validation matrix

These are acceptance tests for implementation, not tests run by this proposal.
Exercise both channels across supported monolith/split and provider fixtures.

| Scenario | Required outcome / owners |
| --- | --- |
| New or legacy installation without selection | Stable selected, observed channel remains evidence-based; no implicit insider/check/execution enrollment. I1/I3/I6 |
| Admin chooses insider; unauthorized user or canceled warning | Explicit reduced-stability warning and privileged confirmation required; rejected/canceled changes perform no enrollment or execution; audit outcome. I3/I5/I6 |
| Stable and insider publish at distinct frequencies | Signed cadence/channel metadata, isolated pointers, complete service/platform sets; no missing service borrowed from the other channel. I2 |
| Discovery/reminders and late responses | Fake clocks prove channel-specific checks/freshness/reminders, jitter/backoff bounds, dedupe isolation, cache revision checks and zero requests when disabled. I3/I5 |
| Policy edit, stable→insider→stable and insider→stable→insider | Retain separate trust-root/channel high-water marks and rejection/supersession records; invalidate stale policy responses without resetting replay protection. #2661/#2664/#2666 |
| Restart, older database restore or lost replay store | Reject previously rejected/superseded metadata even above installed version; neither restored policy/cache nor offline bundles lower durable replay state. Missing/unproven continuity blocks eligibility/apply pending trusted recovery. #2661/#2664/#2666 |
| Stable-to-insider and insider-to-stable | Fresh supported path/preflight, source/target/version/recovery confirmation and policy-bound idempotency; drift requires reapproval. Lower/unsupported targets never silently apply. I3/I4/I6 |
| Mixed-channel inventory or desired state | Block normal eligibility/execution; fence writes and recover only to a complete verified one-channel set, including managed remote workers. I1/I4/I5 |
| Offline import/export/manual commands and reconnect | Selected channel and policy revision preserved and verified; wrong/unknown-channel bundles rejected; no network fallback or silent enrollment reset. I3/I4 |
| Same/cross-channel rollback, schema advance and power loss | Prove safe image-only recovery or coordinated restore/fix-forward; block unsupported downgrade, hold reconciler, preserve channel audit across restore/restart. I4/I5 |
| Admin presentation and history | Accessible persistent insider warning, distinct selected/observed/target state, cadence, compatibility/recovery reasons, confirmed/rejected/failed transition history. I1/I3/I6 |

Channel-specific risks include accidentally enrolling legacy daily builds,
confusing policy with running provenance, replaying another channel's metadata,
partial publication mixing images, aggressive insider polling/alert fatigue,
and treating a return to stable as permission to downgrade schema. The gates
above fail closed on identity/compatibility uncertainty; exact timing remains
bounded local policy, never authority granted by release metadata.
Identity-specific risks include workflow counter reuse, tag/branch assertions
mistaken for provenance, numeric assembly versions mistaken for full SemVer,
cached frontend skew, and relabeled insider bytes masquerading as stable.
Derived-record equality, runtime publication authorization, main rebuilds and
immutable manifest-bound approval are required mitigations, not assumptions.

All future changes follow the repository risk-based review gate. Deployment,
security, API contracts, and this safety-boundary proposal require the high-risk
review path before a PR. This document does not record review approval.

## Proposed GitHub work graph

**Materialized execution graph:** epic #2658 and children #2659–#2668.
The original nine-increment graph was approved for issue materialization;
the live graph additionally contains the existing publication-policy prerequisite
#2668, whose branch/channel baseline is implemented locally, with remaining
implementation gaps noted above. Policy criteria in #2658/#2660/#2661/#2668
have been reconciled; this does not close the implementation gaps. This preserves
native links/edges and approval boundaries. The local verifier returned
**PASS** on 2026-09-12: 10 declared/linked children, 13 edges, first wave
#2659/#2665/#2668, no missing/isolated children or errors. API readback confirms
#2660 is blocked by #2668; no issue or relationship mutation was performed.
Proposal labels below remain architecture labels, not issue numbers.

| Label | Candidate independently deliverable child | Depends on |
| --- | --- | --- |
| A | I1 inventory and existing status UI | None |
| B0 | #2668 local branch/channel baseline implemented; complete remaining implementation criteria before closure | None |
| B | I2 manifest contract, verifier and complete publication, including slicer-host build/signing/SBOM/validation/promotion in `docker-publish.yml` with source-first gates preserved | B0; coordinate vocabulary with A |
| C | I3 checker, settings, compatible alerts, offline metadata and read-only host inventory exporter/importer; prove fresh/complete evidence reaches `Eligible` and reject stale/incomplete evidence | A, B |
| D | I4 host engine, complete staging and journal/preflight | A, B, C's supported Compose `Eligible` fixture gate |
| E | I4 drain, backup, migrations and recovery rehearsals | D |
| F | I4 operator instructions and complete offline bundle | E |
| G | Discovery socket hardening and executor enrollment/policy review | None |
| H | I5 opt-in external pull reconciler | E, F, G |
| J | I6 optional UI trigger, confirmation and durable history | C, H |

First wave: **A, B0, G** after maintainer approval; A delivers value without
waiting for automation. Subsequent waves: **B**, **C**, **D**, **E**, **F**, **H**, **J**.
The epic should describe the entire initiative but not gate A on completion of
its siblings. Every child inherits its increment's evidence, acceptance tests,
risks, review requirements, and rollout gates above.

Maintain the epic using the
[epic dependency protocol](../.github/skills/epic-dependencies/SKILL.md):
preserve exactly one finalized child-plan marker and the existing first-wave
marker, and read native links/edges back. Do not mark the epic ready until the
graph verifies. Channel requirements fit the existing graph; this follow-up
does not create issues or mutate graph relationships.

## Decisions requiring maintainer approval

1. Supported initial execution topologies/OSes, remote-worker enrollment scope,
   and acceptable print/slice downtime; confirm no zero-downtime expectation.
2. Exact outbound opt-in mechanics, per-channel publication/check/reminder/
   freshness/retention limits, proxy/mirror configuration and privacy copy.
   Stable default for existing/new installs, explicit admin insider opt-in with
   reduced-stability warning and single-channel sets are settled requirements.
   Approve the proposed stable 24h / insider 6h checks, 48h / 12h freshness and
   weekly / daily reminders or record bounded alternatives before enablement.
3. Signing technology, trusted identities, offline expiry/revocation and key
   rotation procedures, and ownership of complete release publication.
4. Exact execution/view permissions, reauthentication/handoff mechanism,
   host policy authority, and whether UI-triggered execution is needed at all.
5. Drain/admission semantics for active prints, pinned workers and uncertain
   outbox commands; who approves explicit manual reconciliation.
6. Backup storage/encryption/retention, restore-time/data-loss objectives,
   minimum rehearsal evidence, and ownership of external database recovery.
7. Migration serialization/startup coordination and schema compatibility policy;
   any image-only rollback exception needs explicit proof, not assumption.
8. Discovery socket replacement and privileged-helper containment before
   automation; authorized-signer and host-compromise residual risk acceptance.
9. Issue reuse, approved epic scope and first wave. No issue creation,
   deployment, implementation, commit, or push is authorized by this document.
10. Channel-switch permission/enrollment mechanics, confirmation and audit
    retention, supported cross-channel paths and per-channel rollback evidence.
    Never waive preflight, explicit confirmation, no-mixing or no-silent-
    downgrade safeguards. Confirm stable-return wait/fix-forward/restore UX
    when no compatible target exists; exact recovery mechanisms remain gated
    by the existing migration-safe architecture.
11. Name the authoritative version-derivation workflow and policy owners;
    approve storage, atomic allocation and continuity recovery for the specified
    one-integer insider N for future coordinated server identity while keeping
    mobile alpha/beta/RC sequencing under `ios/`. Reconcile remaining #2668
    closure scope separately.
    No allocator exists by implication.
    `VERSION` remains the sole authored base and tags remain checked outputs;
    no decision may reintroduce competing version sources. Record ruleset,
    environment and bypass ownership. Main/stable, development/insider,
    immutable identity and qualified main rebuild promotion are mandatory.
    No permanent release channels; short-lived stabilization lifecycle and
    merge-back are settled. Operational owners/expiry limits remain gated.
