# Licensing, Corresponding Source, and Provenance

This document is the operational policy for PrintFarmer licensing,
corresponding-source publication, third-party notices, dependency license
review, and source-derived calibration provenance.

## Licensing decision and boundary

The PrintFarmer repository owner approved GNU Affero General Public License
version 3.0 only (`AGPL-3.0-only`) for repository-owned PrintFarmer code
beginning with v0.2.3. The canonical terms are in [`LICENSE`](../LICENSE), and
the machine-readable decision record is
[`compliance/licensing-policy.json`](../compliance/licensing-policy.json).
This records an explicit repository-owner decision; it does not claim external
legal approval.

Releases through v0.2.2 retain the terms that applied to those revisions. Do
not replace historical release assets or tags with newly licensed content.
The in-repository mobile client is first-party PrintFarmer code and follows the
root license beginning at the same boundary.

Third-party components are not relicensed. Preserve upstream SPDX markers,
copyright statements, license files, and notices where repository policy
allows. See [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) and the
release-specific SPDX SBOMs.

## Network corresponding-source disclosure

Every deployed API exposes the unauthenticated endpoint:

```text
GET /api/system/source
```

For a release image, the response identifies:

- `version` and the full 40-character immutable Git `revision`;
- the `AGPL-3.0-only` license expression;
- the exact source tree and release source archive;
- the matching license and third-party notices;
- the release SPDX SBOM; and
- `sourceAvailable`, which is true only when an immutable revision is known.

Development builds with no valid revision return `sourceAvailable: false` and
do not invent versioned source links. Public source metadata accepts HTTPS
locations only and rejects credentials, loopback addresses, private IP
addresses, and internal-only hostnames. Archive and SBOM paths must contain the
exact release version or full revision as a complete path segment; query
strings, fragments, and mutable references such as `latest`, `main`, `master`,
`develop`, or `nightly` are rejected.

Source metadata fails closed. An explicitly configured blank or invalid
`SourceInfo:RepositoryUrl` does not fall back to the PrintFarmer upstream
repository. A custom repository does not inherit fabricated upstream archive
or SBOM links: operators must also configure their own public
`SourceInfo:SourceArchiveUrl` and `SourceInfo:SbomUrl`. Unsupported or omitted
custom artifact links are returned as unavailable.

Operators who modify or self-host PrintFarmer must publish the complete,
corresponding source for the exact version with which remote users interact.
Configure public links for the operator's repository and artifacts rather than
pointing users to an unrelated upstream revision.

## Container build metadata

All first-party Docker build paths accept these build arguments:

| Argument | Meaning |
|---|---|
| `BUILD_VERSION` | v-prefixed release version |
| `VCS_REF` | Full 40-character Git commit |
| `SOURCE_REPOSITORY` | Public HTTPS source repository |
| `SOURCE_ARCHIVE_URL` | Public exact-version source archive |
| `SBOM_URL` | Public matching SPDX JSON SBOM |

API and monolith images map these values to
`PFARM__SourceInfo__Version`, `PFARM__SourceInfo__Revision`,
`PFARM__SourceInfo__RepositoryUrl`, `PFARM__SourceInfo__SourceArchiveUrl`,
and `PFARM__SourceInfo__SbomUrl`. Published images also carry OCI license,
source, revision, and version labels. `LICENSE` and
`THIRD-PARTY-NOTICES.md` are installed under
`/usr/share/licenses/printfarmer/`.

Do not set a mutable branch, abbreviated SHA, internal URL, or credentialed URL
as corresponding-source metadata.

Source-built Compose templates forward the same values from the deployment
environment. Set all five variables before `docker compose build`; use the
full commit for `VCS_REF` and public, deployment-owned HTTPS URLs for source
and SBOM artifacts. Leaving custom source values blank is an intentional
fail-closed configuration, not permission to point users at upstream source
that does not correspond to the running modifications. Official exact-tag
image builds populate release archive and SBOM URLs; branch and manual builds
leave unpublished artifact URLs blank.

## Release source and SBOM procedure

The release workflows generate all legal and source artifacts from the exact
release tag. For a local dry run from the repository root:

```bash
node scripts/compliance/create-source-bundle.mjs \
  --revision v0.2.3 \
  --version v0.2.3 \
  --output release-artifacts
```

This command resolves the tag to a commit, rejects prohibited tracked paths,
checks required source paths, creates
`PrintFarmer-v0.2.3-source.tar.gz`, and writes
`PrintFarmer-v0.2.3-source.json` with the commit, URLs, and archive SHA-256.
It fails rather than publishing source from a mutable or missing revision.

The .NET build job creates a reviewed license inventory from the restored
production `project.assets.json` files and the production npm dependency graph.
Development-only and optional npm lock entries are excluded. The inventory is
bound to the full Git revision and release version, then passed as an artifact
to every image job.
After Syft scans a pushed image, the compliance enricher reconciles:

- first-party projects with the exact source revision and `AGPL-3.0-only`;
- managed and native NuGet files with exact restored package versions and
  reviewed license evidence;
- separately licensed .NET runtime packs and narrowly reviewed binary records;
- production npm packages for the frontend and monolith images; and
- operating-system records with the package manager identity and license
  evidence present in the image.

The inventory revision and version must match the image build. Components from
another restore graph, ambiguous native-file matches, and unknown packages
remain unresolved and block publication; the enricher does not infer a license
from a similar package name.

Release workflows use Syft to generate SPDX JSON for every published image.
The enriched monolith record is the canonical
`printfarmer-v0.2.3.spdx.json`; split images also receive image-specific
records. The compliance validator requires component identity, version,
supplier/source/package URL, a reviewed license expression, relationships,
and durable document metadata. Reviewed Debian, Ubuntu, or Alpine package
records remain under their own terms. A custom distribution `LicenseRef`
requires extracted text or an exact in-image copyright path under
`/usr/share/doc` or `/usr/share/licenses`. Unknown or unreviewed licenses block
release.
The frontend build also generates
`THIRD-PARTY-LICENSES.npm.txt` deterministically from installed production
package terms and exact package/version/license/hash-bound fallbacks. Frontend
and monolith images ship that file with the web assets; the frontend image also
installs it under `/usr/share/licenses/printfarmer/`.

Images are initially pushed only by immutable digest. The workflow validates
and uploads all five digest/SBOM pairs, attaches the enriched SPDX document as
a signed Cosign attestation, verifies signatures and attestations, and smoke
tests ARM64 digests. For an exact version tag, the same gate then creates or
verifies the GitHub release, publishes the exact source archive, source
manifest, notices, canonical and image-specific SBOMs, and digest records, and
confirms every asset is anonymously reachable. An existing asset must be
byte-for-byte identical; the workflow never silently replaces it. Only after
that source-first publication succeeds does the gate assign semantic version or
channel tags. Semantic version tags are emitted only by the matching exact-tag
workflow run. The release orchestrators subsequently finalize release notes but
do not republish compliance assets. BuildKit SBOM and provenance attestations
are retained in addition to the enriched SPDX attestation.

Each GitHub release must contain:

- the exact-commit source archive and source manifest;
- `LICENSE` and `THIRD-PARTY-NOTICES.md`;
- the canonical and image-specific SPDX JSON SBOMs; and
- existing digest and signature records where produced.

Source archives must not contain secret-bearing environment files, credentials,
private keys, database files, or other prohibited tracked paths. Reviewed
non-secret templates named `.env.example`, `.env.development`, or
`.env.template` may be included because they are required build/deployment
source. Repository-owned application, test, deployment, release, and
development metadata remains included. Release files are scanned for credential
patterns before publication. Synthetic scanner fixtures and security
documentation examples require an exact path, pattern, matched-value SHA-256,
and rationale in policy; changed or additional matches fail publication.

## Deployment and verification procedure

The same disclosure requirement applies to monolith, split-container, and
custom hosted deployments.

1. Deploy images built from one immutable commit with matching source
   arguments.
2. From a network location that requires no operator authentication, request
   `GET /api/system/source`.
3. Confirm `sourceAvailable` is true, `revision` is a full commit, and every
   returned link is publicly reachable.
4. Confirm the archive manifest revision matches the running image's
   `org.opencontainers.image.revision` label.
5. Confirm the source archive SHA-256 matches the source manifest and that the
   SBOM corresponds to the deployed image version.
6. Retain the verification result with deployment records.

For split deployments, the API endpoint provides the release-level record and
each image-specific SBOM remains available with the release. A reverse proxy
must not authenticate, suppress, or rewrite `/api/system/source`.

## Retention, correction, and rollback

Published source, manifests, notices, and SBOMs are immutable release assets.
Repository policy is to retain them indefinitely. At minimum, they must remain
available for the full period that matching images, binaries, downloads, or a
hosted network version are offered, and for at least three years after the last
matching binary distribution.

Do not silently replace an asset under an existing tag. If source or metadata
is missing or mismatched:

1. stop new deployments and distribution of the affected artifact;
2. restore the exact missing artifact when it can be verified from the tagged
   commit, otherwise mark the release affected;
3. roll hosted instances back to a release with verified disclosure or publish
   a corrected patch release from a new immutable tag; and
4. record the correction in release notes and re-run compliance validation.

A rollback must update the running image, revision label, source endpoint,
archive, and SBOM together. Never point an older running binary at the current
`main` branch or at a newer release's artifacts.

## Third-party notice and dependency review

Dependency changes must preserve package license evidence and pass:

```bash
node scripts/compliance/validate-compliance.mjs
```

The allowlist and narrow exceptions are in
[`compliance/dependency-license-policy.json`](../compliance/dependency-license-policy.json).
An exception requires the exact ecosystem, package and version, observed and
approved license expressions, immutable evidence, reviewer, review date,
expiry/review date, and rationale. An exception cannot approve an incompatible
license or use a mutable URL as evidence.

The two vendored .NET installer scripts retain their upstream MIT terms in
`compliance/licenses/dotnet-install-MIT.txt`; CI binds that preserved text to
its exact SHA-256. This is third-party license preservation, not a declaration
that first-party PrintFarmer code is MIT-licensed.

Update `THIRD-PARTY-NOTICES.md` when a dependency, base image, slicer, or asset
introduces a new notice class. The release SBOM is the exact-version component
inventory; it does not replace upstream attribution or license files.

Automated validation does not fetch contributor-selected external URLs with
privileged credentials. External identifiers are treated as evidence records
and must use approved public source policy.

## Source provenance and the Governed/ folder convention

Production source-derived content is governed by
[`compliance/calibration-provenance.json`](../compliance/calibration-provenance.json)
and its
[`schema`](../compliance/calibration-provenance.schema.json). The production
manifest starts empty and fail-closed because no unverified upstream content is
approved by this licensing change.

Governance is scoped to a single canonical folder convention,
`src/**/Governed/**`, rather than any directory merely *named* after a feature
area (e.g. `Calibration`). A file's location alone does not make it governed:
code that lives in a `Calibration` folder but is 100% self-authored is not
governed and needs no manifest entry. Only source-derived or upstream-ported
code belongs under a `Governed/` folder within its project area (for example
`src/orcaslicer-worker/Services/Governed/`), and only files under that
convention are checked against the manifest.

Before adding source-derived code, place the destination file under the
project area's `Governed/` folder and add a manifest record that includes:

- a permitted destination under a `Governed/` folder;
- an immutable upstream repository, commit, and blob identity;
- the destination file SHA-256;
- upstream license and notice evidence;
- modification and porting notes;
- reviewer and review date;
- validation coverage and test references; and
- the issue or pull-request reference authorizing the port.

The record's SPDX source license must also appear in
`allowedSourceLicenseExpressions`. Schema validation and the compatibility
allowlist are both enforced; a syntactically complete record with an
unapproved or incompatible license still fails.

Static printer catalogs, calibration models, screenshots, icons, fixtures,
data tables, mutable revisions, and unknown-license assets are excluded. Do not
use the manifest to claim provenance for generated or unverified assets.

## Contributor and CI commands

Restore .NET dependencies before the full dependency license check so NuGet
assets files exist:

```bash
cd src
dotnet restore ./farm-web.sln
cd ..
node --test scripts/compliance/compliance.test.mjs
node scripts/compliance/validate-compliance.mjs
node scripts/compliance/create-license-inventory.mjs \
  --output release-artifacts/license-inventory.json \
  --version "$(tr -d '[:space:]' < VERSION)" \
  --revision "$(git rev-parse HEAD)"
```

Image publication then runs `scripts/compliance/enrich-sbom.mjs` against the
Syft SPDX document and this exact inventory before any public tag is assigned.
Release workflows verify `VERSION`, .NET `VersionPrefix`, and mobile marketing
version before creating a tag, wait for the single exact-tag image run, and
refuse to create a GitHub release unless all five image digest/SBOM pairs are
present and signed.

CI runs these checks as blocking gates. The test suite includes negative
fixtures for mutable revisions, unmanifested or changed calibration files,
missing notices, invalid digests, excluded static data, unverified assets,
conflicting first-party metadata, incomplete or opaque SBOM components,
restore/image drift, and embedded credentials.

Policy, exception, notice, provenance, Docker metadata, package metadata, and
release workflow changes must be reviewed together. A green application build
does not override a failed compliance check.
