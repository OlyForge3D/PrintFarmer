
# OctoPrint Backend Integration Plan

## Overview
Add support for OctoPrint as a printer backend in PrintFarmer, enabling management and monitoring of OctoPrint-powered 3D printers alongside Moonraker, PrusaLink, and SDCP.

## Steps

### 1. Backend (C#/.NET)
- [x] Add OctoPrint to PrinterBackend enum (shared and API)
- [x] Add OctoPrint-specific fields to Printer model/DTO (API key, camera URL, etc.)
- [x] Create IOctoPrintClient interface and OctoPrintClient implementation
- [x] Implement OctoPrint API calls (status, job control, camera, etc.)
- [x] Integrate OctoPrintClient into PrintersController and related services
- [x] Plugin detection and auto-discovery for X/Y/Z and spool info (Display Current Position, SpoolManager, Spoolman plugins)
- [x] Field parity with Moonraker maximized; differences and plugin requirements documented in `docs/octoprint-vs-moonraker-parity.md`
- [x] Update database migrations if new fields are needed
- [x] Add validation for OctoPrint printer configuration
- [x] Add unit/integration tests for OctoPrint backend

### 2. Frontend (React/TypeScript)
- [x] Add 'OctoPrint' to PrinterBackend enum/type
- [x] Add OctoPrint icon/branding and update getBackendIcon logic in all relevant components (PrinterDiscoveryModal, PrinterCard, PrinterTableView, EnhancedPrinterCard) to use custom SVG for OctoPrint and unique icons for each backend
- [x] Update printer creation/edit UI to support OctoPrint
- [x] Add OctoPrint-specific fields (API key, camera URL) to forms
- [x] Update API client/types for OctoPrint support
- [x] Update UI logic to handle OctoPrint printers (status, camera, controls)
- [x] Add frontend tests for OctoPrint support (PrinterCard/EnhancedPrinterCard, SignalR/Auth mocks, all tests passing)

### 3. Documentation
- [x] Document OctoPrint integration, configuration, and limitations (see `docs/octoprint-vs-moonraker-parity.md`)
- [x] Update README and API docs as needed

## References
- OctoPrint REST API: https://docs.octoprint.org/en/main/api/
- PrintFarmer architecture: see repo docs

## Acceptance Criteria
- Users can add/manage OctoPrint printers in PrintFarmer
- Status, job control, and camera features work for OctoPrint printers
- OctoPrint printers are clearly identified in the UI (custom icon/branding implemented)
- Plugin detection and field parity logic is implemented in backend
- All tests pass and documentation is updated

## Status Summary (2025-09-19)

- ✅ OctoPrint backend and API integration complete
- ✅ Frontend UI logic, camera, and controls for OctoPrint fully implemented
- ✅ All frontend and backend tests pass (including robust UI tests for OctoPrint logic)
- ✅ Documentation and parity notes updated
- 🟢 OctoPrint support is stable and fully integrated in PrintFarmer
