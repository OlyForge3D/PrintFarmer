# API Reference (Summary)

This document aggregates endpoint groups and key DTOs. For specialized domains see:
- `GCODE_HARVESTING_API.md`

## Tags / Endpoint Groups
- Authentication
- Printers
- Catalog
- G-code Harvesting
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

### StartGcodeHarvestDto
Configuration for starting a harvest (filters & duplicate handling).

### DiscoveredGcodeFileDto
Represents a file found on a printer during harvest.

### GcodeHarvestOperationDto
Tracks progress/state of a harvest operation.

### PagedResult<T>
Generic wrapper for paging: items, totalCount, page, pageSize, totalPages.

### AuthenticationResult
Result of login/registration containing JWT token & user metadata.

### BarcodeScanLogDto
Admin-facing diagnostic entry for optional Spoolman barcode scan logging. Fields include
`id`, `timestamp`, `barcode`, `action`, `outcome`, `httpStatus`,
`matchedFilamentId`, `createdSpoolId`, `userId`, and `message`.

(Extend with additional DTO details as needed.)

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
  legitimately share one. When several filaments match, the lowest-ID filament
  wins deterministically.

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
