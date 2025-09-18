
# OctoPrint Backend Integration Plan

## Overview
Add support for OctoPrint as a printer backend in PrintFarmer, enabling management and monitoring of OctoPrint-powered 3D printers alongside Moonraker, PrusaLink, and SDCP.

## Steps

### 1. Backend (C#/.NET)
- [x] Add OctoPrint to PrinterBackend enum (shared and API)
- [x] Add OctoPrint-specific fields to Printer model/DTO (API key, camera URL, etc.)
- [ ] Create IOctoPrintClient interface and OctoPrintClient implementation
- [ ] Implement OctoPrint API calls (status, job control, camera, etc.)
- [ ] Integrate OctoPrintClient into PrintersController and related services
- [x] Update database migrations if new fields are needed
- [x] Add validation for OctoPrint printer configuration
- [ ] Add unit/integration tests for OctoPrint backend

### 2. Frontend (React/TypeScript)
- [x] Add 'OctoPrint' to PrinterBackend enum/type
- [x] Add OctoPrint icon/branding and update getBackendIcon logic in all relevant components (PrinterDiscoveryModal, PrinterCard, PrinterTableView, EnhancedPrinterCard) to use custom SVG for OctoPrint and unique icons for each backend
- [ ] Update printer creation/edit UI to support OctoPrint
- [ ] Add OctoPrint-specific fields (API key, camera URL) to forms
- [ ] Update API client/types for OctoPrint support
- [ ] Update UI logic to handle OctoPrint printers (status, camera, controls)
- [ ] Add frontend tests for OctoPrint support

### 3. Documentation
- [ ] Document OctoPrint integration, configuration, and limitations
- [ ] Update README and API docs as needed

## References
- OctoPrint REST API: https://docs.octoprint.org/en/main/api/
- PrintFarmer architecture: see repo docs

## Acceptance Criteria
- Users can add/manage OctoPrint printers in PrintFarmer
- Status, job control, and camera features work for OctoPrint printers
- OctoPrint printers are clearly identified in the UI (custom icon/branding implemented)
- All tests pass and documentation is updated
