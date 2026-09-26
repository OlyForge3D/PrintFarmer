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
**not installable** and grants no rollout authority. A
[host-local import command](#recovery-instructions-and-host-local-import-3063)
(#3063) verifies a complete bundle, loads only its verified images and records
every decision durably. It also enforces the offline trust expiry policy and
admits the release through the host's durable replay store, refusing replays,
downgrades and cross-channel imports (#3064, see
[replay admission and trust expiry](#replay-admission-channel-continuity-and-trust-expiry-3064)).
It still installs, activates and authorizes nothing.

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
| Deployment and recovery tools | Package the approved host-local updater/status/recovery tool, matching templates, configuration schema, provider-native tooling and these operator instructions. The signed CLI archive, its checksum list, the Cosign bundle (#3041), per-archive SBOMs and the verifying installer (#3045) are the status/recovery tool. The signed [deployment set](#deployment-set-and-approved-tools-3081) (#3081) carries the templates, configuration schema and approved tool pins. No reliance on the API, package manager, registry or internet being available during recovery. |
| Installation-specific protected backup | Coordinated databases, models/G-code/profiles/artifacts, keys, certificates and config at the same consistency point. Keep private material access-controlled and separate from the redistributable release bundle. Never include publisher credentials. |

## Verified release-metadata bundle (first slice)

`scripts/ci/offline-update-bundle.mjs` covers the original signed manifest row,
the bounded-verification part of the offline verification row, and the
host-update CLI archives. It does not package Node.js, Cosign or trust-root
continuity/expiry/revocation evidence: the operator provisions a pinned Cosign
and an approved `trusted_root.json` out of band; `import` applies the
[offline trust expiry policy](#replay-admission-channel-continuity-and-trust-expiry-3064).
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

The index is unsigned, so the reference it carries is only a claim. `verify`
therefore requires the operator's own expected reference through
`--protected-backup <reference.json>` whenever the bundle binds a prior set,
and fails closed unless every field equals the index's reference; supplying one
for a bundle without a prior set is also rejected. The operator's record of the
backup, not the transported bundle, is the trust source for the reference.

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
those remain future work under #2981. A bound
prior set is recovery-only material and never a new offer or implicit channel
consent.

The index always states `installable: false` and `rolloutAuthorization: false`:
a bundle by itself is never installable. Only an `import` decision record can
state `installable: true`, after replay admission (#3064); rollout enablement
remains out of scope.
`contents.recoveryInstructions` is `true` only when the bundle carries the
signed, release-bound
[recovery instructions](#recovery-instructions-and-host-local-import-3063)
together with their signature bundle. `contents.priorRecoverySet` is
`true` only when the bundle binds a complete prior set with its
protected-backup reference. `contents.images` and `contents.infrastructure` are
both `true` when the bundle carries the image set described below and both
`false` otherwise; a bundle that claims one without the other, or carries only
part of the set, is rejected. `contents.deploymentSet` is `true` only when the
bundle carries the signed
[deployment set](#deployment-set-and-approved-tools-3081) together with its
signature bundle, every approved tool it pins and the image set; a partial set is
rejected. Remaining work under #2658:

- #3061 (delivered): application and infrastructure image archives.
- #3062 (delivered): prior recovery set and protected-backup references.
- #3063 (delivered): host-local import with Bash/PowerShell parity and bound
  recovery instructions.
- #3064 (delivered): replay protection, channel continuity and offline trust
  expiry.
- #3081 (delivered): complete offline set — deployment templates, configuration
  schema and approved tools bound to the signed release.
- #3080: installation and activation from a complete imported set.

### Application and infrastructure images (#3061)

Every release publishes `infrastructure-images.json` and its
`infrastructure-images.sigstore.json` bundle. The release build reads the
repository lock `scripts/docker/infrastructure-images.lock.json`, proves every
pinned digest (and, for multi-platform indexes, every pinned platform child)
against the registry with `docker buildx imagetools inspect --raw`, binds the
list to the exact release identity (tag, version, channel, source branch and
commit, build ID and sequence) and signs it with the same workflow identity as
the manifest. A moved or unavailable pin fails the release before any image is
built. The offline-supported infrastructure images are PostgreSQL
(`postgres:16-alpine`, amd64 and arm64), nginx (`nginx:alpine`, amd64 and arm64)
and SQL Server (`mcr.microsoft.com/mssql/server:2022-latest`, amd64 only).
Optional add-ons and monitoring images are not part of the offline-supported
topology. To change a pin, update the lock (digests sorted by `id`) in a
reviewed PR.

To include images, gather them on the connected host into one OCI image layout
directory, preserving digests, and pass it to `assemble --images`:

```bash
# For each application service (manifest index digest) and infrastructure pin:
skopeo copy --all --preserve-digests \
  docker://ghcr.io/olyforge3d/printfarmer-api@sha256:<index-digest> oci:./images:api
skopeo copy --all --preserve-digests \
  docker://docker.io/library/postgres:16-alpine@sha256:<pinned-digest> oci:./images:postgres
node scripts/ci/offline-update-bundle.mjs assemble ... --images ./images
```

Only each image's pinned root, its release-selected platform manifests, their
configs and layers are copied; the layout's own `index.json` is ignored. The
bundle then carries one nested, uncompressed ustar OCI layout per image,
`image-<service>.oci.tar` for each manifest service and
`infrastructure-<id>.oci.tar` for each signed infrastructure pin, plus the signed
infrastructure list and its bundle. Each nested archive holds exactly
`oci-layout`, a canonical `index.json` with one descriptor naming the pinned
digest and the alias the Compose templates use, and `blobs/sha256/<hex>` for the
reachable closure, in canonical order.

With images present, `verify` additionally requires that:

- Cosign accepts `infrastructure-images.sigstore.json` for the same channel
  identity, and the list is bound to the manifest's release identity.
- The bundle carries exactly the release-selected image set: every manifest
  service and every signed infrastructure pin, no missing or extra image.
- Every nested archive stays within per-archive size, blob-count and JSON limits;
  its alias and root digest match the signed identity; every selected platform
  child digest matches the signed per-platform digest; configs match their
  platform (`linux/arm64` accepts variant `v8`); every blob matches its digest and
  size; and no unselected platform, attestation or unreachable blob is present.

The verification record then lists every image with its reference, digest,
platforms, size and SHA-256. Load them on the network-denied host with:

```bash
node scripts/ci/offline-update-bundle.mjs load --staging ./offline-staging --channel stable \
  --trusted-root ./trusted_root.json [--cosign <path>] [--docker <path>]
```

`load` does not trust the mutable verification record for image expectations.
It re-authenticates the staged `update-manifest.json` and
`infrastructure-images.json` signatures offline against the operator-supplied
trusted root, derives the required image set, digests and platforms from those
signed bytes alone, requires the record to name exactly that set, re-hashes and
re-verifies every archive before loading any of them, and streams the same open file to `docker load` (Docker Engine 25 or later for
OCI archive support). It never pulls, builds or fetches anything; a changed
archive or a staging directory without a verified image set is rejected.

### Deployment set and approved tools (#3081)

Every release publishes `offline-deployment-set.json` and its
`offline-deployment-set.sigstore.json` bundle, signed with the same workflow
identity as the manifest and bound to the same release identity. The release
build generates it from the source checkout; it contains:

- **Templates.** The exact bytes (with size and SHA-256) of every deployment
  template a supported topology needs: the common, split, monolith, discovery,
  slicer-host, OrcaSlicer worker and PostgreSQL/SQL Server Compose templates, the
  container entrypoint and security configuration, and the nginx configurations.
  Optional add-ons (monitoring, registry, emulators, pgAdmin, Spoolman, go2rtc,
  Obico ML and telemetry) are outside offline support and are never carried.
- **Configuration schema.** Version 1: the sorted set of `${NAME}` variables the
  carried templates consume. It is derived from the template bytes, so it always
  describes exactly those templates.
- **Approved tools.** The host tools pinned by upstream URL, SHA-256 and size in
  the repository lock `scripts/docker/offline-tools.lock.json` (currently Cosign
  v3.0.6 for linux/amd64, linux/arm64 and windows/amd64, matching the version the
  release workflow pins), plus the database tools that ship inside pinned
  infrastructure images (`pg_dump` and `pg_restore` in `postgres`, `sqlcmd` in
  `mssql`). Docker Engine 25 or later is a host prerequisite and is not bundled.
- **Topologies.** `monolith-postgres`, `monolith-sqlserver`, `split-postgres` and
  `split-sqlserver`, each naming its images, templates and tools. The document
  states `rolloutAuthorization: false`.

To change a tool pin, update the lock (artifacts sorted by name) in a reviewed PR.
To include the set, download every pinned tool artifact into one directory on
the connected host, verify it against the lock and pass the directory with the
image layout:

```bash
node scripts/ci/offline-update-bundle.mjs assemble ... --images ./images --tools ./tools
```

`assemble` requires `--images` with `--tools`, checks each tool file against its
signed size and SHA-256 and packages it as `tool-<artifact name>` beside the
signed pair. Without `--tools` the set is omitted and `contents.deploymentSet`
is `false`. With the set present, `verify` additionally requires that:

- Cosign accepts `offline-deployment-set.sigstore.json` for the same channel
  identity, and the document is **byte-for-byte** the document regenerated from
  its own carried templates and tool pins for the manifest's release identity,
  so an edited, reordered, re-signed or wrong-release copy is rejected.
- Every topology image is a release-selected application image or signed
  infrastructure pin, the topologies together cover exactly the carried image
  set, and every image tool comes from a pinned infrastructure image.
- The bundle carries exactly the pinned tool members, no missing or extra tool,
  each matching its signed size and SHA-256.

The verification record's `deploymentSet` then names the document SHA-256, the
configuration schema version, the topologies and each tool's SHA-256; it is
`false` when the bundle carries no set.

## Recovery instructions and host-local import (#3063)

Every release publishes `offline-recovery-instructions.json` and its
`offline-recovery-instructions.sigstore.json` bundle, signed with the same
workflow identity as the manifest. The document is generated from the release
identity alone (tag, version, channel, source branch and commit, build ID and
sequence) and names only four fixed wrapper operations for that one release,
each with its exact Bash and PowerShell argument vector: `offline-bundle-import`,
`host-update-status`, `host-update-recover-preview` and
`host-update-recover-confirm`. Host paths and the operator are placeholders such
as `<bundle.tar>`; there is no shell text, URL, credential or caller-chosen
command. It states `rolloutAuthorization: false`.

`assemble` packages the pair automatically when both files are present in the
release asset directory, and rejects one without the other. `verify` requires
Cosign to accept the signature bundle for the channel's release workflow
identity and the document to be **byte-for-byte** the document regenerated from
the signed manifest identity, so a re-signed, edited, wrong-release or
unflagged copy is rejected. The verification record then names the
instructions' SHA-256 and operation IDs.

Import on the network-denied host with the one documented command for each
platform (the paths must be absolute; `--staging` must not exist yet and
`--records` must be an existing directory kept outside replaced containers and
restored application databases):

```bash
scripts/printfarmer-host-update.sh import --config /etc/printfarmer/host-update.json \
  --bundle /srv/offline/printfarmer-offline.tar --channel stable --version 1.2.3 \
  --trusted-root /srv/offline/trusted_root.json \
  --trusted-root-approval /srv/offline/trusted-root-approval.json --staging /srv/offline/staging-1 \
  --records /var/lib/printfarmer/offline-decisions --operator ops.alice \
  [--prior-recovery-set /abs/dir] [--protected-backup /abs/reference.json]
```

```powershell
pwsh -File scripts\printfarmer-host-update.ps1 import -Config D:\PrintFarmer\host-update.json `
  -Bundle D:\offline\printfarmer-offline.tar -Channel stable -Version 1.2.3 `
  -TrustedRoot D:\offline\trusted_root.json `
  -TrustedRootApproval D:\offline\trusted-root-approval.json -Staging D:\offline\staging-1 `
  -Records D:\PrintFarmer\offline-decisions -Operator ops.alice `
  [-PriorRecoverySet D:\abs\dir] [-ProtectedBackup D:\abs\reference.json]
```

Both wrappers accept exactly the same options, validate them the same way
(absolute paths, `stable`/`insider`, `[0-9A-Za-z.+-]{1,128}` versions and
`[A-Za-z0-9][A-Za-z0-9._@-]{0,63}` operators), refuse a usage error with exit 2
before anything runs, and run `node offline-update-bundle.mjs import` with an
identical argument vector; the wrapper tests assert that parity. Both resolve the
host-update CLI exactly as for `status`/`recover` (`PRINTFARMER_HOST_UPDATE_CLI_DIR`,
or `cli/` beside an installed package's wrapper) and pass it with `--config` to the
tool, which runs `offline-admit`. `import` needs Node.js, a pinned Cosign and Docker
Engine 25 or later on the host. Set
`PRINTFARMER_NODE`, `PRINTFARMER_COSIGN` and `PRINTFARMER_DOCKER` to absolute
executables to avoid `PATH` lookup. In a repository checkout the tool is
`scripts/ci/offline-update-bundle.mjs`; an installed CLI package does not carry
it, so point `PRINTFARMER_OFFLINE_BUNDLE_TOOL` at an approved copy.

`import` first applies the trust expiry policy, then runs `verify` into the new
staging directory, then requires a complete bundle: the release-selected application images, the signed infrastructure
image list with its images, the signed recovery instructions, and the signed
deployment set with every approved tool it pins. A bundle that
verifies but lacks any of them is **not installable for import** and is
refused. It then asks the host-update CLI to admit the verified release into the
durable replay store (below) and only then runs `load`, which re-authenticates the staged metadata and
loads only verified archives. It exits 0 when imported and 1 when refused. A
refusal after verification succeeded removes the staging directory.

Before the first `docker load`, `import` publishes a durable record with
outcome `in-progress`, so evidence exists before any engine side effect. The
final `imported` or `refused` record then atomically replaces it. If `import`
is interrupted or finalization fails, the `in-progress` record remains: treat
it as "images may have been loaded" and re-run `import`. A refusal during
`docker load` may leave the verified, content-addressed images loaded before
the failure in the engine; they are inert, never tagged as active or started,
and the record lists them in `loadedImages` and names the failing member in
`failedLoad`.

Every decision, including every refusal, is written as one record
`<records>/<decidedAt>-<decisionId>.json`: created exclusively (POSIX mode
`0600`), fsynced, then renamed into place (and the directory fsynced on POSIX),
so a leftover `.<name>.partial` file is never a decision. A record holds the decision
ID and time, operator, outcome (`imported`, `refused` or `in-progress`), a bounded reason,
the expected channel and version, the bundle SHA-256, the signed release
identity, the verified manifest, image, recovery-instruction and prior-set
digests, the loaded image digests, the `trust` evaluation (trusted-root SHA-256,
approval time, approver and approval expiry), the `replay` decision
(`disposition`, `correlationId`, `reused`, `sequence` and `admitted`, also kept for a
refused admission), `installable` and `rolloutAuthorization: false`. `installable` is
`true` only on an `imported` record whose replay admission succeeded. Reasons are redacted: supplied paths are replaced
by placeholders such as `<bundle>`, any other host path by `<path>`, control
characters are removed and the text is capped at 512 characters. Usage errors
(a malformed operator, version or channel, or a missing or linked records
directory) are rejected before a record can be written.

An imported record is evidence that the bytes were verified and loaded. It is
not an update offer, an installation or channel consent; applying a release
still follows the [operator runbook](HOST_UPDATE_RUNBOOK.md).

## Replay admission, channel continuity and trust expiry (#3064)

### Trust expiry policy

A network-denied host cannot refresh the Sigstore trusted root, so `import`
trusts the operator's `trusted_root.json` only while both hold:

- An operator approval record (`--trusted-root-approval`) binds its exact bytes
  and is younger than **90 days**. The record is exactly
  `{"schema":1,"kind":"printfarmer-trusted-root-approval","trustedRootSha256":"<64 hex>","approvedAt":"<canonical UTC ISO-8601>","approvedBy":"<operator>"}`.
  Unknown fields, a SHA-256 of different bytes, a non-canonical timestamp, an
  approval dated more than 10 minutes in the future or one older than 90 days is
  refused. Re-approve a current trusted root from a connected, trusted host to
  continue.
- The root still lists at least one certificate authority and one
  transparency-log key whose `validFor` window covers the current time.

The policy is fixed in `offlineTrustPolicy`; there is no override option. It is
evaluated before anything is extracted, and the result is kept in the record.

**Revocation** is expressed only through those validity windows and the replay
store's rejected/superseded identities. There is no offline revocation list:
a key compromised after approval stays accepted until its window ends or the
approval expires (at most 90 days). The age of the manifest signature itself
(its Rekor integration time) is not bounded; rollback to an older signed
release is prevented by the replay high-water mark instead. Both are
documented residuals.

### Replay admission and channel continuity

After a complete bundle verifies, `import` runs
`Farm.HostUpdate.Cli offline-admit --staging <dir> --channel <c> --trusted-root <abs> [--cosign <abs>] --json`
with the host's `--config`, passing the same absolute trusted root and Cosign
executable used for verification. The CLI (see the
[runbook](HOST_UPDATE_RUNBOOK.md#offline-replay-admission)) requires host state to
be enabled, holds the host-update execution lock, re-parses and validates the
staged manifest, checks that its digest, channel and release identity match the
verification record, and that the manifest channel equals both `--channel` and
the host's durable automation policy channel. Because the staging directory and
its verification record are mutable, neither is trusted for authenticity: the CLI
re-verifies the exact staged manifest bytes and `update-manifest.sigstore.json`
with Cosign against `--trusted-root` (a regular, non-link file whose physical
location, with every linked or junctioned parent resolved, lies outside staging)
and the channel's pinned release identity, and refuses on failure. It then records the release in the
same durable replay store (trust root `default`, per-channel high-water mark,
hash-chained anchor) that online updates use, with a new `Imported`
disposition:

- A new identity above the channel high-water mark is recorded `Imported`,
  supersedes the previous high-water identity and advances the mark.
- The identical identity is reused (idempotent re-import of an `Imported` or
  `Accepted` release).
- A lower sequence (downgrade/replay), an equal sequence with a different
  identity (substitution) or a rejected/superseded identity is persisted as
  `Rejected` and refused, so reimported sequence 41 stays rejected after 42,
  after restart and after channel round trips.
- A channel that differs from the manifest or the policy is refused without
  touching replay state; each channel keeps an independent high-water mark.

An `Imported` identity never authorizes installation by itself: the online
scheduler still applies every current gate and admits it normally. Replay state
lives in the host-state directory, outside restored application databases and
replaced containers; missing or tampered anchor state fails closed (exit 4).
`offline-admit` refuses while another host-update process holds the execution
lock (exit 7). Every replay decision, from the API scheduler or the CLI, also
takes an exclusive cross-process lock on `host-update-replay.lock` in the
host-state directory, so concurrent writers cannot interleave or delete each
other's staged state; if it cannot be acquired within 30 seconds the decision
fails closed with `host_update_replay_lock_unavailable` (exit 4). Refused admissions are
recorded with their disposition and the staging directory is removed.

### Retention

Neither `import` nor the CLI ever deletes decision records, replay state or its
anchor journal; keep them for the life of the installation. Staging is removed
on refusal. Keep each imported bundle and its approval record while the release
is installed or is the prior recovery release.

## Import and continuity rules

Import must be bounded before extraction or allocation: reject traversal,
absolute paths, links/reparse escapes, duplicate/conflicting entries, oversized
or incomplete archives and expansion bombs. A failure must leave no
success-shaped import or partially activated set. The importer must fail
closed on untrusted, expired, revoked, wrong-platform or mixed-channel evidence.
The approved offline trust expiry/revocation and retention policy is the fixed
policy in [the #3064 section](#replay-admission-channel-continuity-and-trust-expiry-3064);
an operator cannot relax it.

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
The host-local CLI is delivered (#2980) and gates writer fence release on a
recorded physical printer reconciliation (#2999). Provider and topology stop
conditions are covered with fake adapters (#3000); see
[the runbook](HOST_UPDATE_RUNBOOK.md#provider-and-topology-stop-conditions).

Complete bundles are tracked in #2981. #2982 owns this matrix and separately
authorized staging/pilot evidence. #2664 remains open until its full retained
acceptance is complete.
