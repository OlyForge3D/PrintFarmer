# API Reference (Summary)

This document aggregates endpoint groups and key DTOs. For specialized domains see:
- [Printer File Catalog API](./PRINTER_FILE_CATALOG_API.md)

## Tags / Endpoint Groups
- Authentication
- Printers
- Catalog
- Filament Types
- G-code Library
- Job Queue
- Printer Capabilities
- Slicer
- Spoolman
- Diagnostics/Test (internal)

## Common DTOs (selected)

### PrinterDto
Represents a printer with optional live status fields.

### PagedResult<T>
Generic wrapper for paging: items, totalCount, page, pageSize, totalPages.

### AuthenticationResult
Successful password and passkey login results contain the access `token`, its
`expiresAt` timestamp, user metadata, and a `refreshToken` with
`refreshTokenExpires`. `POST /api/auth/refresh` accepts `{ "refreshToken": "..." }`
and returns a rotated token pair with a fresh 30-day sliding refresh-token expiry
and no absolute session cap. Refresh tokens are single-use; clients must persist
each replacement and must not reuse a token after rotation. Send the current
refresh token in the optional authenticated `POST /api/auth/logout` body to revoke
that session. A previously rotated token revokes its active descendant in the same
session family; unrelated sessions and other users' sessions are not affected.
Reusing a rotated token at the refresh endpoint revokes all of the user's refresh
sessions and access tokens issued at or before the revocation marker's second.
Access tokens issued in a later second remain valid. Successful password changes
and password resets revoke all refresh sessions, but do not revoke existing JWT
access tokens. The React client refreshes access tokens proactively and retries one
authenticated request after a 401; refresh tokens are stored separately from
access tokens in local storage.

### BarcodeScanLogDto
Admin-facing diagnostic entry for optional Spoolman barcode scan logging. Fields include
`id`, `timestamp`, `barcode`, `action`, `outcome`, `httpStatus`,
`matchedFilamentId`, `createdSpoolId`, `userId`, and `message`.

(Extend with additional DTO details as needed.)

## Printer Expected-Offline Monitoring

`PUT /api/printers/{id}/expected-offline` accepts a JSON boolean: `true`
acknowledges an already-offline printer; `false` restores normal monitoring.
It requires `printers:admin`, Submit access to the printer's group, and an
`If-Match` header containing the printer's current revision.

The response contains `id`, nullable `expectedOfflineSinceUtc`, and `rowVersion`,
with the updated revision also returned as an ETag. Missing preconditions return
428; a stale revision returns 412. Acknowledgement returns 409 when offline
status is unknown or the printer has active or assigned work.

Intent persists until a proven successful reconnect or explicit reversal.
Actual connectivity remains offline, and active-work or provider faults remain
actionable. This endpoint changes no power, maintenance, dispatch, or scheduling
policy.

## Spoolman Barcode Resolution

- `GET /api/spoolman/filaments/by-barcode?code=` resolves a scanned retail barcode
  to a filament. The scanned value is normalized to a canonical 14-digit GTIN
  (GTIN-8/12/13/14 are zero-pad equivalent) and matched against the filament
  `gtin` field only. A value that fails length or GS1 mod-10 check-digit
  validation is rejected without querying Spoolman.
- `articleNumber` is a vendor article number / SKU and is **never** consulted for
  barcode resolution. Matching scanned barcodes against a SKU field risks
  collisions with numeric SKUs, and its exact-string semantics would break
  UPC-12 ↔ EAN-13 equivalence.
- `gtin` is intentionally non-unique — multipacks and vendor parent listings
  legitimately share one. Resolution queries every valid zero-pad literal
  encoding of the scanned barcode (GTIN-8/12/13/14) via the server-side `gtin=`
  filter and merges the results by filament ID, so mixed-format duplicates of
  the same logical GTIN (e.g. one filament stored as UPC-12, another as
  EAN-13) all participate in the lowest-ID tie-break, not just whichever
  format happens to match a single exact-string query. An unfiltered full
  scan is used only as a fallback: either when no literal query matched
  anything (to catch a stored value formatted with separators or otherwise
  not a plain zero-pad literal), or immediately if Spoolman rejects/errors on
  the `gtin=` filter itself.

> **Operator note.** Barcode resolution requires a Spoolman instance exposing the
> `gtin` field, which the bundled PrintFarmer Spoolman image provides. If you
> point PrintFarmer at an upstream Spoolman without `gtin`, barcode scans will
> not resolve. PrintFarmer versions **before** the GTIN migration wrote scanned
> barcodes into `article_number`; those legacy mappings — along with any barcode
> typed into `article_number` by hand — must be backfilled into `gtin` to remain
> scannable, as `article_number` is no longer consulted. Current PrintFarmer
> writes target `gtin` only.
>
> Run `scripts/backfill-spoolman-gtin.py` to migrate legacy mappings. It is
> dry-run by default, only ever *adds* a `gtin`, never modifies or clears
> `article_number`, and skips values that are genuine vendor SKUs rather than
> barcodes:
>
> ```bash
> python3 scripts/backfill-spoolman-gtin.py --spoolman-url http://localhost:7912
> python3 scripts/backfill-spoolman-gtin.py --spoolman-url http://localhost:7912 --apply
> ```

## Spoolman Filament Deletion

All deletion and preview routes require `spoolman:admin`.

- `GET /api/spoolman/filaments/{id}/delete-preview` reads fresh references,
  including archived spools, and returns
  `{ "activeSpoolCount": 0, "archivedSpoolIds": [12, 14] }`. It deletes nothing.
- `POST /api/spoolman/filaments/{id}/delete-confirmed` accepts
  `{ "archivedSpoolIds": [12, 14] }` as explicit consent to permanently delete
  exactly those archived spools, then the filament. An empty array confirms
  that no spool deletion is authorized; a missing array is rejected.
- Active spools always block deletion, with their count. A changed spool set
  requires a fresh preview and confirmation. Remaining references are checked
  before every delete; newly discovered spools are never added to consent.
- After each acknowledged spool deletion, fresh reference reads wait up to
  five seconds for that deletion to become visible in older Spoolman versions.
  Only stale references to acknowledged deletions are retried, with bounded
  backoff; active, new or missing remaining spools stop the operation immediately.
  Non-convergence reports the acknowledged partial outcome instead of continuing.
- `DELETE /api/spoolman/filaments/{id}` and
  `DELETE /api/spoolman/filaments/bulk` never cascade to spools. Bulk results
  contain per-filament reasons for skipped/failed items. Delete archived-only
  filaments individually to review the permanent-deletion confirmation.

Archived spools can be absent from the normal inventory view while still
preventing Spoolman from deleting their filament. The web confirmation shows
the archived count and warns that their NFC tag bindings are removed too;
Spoolman owns this cascade. Filament NFC bindings are also removed.

Spoolman's individual REST deletes are **not a transaction**. Other clients can
still change a spool between a check and its delete. Do not edit the same
inventory concurrently during deletion. If an operation fails after deleting
some spools, the error lists the confirmed deleted spool IDs and warns that the
filament's deletion was not confirmed. Refresh before retrying: a timed-out
upstream request may have completed even without an acknowledgement.

## Spoolman Barcode Diagnostics

- `GET /api/spoolman/barcodes/scan-logs?limit=` returns recent barcode scan
  diagnostics newest first. Requires `farm_admin`; `limit` defaults to 100 and
  accepts 1-500.
- Logging is disabled by default. Enable the Spoolman setting
  `barcodeScanDebugLoggingEnabled` to persist scan attempts and outcomes.

## Spool Burn-Rate Projection

- `GET /api/spoolman/spools/{spoolId}/burn-rate` requires `sourceKind` and
  `sourceIdentity` query parameters. Source kinds are `Central` and
  `MoonrakerNative`; source URLs are normalized before lookup.
- The response includes the source-qualified identity, remaining grams,
  authoritative grams consumed in the configured lookback, grams per day,
  projected `spoolReorderThresholdGrams` crossing time, evaluation time,
  sample count, and `Ready`, `InsufficientData`, or `SourceUnavailable` state.
- Only positive backend-reported usage from completed jobs contributes.
  Estimated, failed/cancelled, unqualified, and duplicate history is excluded.

## Notes
- All timestamps are ISO 8601 UTC where possible.
- Pagination: new endpoints adopt `page` & `pageSize` query parameters with maximum pageSize 500.
- Filtering: search parameters perform case-sensitive substring matching unless stated otherwise.
