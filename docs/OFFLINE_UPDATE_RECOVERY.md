---
post_title: "Offline update and recovery delivery requirements"
author1: "Parker"
post_slug: "offline-update-recovery"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["deployment", "offline", "recovery"]
ai_note: "AI-assisted delivery contract; no complete offline bundle is claimed."
summary: "Separate legacy image caching from the required verified, network-denied recovery bundle."
post_date: "2026-09-24"
---

## Current support

**A complete managed-update/offline recovery bundle is not shipped yet** (#2981).
`deploy-docker.sh --prepare-offline` prepares legacy deployment materials and
image caches. It is not a signed complete release, a bounded trusted importer,
or proof of coordinated restore. Do not use it to update an installation that
needs #2664's recovery guarantees. The signed host-local status/recovery CLI
package (#2980, #3041) is a separate release asset. It can be carried to a
disconnected host as described in the
[runbook](HOST_UPDATE_RUNBOOK.md#install-the-signed-cli-package), but it is only
one item in the bundle below. There is no supported skip-verification,
force-import or replay-reset option.

The first delivered slice (#2981) is a
[verified release-metadata bundle](#verified-release-metadata-bundle-first-slice):
it carries the original signed release bytes and the host-update CLI to a
network-denied host and verifies them with bounded extraction. It is explicitly
**not installable** and grants no rollout authority.

Connected installations need a proven minimum host-local recovery path but do
not need to hand-carry a bundle. Disconnected installations additionally need
all the material and evidence below. See the
[operator runbook](HOST_UPDATE_RUNBOOK.md) for authorization and stop conditions.
This contract does not invent a second manifest format or release publisher:
the [release guide](RELEASE_GUIDE.md) owns original signed release identity.

## Required bundle contents

The future exporter/importer must account for every item before calling a
bundle complete; this table is not an archive layout or an implementation.

| Material | Required checks |
| --- | --- |
| Original manifest and signature bundle | Preserve exact signed bytes and canonical source/tag/version/channel/build identity, manifest digest, promotion/branch authorization where required by the canonical contract, and all selected index/platform digests. No mutable-only identities or same-version byte substitutions. |
| Offline verification evidence and tooling | Preserve provenance and approved trust-root continuity, expiry/revocation evidence and pinned verification tools. Bundle-supplied signer material cannot enroll itself. Verification must work after source branch movement without live ancestry lookup. |
| Target application and infrastructure images | Include every selected platform/service image, database/runtime/proxy/add-on dependency and required worker under the supported topology contract. Six published application images alone are not every installation's infrastructure. Verify archive content against immutable identity; no missing-image downloads or builds. |
| Prior recovery set | Retain complete compatible prior manifests/images and effective configuration, schema/format compatibility and backup references. Prior-channel artifacts are recovery-only under explicit verified authorization, not new offers or implicit channel consent. |
| Deployment and recovery tools | Package the approved host-local updater/status/recovery tool, matching templates, configuration schema, provider-native tooling and these operator instructions. The signed CLI archive, its checksum list, the Cosign bundle (#3041), per-archive SBOMs and the verifying installer (#3045) are the status/recovery tool. No reliance on the API, package manager, registry or internet being available during recovery. |
| Installation-specific protected backup | Coordinated databases, models/G-code/profiles/artifacts, keys, certificates and config at the same consistency point. Keep private material access-controlled and separate from the redistributable release bundle. Never include publisher credentials. |

## Verified release-metadata bundle (first slice)

`scripts/ci/offline-update-bundle.mjs` covers the original signed manifest row,
the bounded-verification part of the offline verification row, and the
host-update CLI archives. It does not package Node.js, Cosign or trust-root
continuity/expiry/revocation evidence: the operator provisions a pinned Cosign
and an approved `trusted_root.json` out of band, and trust continuity is #3064.
It reuses the existing signed release outputs; it does not create a new
manifest format, signer or publisher.

Assemble on a connected host from a downloaded release asset directory:

```bash
node scripts/ci/offline-update-bundle.mjs assemble \
  --release-assets ./release-assets --channel stable \
  --output ./printfarmer-offline.tar [--version <v>] [--runtime <rid>]... \
  [--trusted-root ./trusted_root.json]
```

Verify on the network-denied host into a staging directory that must not
already exist:

```bash
node scripts/ci/offline-update-bundle.mjs verify \
  --bundle ./printfarmer-offline.tar --channel stable \
  --trusted-root ./trusted_root.json --staging ./offline-staging
```

The bundle is a flat, uncompressed ustar archive. Its first member,
`offline-bundle.json`, is an unsigned index; every trusted fact is re-derived
from the signed members, never from the index. Members are the exact
`update-manifest.json` and its `.sigstore.json` bundle, the CLI `SHA256SUMS`
and its `.sigstore.json` bundle, and each selected runtime's CLI archive
together with its SPDX SBOM (`.spdx.json`).

`verify` fails closed unless all of the following hold:

- The archive parses within fixed size and member-count limits before any file
  is written: no links, traversal, absolute paths, PAX/long names, duplicates,
  unexpected members or trailing data.
- Every member matches its SHA-256 as streamed from the bundle.
- Cosign `verify-blob --trusted-root` accepts both signature bundles for the
  release workflow identity of the expected channel (`main` for stable,
  `development` for insider), using only the supplied trusted root (no network
  lookup).
- The manifest channel (and version, when given) matches the operator's
  `--channel`/`--version`, the signed CLI checksum list names exactly every
  supported archive and SBOM, every carried CLI archive and SBOM matches its
  signed SHA-256, and each carried SBOM is a structurally valid SPDX 2.x
  document.

The staging directory is created exclusively and members are written with
exclusive-create semantics into its `.unverified/` quarantine subdirectory.
Only after every check passes are they moved to the staging root, and
`offline-bundle-verification.json` is written last. On any caught failure the
staging directory is removed. **Only the presence of the verification record
marks success;** a staging directory left by an interrupted run (for example
with `.unverified/` and no record) is untrusted and must be deleted.

### Prior recovery set and protected-backup reference

A bundle may bind the prior release a host returns to on recovery. The prior
set is that release's original signed metadata: its `update-manifest.json` and
`.sigstore.json` bundle, and its CLI `SHA256SUMS` and `.sigstore.json` bundle.
It must be accompanied by a reference to the installation's protected backup
taken for that prior release; neither is accepted without the other.

```bash
node scripts/ci/offline-update-bundle.mjs assemble ... \
  --prior-release-assets ./prior-release-assets \
  --protected-backup ./protected-backup.json \
  [--prior-mode packaged|local-reference]
```

The protected-backup reference is a JSON object with exactly `id`, `sha256`
(lowercase SHA-256 of the backup), `locationClass` (`host-local`,
`attached-volume` or `external-storage`) and `releaseVersion` (must equal the
prior release version). The closed field set leaves no place for backup
contents, credentials, connection strings or paths; the backup itself stays
access-controlled and outside the redistributable bundle.

- `packaged` (default) carries the prior files as `prior-*` members.
- `local-reference` carries no prior members; the index binds each prior file
  by name, size and SHA-256, and `verify` requires the operator's copy through
  `--prior-recovery-set <dir>`. Each local file must match its bound digest and
  is copied into the quarantine before authentication.

Both `assemble` and `verify` fail closed unless the prior set is on the same
channel, strictly older than the target (by sequence and version), its signed
CLI checksum list names exactly every supported archive and SBOM, and Cosign
accepts both prior signature bundles for the channel's release workflow
identity using the supplied trusted root. A tampered, forged, wrong-channel,
same-version or newer prior set, a missing packaged member, an index that
misstates the prior set, a local copy supplied for a packaged set, or a local
set supplied for a bundle without one is rejected. On success the verification
record's `priorRecoverySet` names the mode, prior release identity, manifest
digest and protected-backup reference; it is `false` when the bundle binds no
prior set.

The prior set does not yet include prior images or effective configuration;
those follow the image archives (#3061) and host-local import (#3063). A bound
prior set is recovery-only material and never a new offer or implicit channel
consent.

The index always states `installable: false` and `rolloutAuthorization: false`;
`contents.images`, `contents.infrastructure` and
`contents.recoveryInstructions` are `false`. `contents.priorRecoverySet` is
`true` only when the bundle binds a complete prior set with its
protected-backup reference. Remaining work under #2658:

- #3061: application and infrastructure image archives.
- #3062 (delivered): prior recovery set and protected-backup references.
- #3063: host-local import with Bash/PowerShell parity and bound recovery
  instructions.
- #3064: replay protection, channel continuity and offline trust expiry.

## Import and continuity rules

Import must be bounded before extraction or allocation: reject traversal,
absolute paths, links/reparse escapes, duplicate/conflicting entries, oversized
or incomplete archives and expansion bombs. A failure must leave no
success-shaped import or partially activated set. The importer must fail
closed on untrusted, expired, revoked, wrong-platform or mixed-channel evidence.
Approved offline trust expiry/revocation and retention policy remain explicit
delivery decisions, not defaults an operator may invent.

Authenticate sequence/channel/identity before advancing durable replay state.
Persist authenticated decisions atomically before offer or action, including
later compatibility/policy rejection. Keep separate enrolled-trust-root/channel
high-water marks and rejected/superseded identities outside restored app
databases and replaced containers. Reject lower sequences, equal-sequence
different identities and rejected/superseded metadata even above the installed
version. Reuse is limited to the identical non-rejected/non-superseded identity
that still passes every current gate.

An import or restore cannot replace or lower existing replay state. Missing or
unproven continuity blocks execution pending trusted recovery; no network
fallback or reset override. Export/import must preserve selected/observed/
source/target channels, policy revision, signed set and cadence metadata through
restart and reconnect. Wrong or absent channel evidence must not default to
stable or enroll insider.

An intentional offline channel switch needs the same preflight, supported
version/schema/storage transition, downtime/recovery preview, explicit
administrator confirmation and durable audit as online operation. Insider
requires the runbook's reduced-stability acknowledgement. Unsupported downgrade
stays blocked; neither an old archive nor administrative intent makes it safe.

Only a fixed approved operation plus validated plan/operation identifier may
be exported as an executable instruction. No arbitrary shell fragments,
caller-chosen paths/URLs/commands or credentials. Drift invalidates approval.
On reconnect refresh trust/metadata and actual installed inventory before
eligibility; do not automatically execute a previously imported plan.

## Evidence required before complete delivery

Run isolated **host update/recovery tests**, not a release publication
workflow or a production pilot without authorization. Capture exact code and
bundle identities, tool versions, network-denial controls, attempted outbound
requests, topology, provider, operation checkpoints and recovery outcome.
Fixtures must not read or modify a real deployment's credentials or storage.

The acceptance matrix includes network-denied monolith and split Compose with
PostgreSQL and SQL Server, required infrastructure, external-storage/DB-owner
evidence and optional/remote pinned-worker cases. Exercise Bash and PowerShell
entry points using the same bundle contract; a Windows documentation/link check
does not prove Linux restore or runtime parity.

Positive cases require stable and insider original/imported identity equality,
fresh-host import without trust self-enrollment, install and coordinated
restore using only packaged instructions. Include supported channel switches
and both stable-insider-stable and insider-stable-insider continuity.

Negative cases include missing image/trust/config, malicious archive, modified
bytes, forged branch/promotion claims, moved aliases, wrong platform, mixed
digests/channels, unsupported downgrade, expired/revoked trust, invalid-signature
sequence poisoning, equal-sequence substitution and missing/rolled-back replay
storage. Authenticate sequence 41 above the installed version but reject it;
separately supersede 41 with 42 without installing either. Reimported 41 must
remain rejected after policy edits, both channel round trips, restart and older
app DB/policy/cache restoration. Intact independent-channel records and current
allowed metadata must still work.

No fallback network request, local build, fabricated recovery success or
physical printer command is acceptable. Retain full failure evidence, prove
fences stay closed on uncertainty, and confirm safe post-restore reconciliation.
The host-local CLI is delivered (#2980). Its open recovery gaps are:

- physical printer reconciliation (#2999)
- provider and topology coverage (#3000)

Complete bundles are tracked in #2981. #2982 owns this matrix and separately
authorized staging/pilot evidence. #2664 remains open until its full retained
acceptance is complete.
