# History

## Core Context

PrintFarmer is a .NET 10 and React 19 application for 3D-printer farm management. The current work is requested by jpapiez.

## Learnings

- Model thumbnail replacement uses immutable, uniquely named PNG candidates. The
  database metadata pointer changes only after shared #840 PNG validation and an
  atomic same-volume move, so failed or concurrent requests delete only their
  own candidate and cannot overwrite a winning request.
- `Model3D.UpdatedAt` is an application-managed concurrency token alongside
  provider-native `RowVersion`, giving PostgreSQL, SQL Server, and SQLite a
  consistent ETag fallback.
- Split nginx already routes `/api/3d-models/` to `slicer-host` for HTTP and
  HTTPS; #842 adds regression assertions rather than changing generated proxy
  copies.
- Merging corrected #841 into #842 required additive resolution in the upload
  controller and model DTO mapping: retain parent attribution/logging while
  preserving #842 ETags and thumbnail replacement semantics.
- The #842 PostgreSQL and SQL Server slicer migrations remain correctly ordered
  after the corrected parent; both provider drift checks report no pending
  model changes.
- PR #856 released against exact stabilized #854 parent
  `96c9df199482535500f24fa3692e75e789190561` with no conflicts. That parent
  contains development commit `9407a7dff7b95550a65b4508649a174f2fdbbf1b`
  and #850 commit `2d0225ee4fcbf4172551c0db4013dcdd3be88201`.
- Final pre-commit validation passed format verification, a strict full build
  with zero warnings/errors, all four EF migration drift checks, 3,325 .NET
  tests, React lint/build and 2,615 tests, plus direct HTTP/HTTPS slicer routing
  assertions. Deployment harness execution is environment-blocked by Windows
  CRLF checkout of unchanged `container-versions.conf` and unavailable Docker
  Compose; the focused MySQL probe reproduces the unchanged unsupported-provider
  error from the exact parent.
