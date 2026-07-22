# Decisions

## Upload Thumbnail Replacement (#842)

- Store each replacement thumbnail under a unique immutable filename.
- Promote the validated temporary PNG before updating the model metadata pointer; delete the previous thumbnail only after the database commit.
- On validation, storage, cancellation, or commit failure, delete only the new candidate and retain the prior metadata and file.
- Use `RowVersion` for ETags where the provider generates it, with `UpdatedAt` as the provider-neutral concurrency token and ETag fallback.
- Treat `If-Match` as optional for compatibility; when supplied, stale values return HTTP 412. EF concurrency still protects overlapping writes.

**Status:** APPROVED (PR #856, commit `32659d2db`). Zoe verdict APPROVED after verifying endpoint auth, ETag, atomicity, rollback, cleanup, migrations, routing, and test matrix. All validations passing; pre-existing MySQL deployment failure unrelated.

