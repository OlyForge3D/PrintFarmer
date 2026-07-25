# Third-Party Notices

PrintFarmer is licensed under `AGPL-3.0-only`. Third-party components remain
under their respective licenses. This file records directly bundled component
and asset classes; each release also publishes an SPDX JSON SBOM with the
complete resolved dependency inventory, versions, package identities, licenses,
and available digests.

## Bundled slicers and profiles

### OrcaSlicer

- Project: [OrcaSlicer](https://github.com/OrcaSlicer/OrcaSlicer)
- Distributed form: upstream AppImage contents, executable, and profile
  resources used by the OrcaSlicer worker
- License: GNU Affero General Public License v3.0
- Upstream license:
  [LICENSE.txt](https://github.com/OrcaSlicer/OrcaSlicer/blob/main/LICENSE.txt)

PrintFarmer does not relicense OrcaSlicer. Upstream license, copyright, and
modification notices shipped inside the AppImage or source tree must remain
intact. The worker integration and deployment wrappers are PrintFarmer code.

### PrusaSlicer

- Project: [PrusaSlicer](https://github.com/prusa3d/PrusaSlicer)
- Distributed form: optional upstream slicer executable and resources used by
  the PrusaSlicer worker
- License: GNU Affero General Public License v3.0
- Upstream license:
  [LICENSE](https://github.com/prusa3d/PrusaSlicer/blob/master/LICENSE)

PrintFarmer does not relicense PrusaSlicer. Upstream notices shipped with the
binary or source distribution must remain intact.

## Icons and user-interface assets

| Component | Distributed form | License | Upstream |
|---|---|---|---|
| Material Design Icons (`@mdi/js`) | SVG path data compiled into the web client | Apache-2.0 | [MaterialDesign-JS](https://github.com/Templarian/MaterialDesign-JS) |
| Heroicons (`@heroicons/react`) | SVG React components compiled into the web client | MIT | [Heroicons](https://github.com/tailwindlabs/heroicons) |
| Lucide (`lucide-react`) | SVG React components compiled into the web client | ISC | [Lucide](https://github.com/lucide-icons/lucide) |

Copyright and license files included by these packages are preserved in the
package manager cache and identified in the release SBOM. New fonts, icons,
screenshots, models, fixtures, or generated assets require an explicit source
and license review before distribution.

## Frameworks, libraries, and base images

Release SBOMs enumerate the exact NuGet, npm, operating-system, and container
packages shipped in each artifact. The image workflow combines a
revision-bound inventory of restored production NuGet packages with a Syft
scan of the pushed image, then validates the enriched SPDX document before
publication. This includes:

- ASP.NET Core, .NET runtime, and NuGet libraries;
- React, JavaScript runtime dependencies, and build tooling;
- Ubuntu, Debian/Alpine, Nginx, Node.js, PostgreSQL, and Microsoft .NET base
  image packages;
- native libraries and bundled binaries used for 3D model and slicer support.

The SBOM is the release-specific identity record. It does not replace license
texts or attribution files supplied by a component. Container files install
PrintFarmer's `LICENSE` and this notice under
`/usr/share/licenses/printfarmer/`.

NuGet assemblies and native files are mapped only to exact restored package
evidence. Distribution packages retain their package-manager identity and
separate terms; custom distribution `LicenseRef` values require extracted text
or an exact copyright path retained in the image. Unmatched, ambiguous,
unknown-license, or cross-revision records block release rather than inheriting
a guessed license.

### Reviewed conditional and separate terms

- QuestPDF 2026.2.3: its bundled license permits open-source projects to use
  the Community MIT License. PrintFarmer selects that MIT option.
- Six Labors Fonts 2.1.3, ImageSharp 3.1.12, and ImageSharp.Drawing 2.1.7:
  their bundled Six Labors Split License 1.0 grants Apache-2.0 when the work is
  consumed in software under an Open Source or Source Available license.
  PrintFarmer selects that Apache-2.0 option.
- Microsoft.Data.SqlClient.SNI.runtime 6.0.2: separately licensed Microsoft
  Distributable Code. Its bundled terms permit redistribution as part of an
  application. PrintFarmer does not relicense this component under the AGPL.
- `scripts/dotnet-install.sh` and `src/dotnet-install.sh`: vendored .NET
  Foundation installer scripts under the MIT License. Their preserved terms are
  in `compliance/licenses/dotnet-install-MIT.txt`.
- SonarAnalyzer.CSharp 10.20.0.135146: a private build-time analyzer under the
  Sonar Source-Available License 1.0. It is not included in distributed
  application artifacts.

Exact reviewed license-file hashes and renewal dates are recorded in
`compliance/dependency-license-policy.json`. These records document the
repository decision and source evidence; they are not external legal approval.

## Reviewed metadata exceptions

`compliance/dependency-license-policy.json` records narrow, dated reviews for
packages whose registry metadata is missing or non-SPDX while their immutable
published package contains compatible license evidence. Unknown licenses are
not inferred. Exceptions require a named reviewer, evidence, rationale, and
review date, and cannot approve an incompatible license.

## Calibration resources and exclusions

No source-derived Printer Calibration file or third-party calibration asset is
approved merely because it appears in an upstream repository. Approved code
must be listed in `compliance/calibration-provenance.json` with immutable
blob-level evidence. Static printer catalogs, calibration models, screenshots,
icons, fixtures, data tables, unpinned revisions, and unknown-license assets
are excluded.

## Updating notices

Dependency, base-image, slicer, or distributed-asset changes must update this
file when they introduce a new notice class and must update the dependency
policy or provenance manifest in the same change. Release validation fails on
unknown or unreviewed license metadata.
