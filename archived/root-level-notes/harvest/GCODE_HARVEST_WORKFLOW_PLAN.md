# G-code Harvest Full Workflow Implementation Plan

## Overview
This document outlines the step-by-step plan to implement a robust G-code harvest workflow in PrintFarmer, covering discovery, user selection, backend APIs, real-time status, and UI integration.

---

## 1. Discovery Phase
- **Goal:** Discover all files on the printer (with thumbnails) and persist them for the current harvest session.
- **Tasks:**
  - Update backend to store discovered files (including metadata and thumbnail URLs) for each harvest session (operationId).
  - Ensure discovered files are not yet copied to the G-code library.
  - Model: `HarvestDiscoveredFile` (operationId, filePath, fileName, size, thumbnailUrl, status, etc.)

## 2. User Selection Phase
- **Goal:** Allow the user to select which discovered files to harvest.
- **Tasks:**
  - Add frontend UI to display all discovered files (with thumbnails).
  - Allow user to select files and submit their selection.
  - Backend endpoint to accept selection and start harvest for chosen files.

## 3. Harvest Operation Phase
- **Goal:** Copy selected files to the G-code library and track per-file status.
- **Tasks:**
  - Backend service to process selected files, updating status (pending, in progress, complete, failed, cancelled, skipped).
  - Persist and emit real-time status updates for each file (SignalR).
  - Model: Extend `HarvestDiscoveredFile` or add `HarvestFileStatus`.

## 4. Real-Time Status & UI
- **Goal:** Display real-time status for each file being harvested.
- **Tasks:**
  - Frontend subscribes to per-file status updates (SignalR).
  - UI shows all states: pending, in progress, complete, failed, cancelled, skipped.

## 5. API Endpoints
- **Discovery:** `POST /gcode-harvest/operations/{operationId}/discover` (trigger discovery)
- **Fetch Discovered:** `GET /gcode-harvest/operations/{operationId}/discovered-files`
- **Submit Selection:** `POST /gcode-harvest/operations/{operationId}/select-files`
- **Fetch Status:** `GET /gcode-harvest/operations/{operationId}/file-status`

## 6. Integration & Testing
- **Goal:** Validate the full workflow with real and simulated printers.
- **Tasks:**
  - End-to-end tests for discovery, selection, harvest, and status updates.
  - Manual UI validation.

---


## Progress Log
 [x] Step 4: Backend API to submit selection (import endpoint for selected files)
 [x] Step 5: Import actions in frontend (submit selected files to backend for import, update UI on result)
 [ ] Step 6: Backend real-time status for each file
 [ ] Step 7: Frontend real-time status UI
 [ ] Step 8: Integration and testing
### [2025-09-21] Frontend: Enhanced discovered files UI & import actions
- Added thumbnails, file metadata, improved status icons, tooltips, and accessibility to IndexedFilesList
- Improved error display and palette usage
- Implemented import actions for selected files (frontend and backend integration)
- [NEXT] Backend: Real-time status for each file

---

## Implementation Progress Log

### [2025-09-21] Backend: Add HarvestDiscoveredFile entity and persistence
- Created `HarvestDiscoveredFile` entity with all required properties for per-file tracking
- Registered in `AppDbContext` and added EF Core configuration
- Updated `GcodeHarvestService` to use new entity for discovered files
- Added/updated skip/retry logic and event emission

### [2025-09-21] Backend: Implement API endpoint to fetch discovered files for a session
- Implemented `GET /api/gcode-harvest/operations/{operationId}/files` endpoint in `GcodeHarvestController`
- Returns all discovered files for a session as `DiscoveredGcodeFileDto[]`
- Confirmed backend and service logic for filtering by session/operation

### [2025-09-21] Frontend: Integrate discovered files API and update UI
- Added `DiscoveredGcodeFileDto` and `HarvestFileStatus` types to `src/Web/ReactApp/src/types/api.ts`
- [NEXT] Update frontend API client to call new endpoint
- [NEXT] Update harvest UI to display discovered files with real-time status, error, and actions
- [NEXT] Add selection, import, and per-file controls
- [NEXT] Ensure palette and accessibility compliance

### [NEXT] Testing & Validation
- Add/expand automated tests for new UI and API integration
- Manual validation of full workflow: discovery, selection, import, error handling, and review

---

*This document will be updated as progress is made on each step.*
