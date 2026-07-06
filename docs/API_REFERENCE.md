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

## Spoolman Barcode Diagnostics

- `GET /api/spoolman/barcodes/scan-logs?limit=` returns recent barcode scan
  diagnostics newest first. Requires `farm_admin`; `limit` defaults to 100 and
  accepts 1-500.
- Logging is disabled by default. Enable the Spoolman setting
  `barcodeScanDebugLoggingEnabled` to persist scan attempts and outcomes.

## Notes
- All timestamps are ISO 8601 UTC where possible.
- Pagination: new endpoints adopt `page` & `pageSize` query parameters with maximum pageSize 500.
- Filtering: search parameters perform case-sensitive substring matching unless stated otherwise.
