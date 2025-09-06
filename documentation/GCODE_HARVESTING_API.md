# G-code Harvesting API

This document provides detailed reference for the G-code Harvesting endpoints.

## Overview
The harvesting system scans a printer's G-code storage, filters candidate files, records discovered files, and optionally imports them into the central library. Operations are asynchronous and progress is observable via the REST API or real-time SignalR events on `/hubs/printers`.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/gcode-harvest/start` | Start a harvest operation |
| GET | `/api/gcode-harvest/operations/{operationId}` | Operation status/details |
| GET | `/api/gcode-harvest/operations/{operationId}/files` | Discovered files list |
| POST | `/api/gcode-harvest/import` | Import selected discovered files |
| POST | `/api/gcode-harvest/operations/{operationId}/cancel` | Cancel running operation |
| GET | `/api/gcode-harvest/printers/{printerId}/active` | Active operation for printer |
| GET | `/api/gcode-harvest/printers/{printerId}/recent?count=10` | Recent operations for printer |
| GET | `/api/gcode-harvest/active` | All active operations |

## Start Harvest
`POST /api/gcode-harvest/start`

Request body (StartGcodeHarvestDto):
```json
{
  "printerId": "11111111-1111-1111-1111-111111111111",
  "includeSubdirectories": true,
  "fileExtensions": ["gcode", "gco"],
  "minFileSizeBytes": 1024,
  "maxFileSizeBytes": 104857600,
  "modifiedAfter": "2025-09-01T00:00:00Z",
  "duplicateHandling": "skip"
}
```

Field notes:
- `fileExtensions`: null/empty => no extension filtering.
- `minFileSizeBytes` / `maxFileSizeBytes`: inclusive bounds (null removes bound).
- `modifiedAfter`: strict greater-than filter.
- `duplicateHandling`:
  - `skip`: ignore existing (increments FilesSkipped)
  - `overwrite`: replace existing entry (increments FilesAdded)
  - `rename`: import as new (adds suffix `-copy`, `-copy2`, ...)

Response (GcodeHarvestResultDto):
```json
{
  "operationId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "success": true,
  "message": "Harvest started",
  "discoveredFiles": 0,
  "importedFiles": 0,
  "errors": null
}
```

## Operation Object
`GcodeHarvestOperationDto` (summary):
```json
{
  "id": "...",
  "printerId": "...",
  "status": "Running|Completed|Failed|Cancelled",
  "startedAt": "2025-09-06T07:30:00Z",
  "completedAt": null,
  "filesFound": 0,
  "filesAdded": 0,
  "filesSkipped": 0,
  "filesErrored": 0,
  "totalBytesProcessed": 0,
  "includeSubdirectories": true,
  "maxFileSizeBytes": 104857600,
  "modifiedAfter": null,
  "fileExtensions": ["gcode","gco"],
  "minFileSizeBytes": 1024,
  "duplicateHandling": "skip"
}
```

## Discovered File Object
`DiscoveredGcodeFileDto` (summary):
```json
{
  "id": "...",
  "harvestOperationId": "...",
  "printerPath": "/path/to/file.gcode",
  "fileName": "file.gcode",
  "fileSizeBytes": 123456,
  "modifiedAt": "2025-09-05T14:11:00Z",
  "fileHash": null,
  "isSelected": false,
  "alreadyInLibrary": false,
  "existingLibraryFileId": null,
  "processingFailed": false,
  "errorMessage": null,
  "extractedSlicerName": null,
  "extractedSlicerVersion": null,
  "extractedPrintTime": null,
  "extractedFilamentLength": null,
  "extractedNozzleDiameter": null,
  "extractedMaterial": null,
  "extractedLayerHeight": null,
  "extractedInfill": null
}
```

## Import Selected Files
`POST /api/gcode-harvest/import`
```json
{
  "harvestOperationId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "selectedFileIds": ["f1f1...", "f2f2..."],
  "addToLibraryOnly": true,
  "autoDetectCapabilities": true,
  "defaultTags": ["harvested"]
}
```

## Cancellation
Cancellation attempts return HTTP 200 with `true` if successful, `400` if the operation can no longer be cancelled.

## Real-time Updates
Operation progress & completion events are broadcast through the existing printers SignalR hub. The client merges updates into local state.

## Development Notes
- Schema managed by `EnsureCreated()` (no active migrations). A temporary migration was generated and removed (2025-09-06) to maintain rapid iteration.
- Duplicate handling is enforced during worker processing when a file hash / name indicates an existing library entry.

## Future Enhancements (Roadmap)
- Pagination & server-side filtering for discovered files
- Retry queue for transient download errors
- Hash pre-check via printer metadata where available
- Optional auto-import on completion

