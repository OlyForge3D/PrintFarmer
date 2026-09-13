---
post_title: "Discovery and host enrollment security boundary"
author1: "Parker"
post_slug: "host-enrollment-security"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["deployment", "security"]
ai_note: "AI-assisted design; maintainer and security acceptance are pending."
summary: "Socket-free discovery and a fail-closed contract for future host enrollment."
post_date: "2026-09-12"
---

## Status and approval gate

Issue [#2665](https://github.com/OlyForge3D/PrintFarmer/issues/2665) is a
design-plus-hardening prerequisite, not an executor implementation. The live
issue and the team-root `docs/DEPLOYMENT_UPDATE_STRATEGY.md` proposal informed
this contract. That proposal was not tracked in this branch at implementation
time; this document preserves the actionable boundary without pretending it
shipped.

**Implemented:** the canonical printer-discovery template removes Docker socket
access and extra capabilities, keeps a read-only root and resource limits,
and adds `no-new-privileges`. No socket proxy replaces the mount.
Tests guard canonical fragments, merged provider configurations, actual generator
outputs in deployment CI, and HTTP discovery without container-control access.

**Not implemented or enabled:** host enrollment, execution permissions/routes,
signature verification, durable replay storage, update/channel-switch UI,
host updater, pull reconciler, or privileged listener. Nothing in this document,
a fixture, a selected channel, an environment variable, or an API setting
enables those features. All new/existing installations remain unenrolled;
future selected-channel initialization is stable without relabeling observed
legacy builds. Native installations have visibility/discovery, not automatic
container replacement. Current manual installation remains operator-owned.

| Required recorded decision | Owner / status |
| --- | --- |
| Accept socket removal and network-only observation threat model | Repository maintainer; pending explicit issue comment |
| Accept host-compromise and authorized-signer residual risks | Repository maintainer; pending explicit issue comment |
| Approve initial OS/topology, limits, trust bootstrap/rotation and recovery contract below | Host operator and repository maintainer; proposed, not approved |
| Name publication ruleset, environment, allocator and bypass owners | #2668 maintainer decision; pending, never inferred from branch ownership |
| Approve signing identity and offline verification implementation | #2660 plus security reviewer; pending |
| Peer threat-model review and high-risk pre-PR panel | Bishop, Hicks and Vasquez, none implementation authors; pending |
| Permit H pilot | Separate explicit maintainer/security approval after prerequisites; blocked |

Record reviewer/maintainer identity, exact reviewed SHA, decision, exceptions and
date in #2665. An agent comment is progress evidence, not maintainer approval.
No PR or pilot is authorized by this document.

## Threat model and observation choice

Assets: host/container authority, printer actions and queues, database/blob
consistency, release trust, host credentials, backup data, and audit/replay state.
Treat a compromised API, browser session, discovery service, registry, LAN
printer, imported bundle or desired-state response as untrusted. A release signer
and host administrator are separate authorities, not extensions of farm roles.

The removed `:ro` Docker socket mount restricted filesystem operations, not
Docker API methods; it still conferred host-compromise authority. Required
discovery is printer observation through the existing shared TCP/HTTP probes
and authenticated API registration, not container inspection. No Docker client
is needed. Keep the image's non-root discovery user, bridge network, unpublished
HTTP port, all capabilities dropped, read-only root, bounded scratch/resources,
and service-key authentication. Do not add host PID/network namespaces, devices,
runtime directories, named pipes, Docker TLS credentials or generic proxies.

Containers on the shared network still reach each other. Socket removal does
not make printer-supplied responses trustworthy, prevent every LAN scan, or
eliminate denial of service. Network egress policy and reachable configured
subnets remain operator responsibilities. Discovery may register observations;
"read-only" does not mean its authenticated registration produces no API writes.
It cannot observe authoritative running image digests. Future inventory can use
self-reported build metadata (explicitly unverified) or independently verified
host snapshots; missing Docker identity stays unknown, not fabricated.

Use the [deployment migration procedure](DEPLOYMENT.md#socket-free-printer-discovery).
Retire old overrides and recreate the service; do not silently retain legacy
mounts. No generated root Compose copy is a source of truth.

## Proposed host enrollment contract

This section resolves a proposed design for review. Until approved and
implemented in the later executor increments, **the executable operation
allowlist is empty**. Shipping documentation or passing design fixtures is
never proof of a functioning authorization control.

### Identity, trust storage and scope

- Bootstrap only from an authenticated, interactive host-administrator session,
  outside the API. Generate a random installation UUID and host key pair;
  bind a separate enrollment epoch, host public-key fingerprint and canonical
  Compose project/topology fingerprint. Never derive identity from hostname,
  application database contents, image tags, or user-uploaded trust material.
- Proposed initial managed scope: single Linux host with Docker Engine/Compose,
  explicitly enrolled split or monolith topology, PostgreSQL or SQL Server.
  Native, Windows/Desktop engines, Kubernetes, multi-host orchestration and
  database-engine major upgrades are unsupported until separately qualified.
  A remote worker needs its own enrollment and policy; topology membership
  alone grants no control. Externally managed databases remain owner-managed
  and require independently verified backup/recovery evidence.
- Host policy/identity: root-owned `/etc/printfarmer-host`, directories `0700`,
  private policy/key files `0600`; protected journal/replay database under
  `/var/lib/printfarmer-host`, outside every application volume and backup restore.
  These are proposed future locations, not files created by this change.
  Use OS-protected key storage where available; prohibit symlink traversal,
  world/group-writable parents and mounting these paths into web containers.
- Pin exact repository, publisher workflow identity, OIDC issuer/subject or
  verification keys chosen by #2660, registry allowlist, platform and permitted
  source-branch/channel pairs. Require verified provenance subjects matching
  each immutable digest, not self-asserted labels or "signature valid" alone.
  Trust bootstrap material must arrive through an independently authenticated
  operator channel. Offline bundles cannot enroll their own trust roots.
- Rotation requires host-administrator confirmation plus existing trusted
  authority verification, monotonically increasing trust revision, bounded
  overlap and explicit old-key revocation. On loss/compromise, stop admission,
  revoke transport/signing identities, preserve replay/journal history, and use
  a separately authenticated host recovery ceremony. Never "trust on first use"
  after restore; missing continuity means `NeedsOperator`.
- #2668 owns named ruleset/environment/bypass owners and durable allocation.
  #2660 owns digest-bound manifests and signing/trust distribution. Record their
  approved concrete identities in host policy before enrollment; wildcards,
  arbitrary uploaded CA roots and a repository name alone are insufficient.

### Independent host policy and bounded operations

The future host operator separately opts into each transport and operation.
Prefer operator-run updates first, then outbound authenticated pull
reconciliation without an inbound privileged listener. An optional helper
requires a distinct enrollment and mutual authentication; controlling Docker
still makes it root-equivalent, even behind fixed operations.

Proposed future operation names: `observe`, `plan`, `stage`, `apply`,
`recover`, `pause-admission`. Each maps to a reviewed implementation and an
opaque plan/operation ID. No shell strings, arbitrary Compose, image names,
commands, local paths, URLs, registry credentials, or container IDs from API
intent. Host-selected paths/endpoints are immutable enrolled configuration.
Recovery is a separate verified plan, not a rollback command escape hatch.

Host policy independently fixes allowed channels and minimum cadences,
maintenance timezone/windows, maximum downtime, active-printer/queue drain
requirements, backup coverage/retention and supported recovery classes.
Approval cannot widen these values. Metadata cannot shorten windows, schedule
more frequent execution or skip external database-owner agreement.

Proposed conservative limits, subject to host acceptance:

| Limit | Proposed boundary |
| --- | --- |
| Concurrent mutating operations | One per installation, durable exclusive lock |
| Pending plans / approval life | Two; maximum five minutes, never beyond preflight or maintenance validity |
| Desired-state polling | No faster than 60 seconds; bounded exponential backoff to 15 minutes |
| Planning admission | One per minute, burst two; reject excess without building a queue |
| Metadata / intent size | 1 MiB / 64 KiB; parser depth and item-count bounds |
| Staging | One set, fixed byte/time quota enrolled per host; reject absent quota |
| Capacity reserve | Full images + backups + recovery set + journal reserve before admission; recheck before side effects |
| Resource use | Host-enforced CPU/memory/I/O caps, bounded requests/timeouts; absence of limits blocks enrollment |
| Journal | Reserved capacity and bounded redacted fields; archive under operator retention, never truncate replay high-water marks |

Disk exhaustion or loss of audit/journal durability blocks new work; never prune
the only recovery set or resume after a failed journal write. Numbers are not
production defaults until approved. Stable/insider check cadence is separate
from host execution cadence and cannot grant unattended enrollment.

## Proposed authorization and channel changes

Require separate `updates:execute` and `updates:manage-policy` permissions, with
explicit administrator authorization for enrollment/trust/channel changes.
Neither follows from ordinary settings-write, viewing inventory, selecting a
channel, checking releases, farm service keys, nor being an API administrator
on a compromised API. Reuse the application's verified identity transport but
require fresh interactive reauthentication and request-origin/CSRF protection
appropriate to that transport. Reject missing/foreign origin and missing CSRF
proof for cookie-authenticated mutations. Service identities cannot stand in
for an interactive operator.

Insider requires explicit opt-in and confirmation of this exact warning:
**Insider updates may arrive more frequently and have reduced stability compared
with stable releases.**
Persist the visible selected state and acknowledgment actor/time; canceling
does not save anything. Selecting insider grants neither checks, unattended
enrollment nor execution. Host agreement is independently required when
enrolled. Generic settings writes must reject channel/trust/policy mutation.

Authenticate desired-state/handoff through mutually authenticated transport
bound to the host and installation. A compromised API can still issue authentic
intents, so independently verify every host-policy condition. Approval binds:

- Actor, operation, installation UUID, enrollment epoch, nonce, idempotency ID,
  issued/expiry time, monotonic policy/trust revision, and independent host consent.
- Prior/target channel, warning acknowledgment, reason and confirmed policy diff.
- Immutable manifest digest, complete one-channel set hash, plan hash,
  topology/configuration/schema fingerprints and fresh compatibility/preflight.
- Downgrade assessment, downtime/window, backup/recovery class and source state.
- Canonical release ID, `sourceTag`, full `sourceCommit`, `authorizedBranchHead`,
  allocation/build ID and attempt, workflow identity, and promotion lineage.

Only exact `vX.Y.Z` from publisher-verified `main`/stable and
`vX.Y.Z-insider.N` from verified `development`/insider qualify. Components have
no leading zeroes and N is positive. Require full-SHA agreement with verified
branch-at-authorization evidence; matching tag, main ancestry, or a live branch
string alone is insufficient. `release/vX.Y.Z` is stabilization only.
Historical beta/rc/daily and mobile `ios/` identities do not acquire eligibility.
Aliases can locate candidates, never authorize apply. Bind allocation and
authorization-record digests so reuse with changed bytes is an identity conflict.

Check all bindings at admission and immediately before each side effect.
Changed policy, topology, configuration, schema, target identity or expired
preflight invalidates approval. Journal an accepted nonce and canonical request
hash atomically before side effects. Identical retry returns its recorded
operation; reused nonce/key with differing bytes is rejected. Serial admission
prevents concurrent replay; uncertain checkpoints require reconciliation, not
blind retry. Preserve per-trust-root/per-channel high-water marks, rejected
identities and consumed allocations across database restore and branch deletion.

One complete target-channel set is mandatory. Signed-but-disallowed metadata,
forged cadence, cached manifests and offline imports cannot widen host policy.
Returning to stable is not authorization for downgrade: require a supported
compatible path or wait/fix-forward/explicit recovery. Even a signed recovery
plan cannot reset channel/trust/enrollment history or bypass backup requirements.

## Kill switch, audit, revocation and fallback

The host kill switch denies new admissions and requests suspension at the next
durably recorded safe checkpoint. Never blindly kill a migration or interrupt
physical printing. Observe/reconcile in-progress database work before retry;
unknown outcomes become `NeedsOperator`. Disabling release checks is not a kill
switch. Revocation blocks new requests immediately, invalidates pending approvals
and stages, and stops active work only at a safe checkpoint.

Write audit outside replaced/restored application state: actor, prior/new
channel and policy revisions, warning/confirmation, reason, canonical attempted
target, promotion origin, request/operation hashes, accepted/rejected outcome,
checkpoint and timestamp. Record rejection too, using bounded reason codes.
Never log tokens, keys, request bodies, printer credentials, environment dumps,
secret configuration, backup contents or arbitrary remote error text.
Use secret-safe configuration fingerprints and restricted audit access/export.
Protected off-host append-only replication detects host-local history tampering;
a host administrator can compromise local evidence, so do not claim otherwise.

Manual deployment remains available under
[migration-safe guidance](DEPLOYMENT.md#migration-safe-upgrades), without an
updater success/rollback guarantee. Future manual/offline recovery must verify
the same manifest/provenance, original timestamp, installation, policy/channel,
expiry/revocation and replay state without implicit network fallback. Imported
artifacts never re-enroll, reset trust or supply execution paths. If continuity
cannot be established offline, stay disabled and require host recovery.

## Abuse-case design fixtures

These are review fixtures for the future authorizer, **not implemented security
tests or evidence that an executor exists**. Each starts with a valid enrolled
plan; change only the input in the second column. Every rejection leaves host
policy/selection unchanged, performs no Docker/physical-printer operation, and
writes a bounded redacted audit outcome. Runtime conformance tests must implement
these before the H pilot; a document-text assertion would not prove enforcement.

| Fixture | Input mutation | Required result |
| --- | --- | --- |
| Compromised API | Authentic desired state requests unallowed operation/channel | Reject independent host policy; API approval is insufficient |
| Unauthorized save | Ordinary settings writer changes channel/trust/branch | Reject dedicated permission; never partially persist |
| Canceled warning | Insider selected but confirmation canceled/missing | No change; audit canceled outcome |
| Reauthentication | Stale/missing interactive proof or service-key identity | Reject |
| Origin | Foreign/missing required origin or absent cookie CSRF proof | Reject before issuing approval |
| Expiry | Expired approval/preflight, future-issued intent or clock uncertainty | Reject; no freshness grace bypass |
| Nonce | Replay consumed nonce with changed bytes | Reject; identical retry returns existing operation only |
| Drift | Change topology/config/schema, source state or policy revision | Invalidate approval and require fresh plan |
| Substitution | Different installation, registry, manifest, image or platform | Reject even if target is signed |
| Cross-channel set | One image belongs to the other channel | Reject whole set; no fallback |
| Cadence | Metadata shortens window, polling or downtime/backup constraint | Reject host-policy escalation |
| Rollback reset | Restore application DB or historical policy/replay database | Preserve host high-water state; unknown continuity blocks |
| Offline signer | Bundle supplies its own key, expired/revoked trust | Reject; import cannot bootstrap trust |
| Direct tag | Matching tag without verified authorization or only main ancestry | Reject |
| Forged provenance | Alter branch/channel, source tag/full SHA or authorization record | Reject signature/identity/policy mismatch |
| Allocation reuse | Reuse allocation/build identity with another SHA or digest | Reject identity conflict; retain evidence |
| Stabilization ref | Publish/enroll from release branch or deleted ref claim | Reject; only verified main/development admission qualifies |
| Promotion | Retag insider bytes as stable or substitute promotion origin | Reject; stable needs qualified main rebuild provenance |
| Policy revision | Replay previously approved policy/channel switch | Reject monotonic revision/epoch check |
| Arbitrary execution | Supply shell, path traversal, URL, container ID or Compose | Reject closed operation contract before any fetch |
| Resource exhaustion | Oversize intent/bundle, deep archive, full disk, quota/rate excess | Reject bounded parsing/admission; preserve recovery and journal reserve |
| Secret leakage | Remote error or plan contains credentials/control characters | Bounded reason code only; no raw payload in audit/UI |
| Kill/revoke | Disable during migration, replay approved stage afterward | Safe checkpoint/NeedsOperator; no blind kill or new admission |
| Host compromise | Administrator changes verifier/journal or authorized signer ships malicious code | Cannot contain through this interface; explicit residual-risk acceptance required |

## Validation and residual limits

`python tests/test-discovery-boundary.py` checks all canonical Compose fragments,
four provider/discovery merged cases using the existing merge implementation,
socket/proxy/environment/parent-directory injection and privilege regressions.
`--compose <generated-file>` checks actual generator output; deployment CI calls
it through `test_discovery_network_consistency` and retains shared-key wiring
coverage. `SocketFreeDiscoveryTests` exercises the shared probe through a real
loopback HTTP printer, alongside existing discovery regression tests.

These checks are regression guards, not a generic Compose security validator:
custom images can tunnel arbitrary traffic, user overrides can reintroduce host
authority, and static tests cannot prove a deployed container was recreated.
Required rollout evidence includes host inspection, known-printer scan, Linux
capability isolation and high-risk review. The host-enrollment fixtures above
remain design evidence only. Accepting them, socket removal or passing tests
never authorizes a pilot or closes the maintainer/security gates.
