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

**A complete managed-update/offline recovery bundle is not shipped yet.**
`deploy-docker.sh --prepare-offline` prepares legacy
deployment materials and image caches. It is not a signed complete release,
bounded trusted importer, host recovery CLI, or proof of coordinated restore.
Do not use it to update an installation that needs #2664's recovery guarantees.
There is no supported skip-verification, force-import or replay-reset option.

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
| Deployment and recovery tools | Package the approved host-local updater/status/recovery tool, matching templates, configuration schema, provider-native tooling and these operator instructions. No reliance on the API, package manager, registry or internet being available during recovery. |
| Installation-specific protected backup | Coordinated databases, models/G-code/profiles/artifacts, keys, certificates and config at the same consistency point. Keep private material access-controlled and separate from the redistributable release bundle. Never include publisher credentials. |

## Verified release-metadata bundle (first slice)

`scripts/ci/offline-update-bundle.mjs` covers only the first two table rows and
the host-update CLI archives. It reuses the existing signed release outputs;
it does not create a new manifest format, signer or publisher.

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
and its `.sigstore.json` bundle, and the selected CLI archives.

`verify` fails closed unless all of the following hold:

- The archive parses within fixed size and member-count limits before any file
  is written: no links, traversal, absolute paths, PAX/long names, duplicates,
  unexpected members or trailing data.
- Cosign `verify-blob --trusted-root` accepts both signature bundles for the
  release workflow identity on `main` or `development`, using only the supplied
  trusted root (no network lookup).
- The manifest channel (and version, when given) matches the operator's
  `--channel`/`--version`, and every CLI archive matches its signed SHA-256.

Only then are members written with exclusive-create semantics; on any failure
the staging directory is removed. A successful run writes
`offline-bundle-verification.json` recording the verified identities.

The index always states `installable: false` and `rolloutAuthorization: false`;
`contents.images`, `contents.infrastructure`, `contents.priorRecoverySet` and
`contents.recoveryInstructions` are `false`. Remaining work under #2658:

- #3061: application and infrastructure image archives.
- #3062: prior recovery set and protected-backup references.
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
Implementation is tracked in #2980 (host-local CLI) and #2981 (complete
bundles); #2982 owns this matrix and separately authorized staging/pilot
evidence. #2664 remains open until its full retained acceptance is complete.
