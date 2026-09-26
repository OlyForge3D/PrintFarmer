---
post_title: "Offline update and recovery delivery requirements"
author1: "Parker"
post_slug: "offline-update-recovery"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["deployment", "offline", "recovery"]
ai_note: "AI-assisted delivery contract; offline activation requires prior verified import evidence."
summary: "Document verified offline import, activation and recovery requirements for network-denied hosts."
post_date: "2026-09-24"
---

## Current support

**Complete verified network-denied update and recovery bundles are delivered**
(#2981). A complete bundle carries the original signed release bytes, every
release-selected application and infrastructure image, the signed deployment
set with its approved tools, the signed recovery instructions and, for
recovery, the prior recovery set with its images and protected-backup
reference. The host-local [import](#recovery-instructions-and-host-local-import-3063),
[activation](#offline-activation-3080) and
[recovery](#offline-recovery-3082) commands verify all of it offline and fail
closed. The isolated host-update acceptance matrix and any owner-authorized
staging or pilot rollout remain separate acceptance under #2982 and are not
inferred from the unit and integration fixtures below.

`deploy-docker.sh --prepare-offline` still only prepares legacy deployment
materials and image caches. It is not a signed complete release, a bounded
trusted importer, or proof of coordinated restore. Do not use it to update an
installation that needs #2664's recovery guarantees. The signed host-local
status/recovery CLI package (#2980, #3041) is a separate release asset. It can
be carried to a disconnected host as described in the
[runbook](HOST_UPDATE_RUNBOOK.md#install-the-signed-cli-package), but it is only
one item in the bundle below. There is no supported skip-verification,
force-import or replay-reset option.

The first delivered slice (#2981) was a
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
It authorizes no rollout by itself; activation is a separate operator step that
reuses the same signed bytes, replay evidence and shared host-update executor.

Connected installations need a proven minimum host-local recovery path but do
not need to hand-carry a bundle. Disconnected installations additionally need
all the material and evidence below. See the
[operator runbook](HOST_UPDATE_RUNBOOK.md) for authorization and stop conditions.
This contract does not invent a second manifest format or release publisher:
the [release guide](RELEASE_GUIDE.md) owns original signed release identity.

## Required bundle contents

A complete bundle accounts for every item below; the sections that follow
describe how each is carried and verified. This table is not an archive layout.

| Material | Required checks |
| --- | --- |
| Original manifest and signature bundle | Preserve exact signed bytes and canonical source/tag/version/channel/build identity, manifest digest, promotion/branch authorization where required by the canonical contract, and all selected index/platform digests. No mutable-only identities or same-version byte substitutions. |
| Offline verification evidence and tooling | Preserve provenance and approved trust-root continuity, expiry/revocation evidence and pinned verification tools. Bundle-supplied signer material cannot enroll itself. Verification must work after source branch movement without live ancestry lookup. |
| Target application and infrastructure images | Include every selected platform/service image, database/runtime/proxy/add-on dependency and required worker under the supported topology contract. Six published application images alone are not every installation's infrastructure. Verify archive content against immutable identity; no missing-image downloads or builds. |
| Prior recovery set | Retain complete compatible prior manifests/images and effective configuration, schema/format compatibility and backup references. Prior-channel artifacts are recovery-only under explicit verified authorization, not new offers or implicit channel consent. The bundle carries the signed prior manifest and its images (#3062, #3094); effective configuration is installation-specific and is retained in the engine's activation-time backup, never in the redistributable bundle (see [offline recovery](#offline-recovery-3082)). |
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

A bundle with a prior set may also carry the prior release's application images
(#3094):

```bash
node scripts/ci/offline-update-bundle.mjs assemble ... \
  --prior-release-assets ./prior-release-assets \
  --protected-backup ./protected-backup.json \
  --prior-images ./prior-oci-layout
```

`--prior-images` requires a prior set. The authenticated prior manifest alone
decides the required set: one `prior-image-<service>.oci.tar` member per prior
application service, each bound by digest and platform exactly like the
[target images](#application-and-infrastructure-images-3061), and every service
in the target manifest must exist in the prior manifest. `contents.priorImages`
is `true` only when every required prior image is present, and it implies
`contents.priorRecoverySet`; bundles assembled before #3094 have no claim and
are treated as `false`. `verify` checks the prior images only after the prior set
authenticates, and rejects a missing, tampered, wrong-platform, extra or mixed
prior image before anything is loaded. On success the record's `priorImages`
lists the verified prior images, or is `false`.

`load-prior` hands those images to the local engine without network access:

```bash
node scripts/ci/offline-update-bundle.mjs load-prior --staging /srv/offline/staging-1 \
  --channel stable --trusted-root /srv/offline/trusted_root.json \
  [--missing refuse|skip] [--cosign /abs/cosign] [--docker /abs/docker]
```

Like `load`, it never trusts the mutable record for expectations: it
re-authenticates the staged target and prior manifests, derives the required
prior images from the prior manifest, requires the record to name exactly that
set, and re-hashes and verifies every archive before loading any of them.
`--missing skip` returns `no_packaged_prior_images` without loading only when the
record claims no prior images and no prior image archive is staged; any other
shape is refused. The prior set deliberately does not carry effective
configuration: it is installation-specific, so it is retained by the engine's
activation-time backup and restored by coordinated recovery (#3082), never
shipped in the redistributable bundle. A bound
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
rejected. Bundles assembled before #3081 have no `deploymentSet` claim; they still
verify and are treated as carrying no deployment set, so import refuses them as
incomplete. Delivered slices under #2658:

- #3061 (delivered): application and infrastructure image archives.
- #3062 (delivered): prior recovery set and protected-backup references.
- #3063 (delivered): host-local import with Bash/PowerShell parity and bound
  recovery instructions.
- #3064 (delivered): replay protection, channel continuity and offline trust
  expiry.
- #3081 (delivered): complete offline set — deployment templates, configuration
  schema and approved tools bound to the signed release.
- #3080 (delivered): offline activation with Bash/PowerShell wrapper parity.
  Activation re-verifies the imported release, proves preloaded images locally and
  executes the existing host-update engine without registry or build fallback.
- #3082 (delivered): network-denied recovery to the prior artifact set with
  provider and remote-owner requirements failing closed (see
  [offline recovery](#offline-recovery-3082)).
- #3094 (delivered): packaged prior recovery images and fail-closed handling of
  remote pinned workers.
- #2981 (delivered): complete-bundle acceptance audit and schema 2 signed
  recovery instructions covering prior-bound import, activation and
  network-denied recovery. The isolated staging matrix and owner rollout
  evidence remain with #2982.

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
sequence) and names only fixed wrapper operations for that one release, each
with its exact Bash and PowerShell argument vector. Schema 1 names four:
`offline-bundle-import`, `host-update-status`, `host-update-recover-preview` and
`host-update-recover-confirm`. Schema 2 (#2981, the current default) adds the
network-denied path end to end: `offline-bundle-import-with-prior`,
`offline-bundle-import-with-local-prior`, `offline-activate`,
`offline-recover-preview` and `offline-recover-confirm`. Published schema 1
documents still verify. Host paths and the operator are placeholders such
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

## Offline activation (#3080)

Activate a previously imported release only after the host is prepared for a
managed update and the API service is stopped. The wrappers expose the same
fixed operation on both platforms:

```bash
scripts/printfarmer-host-update.sh activate --config /etc/printfarmer/host-update.json \
  --staging /srv/offline/staging-1 --channel stable \
  --trusted-root /srv/offline/trusted_root.json [--cosign /abs/cosign] [--json]
```

```powershell
pwsh -File scripts\printfarmer-host-update.ps1 activate -Config D:\PrintFarmer\host-update.json `
  -Staging D:\offline\staging-1 -Channel stable `
  -TrustedRoot D:\offline\trusted_root.json [-Cosign D:\abs\cosign.exe] [-Json]
```

`activate` invokes `Farm.HostUpdate.Cli offline-activate` with the same absolute
argument vector and preserves the CLI exit code. Usage/setup errors return 2;
refusals return 6; a held host-update execution lock returns 7; unreadable
configuration or state returns the existing configuration/state codes. There is
no registry fallback, no local build and no verification bypass option.

Activation re-parses the staged `update-manifest.json`, re-verifies
`update-manifest.sigstore.json` with the supplied trusted root and requires the
manifest channel to equal both `--channel` and the standing policy channel. It
then performs a read-only check of the durable replay store for the exact
imported release identity (release ID, sequence and manifest digest). Only a
strict `Imported` disposition is accepted. Missing evidence, an already accepted
or completed activation, a rejected, superseded or replayed identity, or
evidence from another channel fails closed before images or compose state are
touched. A successful activation records the imported evidence as consumed, so a
second activation requires a fresh import/admission.

Before any mutation, activation constructs the configured topology from the
signed manifest and verifies every required image locally by exact
`repository@sha256:<digest>` and platform (`os/architecture[/variant]`). Missing
images, wrong platforms, incomplete platform digest sets or mixed-release
digests are refused before `docker compose` runs. The apply and target-image
migration steps use preloaded image mode: they inspect local images, run
migrations with `docker run --pull never`, and apply templates with
`docker compose up -d --no-build --pull never`; they never run `docker pull`.

Execution still goes through the existing `HostUpdateExecutor` state machine
(preflight, drain, fence, backup, migration, apply, verify), journal and
installed-state writer. The CLI wires the same concrete adapters used by the API
for backup, target-image migrations, apply, health/digest verification and
recovery semantics. To avoid a false in-process writer fence, the CLI proves
every active database-writing compose service is inactive: monolith topologies
check `monolith`, split topologies check `api` and `slicer-host`, and any other
active writer service listed by configuration must have a known compose service
mapping. The proof uses all container states (`docker compose ps -a --format
json`) and allows only absent, exited, dead, removed or not-created services; a
running, restarting, paused, created/starting, unknown or unparseable state fails
closed. Activation repeats the replay and writer-absence checks inside the
executor's own lock immediately before executor steps begin, closing the gap
between preflight validation and mutation. After health/digest verification
persists the installed state, the executor consumes the `Imported` replay record
as `Accepted` before releasing that same lock. A crash in that finalization
window is recoverable in either order: a later `activate` for the same release
and manifest digest finalizes a completed journal whose replay record is still
`Imported`, or finalizes the completion journal when replay is already `Accepted`
and the request-bound journal proves `verify:after`. The completed-journal path
requires both `verify:after` and `completed` records to carry the exact binding
for the current preloaded activation request; registry-mode, legacy-unbound, or
otherwise mismatched journals are refused and left unchanged. Both paths
additionally require the installed state to already match the signed target, and
neither reruns migration or apply steps. With writers stopped, there are no live
API/slicer/monolith in-memory writer flags to prove, while the durable admission
gate, database active-work checks, backups, migrations, health gates and
installed-state records remain the single engine source of truth. Failures before
verification preserve the prior installed state and leave recovery to the
existing journal/recovery workflow; once the installed state has changed, the CLI
reports the completed activation state rather than a refused activation.

## Offline recovery (#3082)

When an offline activation fails after mutation and the journal reports
`RecoveryRequired`, recover to the prior release bound in the same staged bundle
with `recover-offline`. Preview first, then confirm by retyping the release ID:

```bash
scripts/printfarmer-host-update.sh recover-offline --config /etc/printfarmer/host-update.json \
  --staging /srv/offline/staging-1 --channel stable \
  --trusted-root /srv/offline/trusted_root.json \
  --protected-backup /srv/offline/protected-backup.json \
  --release stable:1.4.0 --preview [--json]
scripts/printfarmer-host-update.sh recover-offline ... --release stable:1.4.0 \
  --confirm stable:1.4.0 [--reapprove-drift <token>] [--printers-reconciled <token>]
```

```powershell
pwsh -File scripts\printfarmer-host-update.ps1 recover-offline -Config D:\PrintFarmer\host-update.json `
  -Staging D:\offline\staging-1 -Channel stable -TrustedRoot D:\offline\trusted_root.json `
  -ProtectedBackup D:\offline\protected-backup.json -Release stable:1.4.0 -Preview [-Json]
```

Both wrappers validate the same fixed options, forward them in one canonical
order to `Farm.HostUpdate.Cli offline-recover` and preserve the CLI exit code;
`--release` names the failed target, and `--request-id`, `--reapprove-drift` and
`--printers-reconciled` behave exactly as for `recover`. Plain `recover` is
unchanged.

The staging directory and its `offline-bundle-verification.json` record are
mutable and are never trusted on their own. Before reading host state the command
requires, in order (every refusal exits 6 and changes nothing):

1. The staged target manifest re-parses and its signature re-verifies against
   `--trusted-root` for `--channel`, and its release ID equals `--release`
   (`offline_recovery_release_mismatch`).
2. The record binds a prior set (`prior_recovery_set_missing` when it is
   `false`; `prior_recovery_set_invalid` when malformed).
3. `prior-update-manifest.json` hashes to the recorded manifest digest, validates,
   matches the recorded channel and version, is on the same channel as the
   target with a strictly lower sequence, and matches the recorded backup's
   `releaseVersion` (`prior_recovery_set_mismatch`).
4. `prior-update-manifest.sigstore.json` verifies the prior manifest bytes
   against the same trusted root and channel identity
   (`prior_recovery_set_unverified`).
5. The operator's `--protected-backup` reference — a regular file of at most
   64 KiB with exactly `id`, `sha256`, `locationClass` and `releaseVersion` — is
   well formed (`protected_backup_invalid`) and equal to the recorded reference
   (`protected_backup_mismatch`). The operator's own copy, not the unauthenticated
   record, is the authority for the reference. The reference is an operator-held
   precondition only: the engine never contacts, reads or restores the protected
   backup (whatever its `locationClass`). A coordinated restore uses only the
   engine's own activation-time backup manifest under host state, whose per-file
   checksums are re-verified before restore.
6. The reference's `locationClass` is `host-local`. An `attached-volume` or
   `external-storage` backup belongs to an owner this host has no configured,
   authenticated provider for, so recovery refuses with
   `protected_backup_owner_required` (detail `restore_through_backup_owner`)
   before reading host state and without contacting that owner; restore through
   the backup's owner instead.

Recovery then runs through the ordinary recovery resolver, drift and physical
reconciliation gates, lease and approval-bound installed-state snapshot. The
gate below is evaluated on that locked snapshot, before planning or confirming:
the failed request must be the
staged target (`offline_recovery_target_mismatch`) in preloaded-image mode
(`offline_recovery_requires_preloaded_request`; a registry-mode request would
re-apply the prior set by pulling), and the installed state must be exactly the
authenticated prior set: its release ID and manifest digest, and exactly the
target's service set with each service on the target's platform and at the prior
manifest's digest for that platform (`prior_installed_state_mismatch`). On
confirm, the coordinator re-proves under its own execution lock, before any
restore or apply, that both the installed state and the execution journal are
unchanged since evaluation; otherwise it exits 12 (`drift_reapproval_stale`).

The recovery engine applies the prior digests in preloaded mode: it inspects each
prior image locally, and applies templates with
`docker compose up -d --no-build --pull never`; it never runs `docker pull` or
`docker login`, and a missing local prior image fails closed. With `--confirm`,
both wrappers first run `offline-update-bundle.mjs load-prior --missing skip`
against the staging directory (resolving `PRINTFARMER_NODE`,
`PRINTFARMER_OFFLINE_BUNDLE_TOOL`, `PRINTFARMER_COSIGN` and `PRINTFARMER_DOCKER`
as for `import`), so packaged prior images are verified and loaded even when the
engine cache has pruned them; a load failure exits 6 before the CLI runs. A
bundle without packaged prior images still recovers from the engine cache, which
is verified by digest and platform as above; with neither, recovery fails closed.

Offline activation and offline recovery also refuse (exit 6) when any registered
slicer worker is outside this host's compose set: every registered worker host
must be an `http(s)` URL naming the compose service of an active mapped service.
A remote or out-of-compose worker refuses with `remote_worker_unsupported`
(detail `restore_remote_workers_through_owner`); unreadable registrations refuse
with `remote_worker_evidence_unavailable`. Restore or update remote workers
through their owner. A coordinated
database restore owned by an external provider (`DatabaseExternallyOwned`) stops
as needs-operator with `database_externally_owned` before any restore or apply,
so the external owner must restore it. Integration tests exercise a
real failed offline activation followed by preview and confirm with a
network-denied HTTP factory, asserting the prior set is restored through the
engine activation-time backup, with no non-loopback request and no pull.

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
replaced containers; missing or tampered anchor state fails closed (exit 4)
and the CLI reports the store's fixed `host_update_replay_*` code (for example
`host_update_replay_state_rollback`), never a path or free-form message.
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
evidence and optional/remote pinned-worker cases. Live cells use the Bash
entry point; the PowerShell entry point shares the same bundle contract but is
checked for documentation and links only, which does not prove Linux restore or
runtime parity (see the [matrix scope](#isolated-recovery-matrix-scope-3098)).

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

Unit-level replay evidence already exists; it does not replace the isolated
matrix above. `HostUpdateOfflineReplayTests` (replay store) covers sequence 41
rejected above an installed 40, 42 imported without installing, reused
decisions across stable-insider-stable round trips and a restart, a moved alias
or other-branch commit at the imported sequence, and fail-closed restore of an
older replay snapshot, an older anchor, an edited snapshot or a foreign
same-epoch snapshot. `HostUpdateCliOfflineAdmitTests` (`offline-admit`) covers a
forged source branch, a moved release alias, evidence staged under a prior
policy after a policy round trip, and a restored older replay state
(`host_update_replay_state_rollback`, exit 4). `scripts/ci/tests/test-offline-update-bundle.mjs`
covers traversal, absolute, nested, duplicate and case-conflicting members,
extended headers, links, and member/bundle/nested-blob size bounds.

No fallback network request, local build, fabricated recovery success or
physical printer command is acceptable. Retain full failure evidence, prove
fences stay closed on uncertainty, and confirm safe post-restore reconciliation.
The host-local CLI is delivered (#2980) and gates writer fence release on a
recorded physical printer reconciliation (#2999). Provider and topology stop
conditions are covered with fake adapters (#3000); see
[the runbook](HOST_UPDATE_RUNBOOK.md#provider-and-topology-stop-conditions).

Complete bundles are delivered in #2981. #2982 owns this matrix and separately
authorized staging/pilot evidence. #2664 remains open until its full retained
acceptance is complete.

### Isolated recovery matrix scope (#3098)

This section fixes the scope of the isolated matrix above. It is a contract
for the harness, not evidence that any cell has run.

**Supported host.** The matrix runs only on **Ubuntu LTS x64** (22.04, 24.04
or 26.04). Linux arm64 and Windows hosts are **unsupported** for host-update
recovery. Live cells run only through the Bash entry point; the PowerShell
entry point is documented and link-checked only, proves no Linux restore or
runtime parity, and cannot produce matrix evidence. SQLite is component-tested
only and is not a live matrix provider.

**Supported cells.** Monolith and split Compose topologies, each with
PostgreSQL and SQL Server, a shared application/slicer database, and database
and storage owned by the host. Workers are either managed on the host or
absent. A supported cell may expect anything other than `Activated` only after
a successful `fault-injected` checkpoint, never with a fail-closed reason, and
with a stable reason unless it expects `RolledBack`.

**Fail-closed cells.** These cells are run to prove the refusal, never to
prove recovery. The first matching row decides the expected result, and the
stable reason must match exactly.

| Cell | Expected outcome | Stable reason |
| --- | --- | --- |
| Split application/slicer databases | `Refused` | `split_database_not_supported` |
| Remote (off-host) pinned workers | `Refused` | `remote_worker_unsupported` |
| Externally owned database | `NeedsOperator` | `database_externally_owned` |
| Externally owned storage | `NeedsOperator` | `storage_externally_owned` |

**Network denial.** Every service runs on a Docker `--internal` network whose
only reachable peer is a default-deny egress sink that logs each attempt. The
evidence mechanism value is `docker-internal-network+default-deny-egress-sink`.
Any recorded outbound attempt fails the run, whatever its recovery outcome.

**Signing.** Matrix cells are signed only by a test-only trust root with an
isolated fixture identity (`signingRoot` `fixture`). The release workflow's
signing identity is never used, and fixture trust is never installed on a real
host. Separately, each run contains exactly one read-only verification of a
real published insider bundle (`signingRoot` `published-insider`), recorded as
its own `printfarmer-published-bundle-verification` record. It proves the
harness accepts production signatures; the record must show that nothing was
imported or activated and the host was not modified, and it carries no
recovery outcome.

**Evidence record.** Each cell emits one JSON record validated by
[`scripts/ci/recovery-matrix/evidence.mjs`](../scripts/ci/recovery-matrix/evidence.mjs)
(`kind` `printfarmer-recovery-matrix-evidence`, `schema` 1). The record carries
the run identity and harness commit, entry point, host distribution, version,
architecture and kernel, the cell, the source/target/prior release identities
(tag `v<version>`, version, `stable` or `insider` channel, 40-character source
commit, build and non-negative integer sequence), bundle SHA-256 and
signing root, tool versions, the network-denial mechanism and every attempt,
operation checkpoints, expected and actual outcome with reason, exit code and
journal phase, timings and the verdict. The validator rejects missing or
unexpected fields, malformed identities, unsupported hosts or entry points, a
non-fixture cell signing root, a wrong fail-closed expectation, a pass
with outbound attempts, a failed checkpoint or a mismatched outcome, and any
unredacted credential: URL user information (with or without a password), PEM
blocks, GitHub tokens, JWTs, secret assignments and secret-bearing field names.
`validateMatrixRun` validates a whole run: every record, one shared run
identity, no duplicated cell, at least one cell and exactly one published-bundle
verification.

**Cadence.** The matrix runs on `workflow_dispatch` and nightly. It is never
part of a release publication workflow and never targets a real deployment.

**Recovery objectives (proposed defaults, pending jpapiez agreement).** These
values are proposals for the harness to measure against. They are **not agreed
targets** and must not be quoted as commitments until the deployment owner
accepts them.

| Objective | Proposed default |
| --- | --- |
| RTO, image-only rollback | 10 minutes on the reference host |
| RTO, coordinated restore | 30 minutes on the reference host |
| RPO | The activation-time protected-backup consistency point; writers are fenced before backup, so no committed write after that point is expected |
| Evidence retention | CI evidence artifacts for 90 days, plus a retained summary comment on the tracking issue |
| Protected-backup retention | Until the next successful release activation (N-1) |
