## 5. P3 Send to Printer Modal — Frontend Architecture (Implemented)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-04-04  
**Status:** ✅ IMPLEMENTED — Feature complete, 8 tests passing  
**Impact:** Medium (enables gcode delivery to online printers from slice jobs)  

### Context

Backend `POST /api/slice/{id}/send-to-printer` endpoint is ready. Frontend needs to let users send completed slice job gcode to selected printers.

### Decision

Implement **modal-based UX** for printer selection and sending, integrated on completed jobs in SliceJobsPage:

1. **Modal over inline form** — Cleaner job list, secondary action doesn't clutter primary view
2. **Child form state pattern** — Form mounts/unmounts with modal (avoids ESLint setState violations)
3. **Online-only printer filter** — Offline printers excluded entirely from dropdown (better UX than disabled state)
4. **No cache invalidation** — Send action doesn't change job status; skip `invalidateQueries`

### Implementation

**Components Created:**
- `src/features/slicer/components/SendToPrinterModal.tsx` (104 lines, child form pattern)
- `src/features/slicer/components/SendToPrinterModal.test.tsx` (8 tests)

**Files Modified:**
- `src/features/slicer/pages/SliceJobsPage.tsx` — Added "Send to Printer" button (card + table views)
- `src/services/sliceJobService.ts` — Added `SendToPrinterRequest`, `SendToPrinterResponse`, `sendToPrinter()`
- `src/features/slicer/pages/SlicerSettingsPanel.tsx` — Fixed duplicate destructuring (lint cleanup)

### Quality Gates
✅ Build clean (0 errors, 0 warnings)  
✅ Lint clean (0 errors, 0 warnings)  
✅ TypeScript strict mode — 0 type errors  
✅ Tests: 8/8 passing  
✅ Accessibility: WCAG 2.2 Level AA verified  

### Key Design Details

- **Modal integration:** Integrated on both card and table job views
- **Online filtering:** Uses `usePrintersFast()` filtered to `isOnline === true`
- **Form pattern:** `SendToPrinterForm` child component with `isOpen` lifecycle (mount/unmount)
- **API integration:** `sliceJobService.sendToPrinter()` with proper error handling

### Hand-off Notes
- Lambert: Backend endpoint validated
- Kane: Ready for E2E testing with mock printer selection
- Next: Cost tracking integration when job metadata available

---

## 6. P5 Onboarding — Profile Detection Strategy (Implemented)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-04-04  
**Status:** ✅ IMPLEMENTED — Feature complete, 4 tests passing  
**Impact:** Small (improves first-time UX for slice job creation)  

### Context

NewSliceJobPage uses cascading selectors (Printer → Machine Profile → Filament → Process). There's no single query that says "does this user have ANY profiles?" Need to detect empty state and guide users to import.

### Decision

1. **Detection via `listExtended()`** — Uses existing well-tested query with 5-min staleTime to check `machineProfiles.length > 0`
2. **Full-page onboarding banner** — Replaces form entirely (early return) rather than overlay; avoids layout jank
3. **Route activation** — Added `/slicer/import-official` with FeatureGate pattern (ImportOfficialProfilesPage was dead code, now routed)

### Implementation

**Components Modified:**
- `src/features/slicer/pages/NewSliceJobPage.tsx` — Integrated onboarding detection + banner
- `src/App.tsx` — Added `/slicer/import-official` route with FeatureGate

**Tests Created:**
- `src/test/features/slicer/components/NewSliceJobPageOnboarding.test.tsx` (4 tests)

### Quality Gates
✅ Build clean (0 errors, 0 warnings)  
✅ Lint clean (0 errors, 0 warnings)  
✅ TypeScript strict mode — 0 type errors  
✅ Tests: 4/4 passing  
✅ Accessibility: WCAG 2.2 Level AA verified  

### Trade-offs

- `listExtended()` fetches full profile list for count check (slightly heavier than dedicated count endpoint; acceptable given caching + small payload)
- Onboarding banner is full-page takeover — user must refresh after importing profiles in another tab (consistent with existing patterns)

### Hand-off Notes
- Lambert: Backend profile import endpoints validated
- Kane: Ready for E2E testing with zero-profile scenarios
- Dallas: Onboarding state can trigger analytics events
- Next: Profile import flow completion tracking

---

# Prusa Buddy Camera & Enhanced Status Integration Proposal

**Author:** Dallas (Lead / Architect)
**Date:** 2026-05-12
**Status:** PROPOSED
**Impact:** High (camera experience for Prusa printers; foundations for multi-source camera support)
**Reference:** [Prusa-StatusBar](https://github.com/deimosfr/Prusa-StatusBar) — MIT-licensed macOS status bar app

---

## Executive Summary

Prusa-StatusBar demonstrates that the Prusa Buddy 3D Camera is a **standalone network device** with its own IP, exposing an RTSP stream at `rtsp://<camera-ip>:554/live/`. PrintFarmer's current camera model already supports standalone cameras (`CameraSource.Standalone`), but lacks RTSP playback, event-driven snapshots, and Buddy-specific discovery. This proposal breaks the integration into three tiers: immediately useful, architecture-needed, and skip.

---

## Current State Analysis

### What PrintFarmer Has

| Capability | Status | Notes |
|---|---|---|
| Camera entity with `StreamUrl`, `SnapshotUrl` | ✅ | Supports standalone + printer-attached |
| `CameraSource.PrusaLink` enum | ✅ | Exists but unused — PrusaLink client returns `null` for camera URLs |
| `ISupportsCamera` on `PrusaLinkClient` | ✅ | Stub only — both methods return `null` |
| Camera CRUD API | ✅ | Full create/update/delete/toggle/display endpoints |
| Camera health monitoring | ✅ | 5-minute checks via HTTP to snapshot URLs, degraded/unhealthy tracking |
| PrusaLink status polling | ✅ | 5-second interval, extracts temps, position, progress, job name |
| Speed multiplier from PrusaLink | ⚠️ | Available in `SimplePrinterStatus.SpeedMultiplier` but **not propagated** via SignalR |
| Nozzle diameter / MMU flag | ⚠️ | Available in `PrinterInformation` (one-time fetch) but **not in status updates** |
| Z-height from PrusaLink | ✅ | `AxisZ` already in `PrinterStatusDto` |
| Filament type from PrusaLink | ❌ | PrusaLink API doesn't expose this in status |
| RTSP stream playback | ❌ | No transcoding infrastructure |
| Event-driven camera snapshots | ❌ | No snapshot-on-state-change mechanism |
| RTSP connectivity probing | ❌ | Health monitor only checks HTTP endpoints |

### What Prusa-StatusBar Does (Feature Map)

| Feature | How It Works | Relevance to PrintFarmer |
|---|---|---|
| Buddy Camera RTSP | `go2rtc` sidecar transcodes `rtsp://<ip>:554/live/` → HLS | High — enables browser-playable Buddy streams |
| Snapshot provider | GET `go2rtc /api/frame.jpeg` for stills from any source | High — event snapshots, timelapse stills |
| RTSP probe | TCP connect + RTSP DESCRIBE to verify camera before use | Medium — improves health checks for RTSP cameras |
| Generic camera support | HTTP still URL, MJPEG frame grab, RTSP via go2rtc | Medium — already partly covered by PrintFarmer |
| Notifications with snapshots | Capture still on print start/finish/attention events | High — enables event-driven camera capture |
| Extra status fields | Speed, Z-height, filament, MMU, nozzle diameter | Medium — some already available, some not |

---

## Tier 1: Immediately Useful (Low–Medium Effort)

### 1A. Propagate Speed Multiplier via SignalR

**Effort:** Small (1–2 hours)
**Value:** Users see print speed in real-time on the dashboard

`PrusaCompositeStatus` already has access to `SimplePrinterStatus.SpeedMultiplier` but it's not included in the `PrusaCompositeStatus` record or `PrinterStatusDto`. Fix:

1. Add `SpeedMultiplier` (int?, percentage 0–999) to `PrusaCompositeStatus`
2. Add `SpeedMultiplier` to `PrinterStatusDto`
3. Populate in `PrusaLinkPollingService` from the existing API response
4. Display in frontend printer card/detail

**Note:** This is backend-agnostic — Moonraker and OctoPrint can also populate this field.

### 1B. Surface Nozzle Diameter & MMU Flag from Printer Info

**Effort:** Small (2–3 hours)
**Value:** Operators see hardware config at a glance

`PrinterInformation` from PrusaLink already contains `NozzleDiameter` (float) and `HasMmu` (bool). These are fetched once during discovery/connection but not exposed to the UI.

1. Add `NozzleDiameter` and `HasMmu` to `Printer` entity (nullable, set during discovery)
2. Include in printer detail API response
3. Display in printer detail view

**Migration required** — both Postgres and SqlServer.

### 1C. Buddy Camera as Standalone Camera (Manual Config)

**Effort:** Small (1–2 hours)
**Value:** Users can add Buddy cameras today using existing CRUD

The existing camera CRUD already supports this. A user can:
- Create a standalone camera with `StreamUrl = rtsp://<camera-ip>:554/live/`
- Associate it with a printer via `PrinterId`
- Set `CameraType = General` or `Wide`

**What's missing:** The frontend Camera View can't play RTSP streams (browsers don't support RTSP natively). This is addressed in Tier 2. For now, `SnapshotUrl` could point to a go2rtc instance if the user runs one manually.

**Action:** Document this manual workflow in the camera setup guide. No code change needed.

### 1D. RTSP Health Probe

**Effort:** Medium (4–6 hours)
**Value:** Camera health checks work for RTSP cameras, not just HTTP

Current `CameraHealthMonitorService` only does HTTP HEAD/GET to snapshot URLs. For RTSP cameras:

1. Add an `ICameraProbe` interface with `ProbeAsync(string url)` returning health result
2. Implement `HttpCameraProbe` (existing behavior) and `RtspCameraProbe`
3. `RtspCameraProbe`: TCP connect to port 554, send RTSP `OPTIONS` request, check for `200 OK`
4. Health monitor selects probe based on URL scheme (`rtsp://` vs `http://`)

This is self-contained, no external dependencies, and makes camera health work for Buddy cameras.

---

## Tier 2: Needs Architecture Work (Medium–High Effort)

### 2A. RTSP → Browser-Playable Transcoding (go2rtc Sidecar)

**Effort:** High (2–3 days for initial; ongoing maintenance)
**Value:** Live Buddy camera streams in the browser

Browsers cannot play RTSP natively. Options:

| Approach | Pros | Cons |
|---|---|---|
| **go2rtc sidecar container** | MIT-licensed, proven, RTSP→WebRTC/HLS/MSE, single binary | New container to manage, ~30MB image |
| **ffmpeg transcoding in API** | No new container | Heavy CPU load, complex pipeline management |
| **Client-side WebRTC** | No server transcoding | Requires STUN/TURN, complex NAT traversal |

**Recommendation: go2rtc sidecar container.**

Architecture:
```
Browser ──WebRTC/MSE──▸ go2rtc (:1984) ──RTSP──▸ Buddy Camera (:554)
                          ▲
                          │ /api/frame.jpeg (snapshots)
                          │
                    PrintFarmer API (camera health, event snapshots)
```

Implementation:
1. Add `docker-compose.go2rtc.yml` template to `scripts/docker/compose-templates/`
2. go2rtc config generated from PrintFarmer camera registry (RTSP URLs → stream names)
3. API proxies or redirects camera stream/snapshot requests through go2rtc
4. Frontend `CameraView` component detects RTSP cameras and uses go2rtc WebRTC/MSE player
5. Add `go2rtc` to `container-versions.conf`

**Config sync concern:** When cameras are added/removed, go2rtc config needs updating. Options:
- **A)** go2rtc API mode — add/remove streams via REST API at runtime (preferred)
- **B)** Config file regeneration + container restart on camera change

go2rtc supports runtime stream management via its API, so option A is cleaner.

### 2B. Event-Driven Camera Snapshots

**Effort:** Medium (1–2 days)
**Value:** Automatic snapshots on print start, finish, error — for notifications, history, timelapse

Architecture:
1. `PrusaLinkPollingService` (and other backend pollers) already detect state transitions
2. On state change (Idle→Printing, Printing→Finished, any→Error), emit a domain event
3. New `CameraSnapshotService` subscribes to these events
4. Service finds cameras associated with the printer, captures a snapshot (HTTP GET to snapshot URL or go2rtc `/api/frame.jpeg`)
5. Store snapshot as a `PrintEvent` attachment or in a `CameraSnapshot` table
6. Optionally include in notification payloads (future notification system)

**Dependencies:**
- For HTTP/MJPEG cameras: works immediately
- For RTSP cameras: requires go2rtc (2A) for snapshot capture via `/api/frame.jpeg`

### 2C. Buddy Camera Auto-Discovery

**Effort:** Medium (1 day)
**Value:** When adding a Prusa printer, automatically find its Buddy camera on the network

PrusaLink API doesn't expose camera information. Options:

1. **mDNS/Bonjour discovery** — Buddy cameras may advertise via mDNS (needs verification)
2. **Subnet scan** — Probe port 554 on the printer's subnet for RTSP responders
3. **User hint** — During printer setup, prompt "Does this printer have a Buddy camera?" and ask for IP

**Recommendation:** Start with option 3 (user hint during printer add/edit), add mDNS later if Buddy cameras advertise themselves.

Add a `CameraIp` or `BuddyCameraHost` field to the printer setup flow. When provided, auto-create a Camera entity with:
- `StreamUrl = rtsp://<camera-ip>:554/live/`
- `SnapshotUrl` = go2rtc endpoint (if available) or null
- `Source = CameraSource.PrusaLink`
- `CameraType = CameraType.Wide`
- `PrinterId` = the printer being configured

---

## Tier 3: Skip (Not Worth It / Doesn't Fit)

### 3A. go2rtc as Embedded Process (Not Container)

Running go2rtc inside the API container adds process management complexity and breaks our single-process-per-container convention. The sidecar container approach is cleaner and aligns with our Docker deployment model.

### 3B. Filament Type from PrusaLink Status

PrusaLink's API doesn't expose the loaded filament type in status responses. PrintFarmer already has a separate filament/spool management system with Spoolman integration. Adding filament type detection from the printer would be unreliable and conflict with our spool tracking.

### 3C. Full MMU Status Tracking

Prusa-StatusBar shows basic MMU presence. Full MMU status (which slot is active, errors, filament runout per slot) would require deep PrusaLink API integration that doesn't exist in the public API. The `HasMmu` flag from Tier 1B is sufficient for now.

### 3D. macOS-Style Notifications

Prusa-StatusBar's notification system is macOS-native. PrintFarmer's notification architecture should be platform-agnostic (web push, email, webhooks). Camera snapshots in notifications (Tier 2B) is the right feature; the delivery mechanism is a separate concern.

---

## Recommended Implementation Order

| Priority | Item | Effort | Dependencies |
|---|---|---|---|
| P0 | 1A — Speed multiplier in SignalR | 1–2h | None |
| P0 | 1B — Nozzle diameter + MMU flag | 2–3h | DB migration |
| P1 | 1D — RTSP health probe | 4–6h | None |
| P1 | 1C — Document manual Buddy camera setup | 1h | None |
| P2 | 2A — go2rtc sidecar for RTSP transcoding | 2–3d | Docker compose templates |
| P2 | 2C — Buddy camera field in printer setup | 1d | 2A for full value |
| P3 | 2B — Event-driven snapshots | 1–2d | 2A for RTSP cameras |

**Total estimated effort:** ~5–7 days for full implementation across all tiers.

---

## Architecture Decisions Required

1. **go2rtc deployment model** — Sidecar container vs. user-managed external instance? Sidecar is recommended but adds a container to manage.

2. **Snapshot storage** — File system (like existing 3D model uploads) vs. database blob vs. object storage? File system is simplest and consistent with existing patterns.

3. **Camera-printer association for Buddy** — Extend printer setup form with optional camera IP, or keep camera management fully separate? Recommend extending printer setup for Prusa printers.

4. **go2rtc config sync** — Runtime API management (preferred) vs. config regeneration? Need to verify go2rtc's API supports all our needs.

---

## Risk Assessment

| Risk | Likelihood | Mitigation |
|---|---|---|
| go2rtc adds deployment complexity | Medium | Make it optional; non-RTSP cameras work without it |
| Buddy camera IP changes (DHCP) | Medium | Document static IP recommendation; health monitor detects failures |
| RTSP probe false positives/negatives | Low | Use RTSP OPTIONS (lightweight), fall back to TCP connect |
| go2rtc WebRTC NAT issues in Docker | Medium | go2rtc supports multiple output formats (HLS, MSE, WebRTC); fall back to HLS if WebRTC fails |

---

## References

- [Prusa-StatusBar source](https://github.com/deimosfr/Prusa-StatusBar) — MIT license
- [go2rtc](https://github.com/AlexxIT/go2rtc) — MIT license, 30MB Docker image
- [PrusaLink API docs](https://github.com/prusa3d/Prusa-Link-Web) — camera endpoints not exposed
- PrintFarmer camera infra: `src/infra/Domain/Camera.cs`, `src/api/Controllers/CamerasController.cs`
- PrintFarmer PrusaLink plugin: `src/backends/Farm.Backend.Plugin.PrusaLink/`
### 2026-04-26: User directive
**By:** Jeff Papiez (via Copilot)
**What:** Catalog model machine profile selection must only match slicer aliases defined in the catalog; do not fall back to manufacturer/model lookup for catalog selections.
**Why:** User clarified that profile selection source of truth is the catalog's configured slicer alias.
