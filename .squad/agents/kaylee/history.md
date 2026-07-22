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
