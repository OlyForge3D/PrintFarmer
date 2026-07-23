# History

## Core Context

PrintFarmer is a .NET 10 and React 19 application for 3D-printer farm management. The current work is requested by jpapiez.

## Learnings

- Reviewed #842 (PR #856, branch jpapiez-issue-842-upload-replacement, stacked on
  #841). APPROVED. `PUT /api/3d-models/{id}/thumbnail` has controller `[Authorize]`
  + service owner/admin gate, optional `If-Match` (stale -> DbUpdateConcurrencyException
  -> HTTP 412), reuses the exact #840 `StageAndValidateClientThumbnailAsync` PNG path,
  writes a unique immutable candidate + `MoveFile(overwrite:false)`, deletes prior only
  after SaveChanges, and rolls back/cleans on every failure (validation, oversize,
  cancellation, move-fail, commit-fail, concurrent-loss).
- Nullable user-scoped composite unique index migrations exist for PostgreSQL (no
  filter — PG treats NULLs distinct) and SQL Server (`IS NOT NULL` filter). Snapshots
  match; `UpdatedAt` marked `IsConcurrencyToken()` as provider-neutral ETag fallback
  alongside `RowVersion`. Migration is named ...IdempotencyAndThumbnailConcurrency
  because #841's schema was deferred into this stacked layer (intentional per PR body).
- Deployment MySQL failure is pre-existing/unrelated: #842 does NOT touch
  scripts/, deploy/, compose-generator, or templates — only appends
  `test_model_thumbnail_replacement_routing` to tests/test-compose-generator.sh.
  The generator/templates are byte-identical to base #841, so a compose MySQL
  assertion cannot be a #842 regression (documented env/ruamel compose issue).
- Split nginx has exactly 2 `location /api/3d-models/` (HTTP+HTTPS) both to
  slicer_backend; monolith nginx-proxy.conf routes /api/. Routing test assertions
  are accurate. Non-blocking: PR #856 has no labels; commit trailer name is
  "Copilot App" (email correct).
