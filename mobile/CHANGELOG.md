# Changelog

All notable changes to PrintFarmer iOS will be documented in this file.

## [v1.0-beta.68] — 2026-04-03

### Fixed
- **Printer status mismatch between list and detail** — The printer detail screen now applies data from `/api/printers/{id}/status` to the displayed printer state, preventing cases where a printer appears "Ready" in the list but "Offline" after opening details.

## [v1.0-beta.67] — 2026-04-03

### Fixed
- **NFC filament data now shows correct values** — Diameter, print temp, and bed temp in the Write NFC Tag preview were showing "—" or placeholder text. Filament lookup now uses the spool's filament ID (not material/vendor name matching), so the correct filament record is always found.
- **NFC preview no longer blank on first open** — Spool and filament data are now bundled together atomically before the sheet is presented, eliminating a race condition that caused the three filament fields to show "—" on first open and only populate after closing and reopening.
- **OpenPrintTag payload now includes filament data** — The actual NFC tag written in OpenPrintTag format now correctly encodes `filament_diameter`, `print_temp`, and `bed_temp` from Spoolman filament settings.

### Changed
- **NFC field label column widened** — Increased label width from 100 to 130 pt so `filament_diameter` no longer wraps.

## [v1.0-beta.66] — 2026-04-03

### Added
- **OpenPrintTag write support** — New writable NFC format alongside OpenSpool and OpenTag3D. Writes JSON with `filament_type`, `color`, `manufacturer`, `net_weight`, and `spool_id` fields.
- **NFC field preview** — Write NFC Tag view now shows all fields that will be written, with format-specific field names (OpenSpool JSON keys, OpenPrintTag JSON keys, or OpenTag3D binary fields).
- **NFC format descriptions** — Settings picker now shows a dynamic description for each format explaining compatibility and encoding.

### Changed
- **Default NFC format** — New installs default to OpenPrintTag instead of OpenSpool.

## [v1.0-beta.65] — 2026-04-03

### Fixed
- **Notification entitlement error** — Removed `registerForRemoteNotifications()` call that caused "no valid aps-environment entitlement" error when enabling notifications. Local notifications via `UNUserNotificationCenter` require no entitlement.

### Added
- **NFC write format indicator** — The Write NFC Tag view now shows the selected tag format (OpenSpool or OpenTag3D) as a pill badge so users can confirm the format before writing.

## [v1.0-beta.64] — 2026-04-03

### Added
- **OpenTag3D NFC tag format** — New binary NFC tag format (opentag3d.info/spec) as a user-selectable alternative to OpenSpool JSON. Core fields (version, material, manufacturer, color, diameter, density, temperatures, target weight) fit NTAG213. Extended fields (batch/serial number, empty spool weight, measured weight/length) written on NTAG215+ when data is available.
- **NFC format picker** — Settings UI now includes an NFC Tags section with OpenSpool/OpenTag3D format picker, persisted via AppStorage.

## [v1.0-beta.12] — 2026-03-12

### Added
- **Local notifications for PendingReady** — App now sends local "Bed Clear Required" notifications when printers enter PendingReady state. Tracks per-printer to avoid spam; re-notifies if printer re-enters PendingReady.

### Fixed
- **Entitlements build failure** — Removed hardcoded `aps-environment` from entitlements file. The build system injects it automatically from the provisioning profile, fixing the "entitlements modified during build" Xcode Cloud error.

### Changed
- **Switched from APNs to local notifications** — PrintFarmer's self-hosted architecture means each installation can't hold centralized Apple credentials. Notifications now use `UNUserNotificationCenter` local notifications instead of APNs push.

## [v1.0-beta.11] — 2026-03-11

### Added
- **Real-time SignalR updates** — Temperatures, state, progress, and job info now update live via WebSocket. No more manual pull-to-refresh needed.
- **Custom nozzle & radiator icons** — Hotend and bed temperature displays now use 3D printer nozzle and radiator icons matching the web UI (replaces generic flame and stack icons).
- **`bedClearRequired` notification type** — Prepares for push notifications when a printer needs its bed cleared.
- **`fileName` field** — Dashboard, printer cards, and printer detail now show the gcode file name (e.g. `benchy.gcode`) instead of the full job path.

### Fixed
- **Bed clear UI not updating** — Tapping "Confirm Bed Clear" now immediately clears the warning banner and buttons (optimistic update with delayed reload).
- **Push notification entitlements** — Added missing `aps-environment` entitlement so push notifications can be enabled.
- **SignalR log noise** — Silenced `toolheadUpdate`, `extruderUpdate`, and `heaterBedUpdate` events that were flooding the debug log.

## [v1.0-beta.10] — 2026-03-11

### Added
- **PendingReady indicator badge** — Orange badge on the Printers tab (iPhone) and sidebar (iPad) shows count of printers waiting for bed clear, matching the web UI's pulsing alert icon.

## [v1.0-beta.9] — 2026-03-11

### Added
- **Job thumbnails** — Gcode file thumbnails now display in job detail pages and job queue list rows.

### Fixed
- **Dispatch dashboard** — Redesigned to show real metrics: pending printers, busy printers, and dispatched jobs in last 24 hours.
- **Job analytics decoding** — Fixed JSON decoding error ("data couldn't be read because it's missing").
- **Predictive insights** — Risk factor percentages now display correctly (was showing 8000%, 10000%).

## [v0.1.0-beta.7] — 2025-07-21

### Added
- **Per-printer camera rotation** — Rotate button next to camera refresh in PrinterDetailView cycles through 0°→90°→180°→270° with per-printer UserDefaults persistence. Fixes upside-down camera feeds on printers like Phrozen Arco. (`c849000`)

### Changed
- **Compact button layouts & shorter labels** — Simultaneous action buttons now grouped side-by-side (Pause+Abort, Resume+Abort, Retry+Cancel, Set+Scan Tag). Labels shortened for clarity: Change Filament→Change, Write NFC Tag→Write Tag, Acknowledge→Accept, Clear Filters→Reset, Scan NFC Tag→Scan Tag. Fixed NFCWriteView error state layout (VStack→HStack). All touch targets remain ≥44pt HIG compliant. (`9f5fe50`)
