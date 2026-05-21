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
# PFarm1-873d: Buddy Camera Auto-Discovery Setup Field — Architecture

**Author:** Dallas (Lead / Architect)
**Date:** 2026-05-12
**Status:** PROPOSED
**Bead:** PFarm1-873d (P3)
**Impact:** Medium (foundational for all Buddy camera beads)

---

## Problem

Users with Prusa Buddy 3D Cameras need a way to associate them with printers during setup. The Buddy camera is a **standalone network device** — it has its own IP, streams RTSP at `rtsp://{ip}:554/live/`, and is NOT accessible through PrusaLink's API. Today, users would have to manually create a Camera entity via the Cameras page with the correct URLs. That's clunky and error-prone.

## Decision

**Add a `BuddyCameraHost` nullable string field to the Printer entity.** On printer save, auto-derive RTSP/snapshot URLs and upsert a linked Camera entity.

### Why on Printer, not standalone Camera-only

1. **UX coherence.** The Buddy camera physically sits on the printer. Users think "my printer has a camera," not "I have a standalone camera that happens to point at a printer." Putting the field in the printer edit modal matches that mental model.
2. **Existing pattern.** Printer already has `CameraStreamUrl` and `CameraSnapshotUrl` fields, plus an "Auto-Detect" button in `EditPrinterModal`. Adding `BuddyCameraHost` follows this pattern.
3. **Camera entity still created.** The field is just the input. The output is a proper `Camera` entity linked to the printer — which gives us health monitoring, multi-camera support, and the Cameras page view for free.

### Why a separate field instead of reusing CameraStreamUrl

`CameraStreamUrl` is a generic URL field populated by backend discovery (Moonraker, PrusaLink). The Buddy camera is a separate device that needs its own IP/hostname stored so we can:
- Re-derive URLs if the URL format changes
- Probe the device independently for health
- Distinguish Buddy-managed cameras from backend-discovered cameras

---

## Schema Changes

### Printer Entity

```csharp
// src/infra/Domain/Printer.cs — new nullable field
[MaxLength(253)]
public string? BuddyCameraHost { get; set; }
```

**253 chars** = max FQDN length per RFC 1035. Accepts IP address or hostname.

### CameraSource Enum

```csharp
// src/infra/Domain/Enums/CameraEnums.cs — new value
public enum CameraSource
{
    Standalone,
    Moonraker,
    PrusaLink,
    OctoPrint,
    SDCP,
    FlashForge,
    BuddyCamera  // <-- new
}
```

**Why not reuse `PrusaLink`?** Because `PrusaLink` means "discovered via the PrusaLink API." The Buddy camera is discovered via user-provided IP — different source, different health probe path, different lifecycle.

### DB Migration Required

Yes — `BuddyCameraHost` on Printer table. Both PostgreSQL and SQL Server migrations needed.

```bash
cd src
DB_PROVIDER=postgres dotnet ef migrations add AddBuddyCameraHostToPrinter \
  --project ./migrations/Farm.Migrations.PostgreSQL \
  --startup-project ./migrations/Farm.Migrations.PostgreSQL \
  --context AppDbContext

DB_PROVIDER=sqlserver dotnet ef migrations add AddBuddyCameraHostToPrinter \
  --project ./migrations/Farm.Migrations.SqlServer \
  --startup-project ./migrations/Farm.Migrations.SqlServer \
  --context AppDbContext
```

No new Camera columns needed — the Camera entity already has everything we need (`StreamUrl`, `SnapshotUrl`, `Source`, `PrinterId`, `CameraType`).

---

## URL Auto-Derivation

When `BuddyCameraHost` is set on a printer save:

```
RTSP URL:     rtsp://{buddyCameraHost}:554/live/
Snapshot URL: null (requires go2rtc sidecar — PFarm1-lzf0)
```

Snapshot URL stays null until the go2rtc sidecar is deployed. Once go2rtc is available, it becomes `http://go2rtc:1984/api/frame.jpeg?src={streamName}`. This is a future concern — the Camera entity can be updated later without changing the Printer schema.

---

## Camera Entity Lifecycle

### On Printer Create/Update with BuddyCameraHost set

1. Look for existing Camera with `PrinterId = printer.Id` AND `Source = BuddyCamera`
2. **If found:** Update `StreamUrl` to new derived URL. Update `Name` if printer name changed.
3. **If not found:** Create new Camera:
   ```
   Name:        "{PrinterName} Buddy Camera"
   StreamUrl:   rtsp://{buddyCameraHost}:554/live/
   SnapshotUrl: null
   Source:      CameraSource.BuddyCamera
   CameraType:  CameraType.Wide
   PrinterId:   printer.Id
   IsEnabled:   true
   ```

### On Printer Update with BuddyCameraHost cleared (set to null/empty)

1. Find Camera with `PrinterId = printer.Id` AND `Source = BuddyCamera`
2. **Delete it.** The user is saying "this printer no longer has a Buddy camera."
3. This is safe because the camera was auto-created, not user-configured with custom settings.

### On Printer Delete

Existing cascade behavior handles this — Cameras with `PrinterId` FK are deleted.

---

## API Contract Changes

### UpdatePrinterDto

```csharp
// src/infra/Dtos/UpdatePrinterDto.cs — new field
[MaxLength(253)]
public string? BuddyCameraHost { get; set; }
```

### CreatePrinterFromDiscoveryDto

```csharp
// src/infra/Dtos/Discovery/CreatePrinterFromDiscoveryDto.cs — new field
[MaxLength(253)]
public string? BuddyCameraHost { get; set; }
```

### PrinterDto (response)

```csharp
// Ensure BuddyCameraHost is included in the printer response DTO
public string? BuddyCameraHost { get; set; }
```

### No changes to Camera API

The Camera CRUD API stays untouched. Buddy cameras are managed through the Printer API — they appear in Camera endpoints like any other camera.

---

## Backend Implementation Points

### Where the upsert logic lives

**`PrinterService`** (or wherever printer create/update is handled in `src/infra/`). After the printer entity is saved:

```
if BuddyCameraHost is set → upsert BuddyCamera Camera entity
if BuddyCameraHost is cleared → delete BuddyCamera Camera entity
```

This should call `CameraService.CreateForPrinterAsync()` or a new dedicated method. Keep it simple — no new service class.

### Validation

- `BuddyCameraHost` must be a valid IP address or hostname (no scheme, no port, no path)
- Reject values like `rtsp://192.168.1.50:554/live/` — we derive the full URL
- Regex: `^[a-zA-Z0-9._-]+$` or use `IPAddress.TryParse` + hostname validation

---

## Frontend Integration Points

### EditPrinterModal (`src/Web/ReactApp/src/features/printers/components/EditPrinterModal.tsx`)

Add a new field in the Camera Configuration section:

```
[Buddy Camera IP/Hostname] _______________
[Camera Stream URL]        rtsp://192.168.1.50:554/live/  (read-only, derived)
[Camera Snapshot URL]      (not available)                  (read-only, derived)
[Auto-Detect]              (existing button for PrusaLink cameras)
```

The `BuddyCameraHost` input is editable. The derived URLs update reactively as the user types (client-side preview, server-side is authoritative).

### TypeScript Types (`src/Web/ReactApp/src/types/api.ts`)

```typescript
// Add to Printer/PrinterBase interface
buddyCameraHost?: string;

// Add to UpdatePrinterDto
buddyCameraHost?: string;
```

### Conditional Visibility

Show the Buddy Camera field only when `printer.backend === 'PrusaLink'` (backend enum value 2). Other backends have their own camera discovery mechanisms. This keeps the UI clean for Moonraker/OctoPrint users who don't need this field.

---

## What This Enables for Downstream Beads

| Bead | How This Helps |
|------|---------------|
| **PFarm1-3sbh** (RTSP health probe) | Camera entity has `StreamUrl` with `rtsp://` scheme → health monitor can dispatch RTSP probe |
| **PFarm1-y3n1** (Event snapshots) | Camera entity linked to printer → snapshot service knows which cameras to capture |
| **PFarm1-lzf0** (go2rtc sidecar) | Camera entity has RTSP URL → go2rtc config can be generated from camera registry |

---

## Out of Scope

- **RTSP playback in browser** — That's PFarm1-lzf0 (go2rtc sidecar)
- **RTSP health probing** — That's PFarm1-3sbh
- **Snapshot capture** — That's PFarm1-y3n1
- **Network discovery/scanning** — Manual IP entry is the right MVP; mDNS scanning is a future enhancement
- **go2rtc integration** — Snapshot URL will be null until go2rtc is deployed

---

## Implementation Estimate

| Task | Effort |
|------|--------|
| Schema: Add `BuddyCameraHost` to Printer + migrations | 1h |
| Backend: Camera upsert/delete logic in PrinterService | 2h |
| Backend: Validation + DTO changes | 1h |
| Frontend: EditPrinterModal field + type updates | 2h |
| Tests: Backend upsert/delete + validation | 2h |
| Tests: Frontend field rendering + save | 1h |
| **Total** | **~9h** |


---

# 2026-05-12: Override Lambert Lockout for Camera Review Pass 2 + 3

**Decided by:** Squad (coordinator) — Jeff unavailable, autonomous mode
**Affected protocol:** `.squad/` reviewer-rejection lockout (author cannot revise own rejected work)

## Context
Code review of the Prusa Buddy camera integration (commits `387ac3f..111b35e7e`) went through 4 review passes with Bishop (GPT-5.4), Hicks (Gemini), and Vasquez (Opus 4.7). Pass 2 had unanimous REQUEST_CHANGES with criticals (IPv6 SSRF, FK regression, BuddyCameraIp clear bug). Pass 3 again had 2/3 REQUEST_CHANGES (Bishop/Hicks) for a NEW finding introduced by the pass-2 fix (FK violation on buddy camera clear with snapshots).

Per protocol, after a REQUEST_CHANGES verdict the original author should be locked out from revising. **But Lambert is the only Backend Dev on the 12-agent roster.** The escalation path (escalate to user) was unavailable because Jeff was away and the session was in autopilot mode.

## Decision
**Override the lockout twice (pass 2→3 fix and pass 3→4 fix).** Lambert revised his own work both times.

## Rationale
- Reviewers gave specific code-level fixes (not just "make it safer"). Little room to rationalize.
- Three independent reviewers gate the next pass. Pass 3 actually caught Lambert's pass-2 FK regression — re-review replicates lockout's protection.
- No alternative: single Backend Dev, autonomous mode, sub-task delegation to a non-backend agent would have produced wrong code.

## Outcome
- Pass 4: unanimous APPROVE. FK regression test (`UpdatePrinter_ClearsBuddyCameraIp_WhenCameraHasSnapshots_Succeeds`) pins the fix.
- Final test gate: 2011/2011 Farm.Web.Api.Tests pass.
- Pushed to `origin/feature/orcaslicer-full-ui-parity` at `27d4cf805`.

## Follow-ups (beads)
- PFarm1-qv4v: orphaned snapshot files cleanup
- PFarm1-ibag: BuddyCameraIp IPv6 support
- PFarm1-l2x0: IPv6 SSRF test cases
- PFarm1-3650: BuddyCameraIp DB-state assertions
- PFarm1-rpxd: IServiceScopeFactory non-nullable
- PFarm1-ugx7: extract snapshot pre-delete to shared helper

## Recommendation
Either (1) add a "single-specialist exception" clause to the lockout rule, or (2) hire a second Backend Dev. Jeff to decide on review.

---

# 2026-05-12: go2rtc Deployment Integration

**Author:** Dallas (Lead / Architect)
**Status:** APPROVED
**Impact:** Low (deployment tooling addition, opt-in)

## Question
Does `deploy-docker.sh` need modification to include the go2rtc container, or will it always be deployed?

## Decision: Opt-In Flag (`--include-go2rtc`)
Both `deploy-docker.sh` and `compose-generator.sh` need modification. The go2rtc compose template exists but neither script references it. Follow the established Spoolman/Obico opt-in pattern:

**In `compose-generator.sh`:**
- Add `INCLUDE_GO2RTC="false"` default (~line 221)
- Add `--include-go2rtc)` case to arg parser (~line 256)
- Add `merge_addon_services` block after Obico ML (~line 795)
- Add `--include-go2rtc` to usage help (~line 150)

**In `deploy-docker.sh`:**
- Add `DEPLOY_GO2RTC` / `ENABLE_GO2RTC` env var handling
- Pass `--include-go2rtc` to generator when enabled (~line 857)
- Add CLI flag + help text (~line 2323)

## Rationale
- go2rtc defaults to disabled (`Go2Rtc:Enabled = false`) — deploying the container without enabling wastes resources.
- Not all farms have cameras.
- Every other optional sidecar is opt-in; consistency.
- ~30MB matters on resource-constrained SBCs.

## Effort
~30 minutes. Templates and `merge_addon_services` already exist.

---

# 2026-05-20: Mobile API Drift + Basic Printer Controls v1 — Locked Decisions

**By:** Dallas (Lead/Architect), via Jeff Papiez
**Scope:** iOS mobile app — basic printer controls (preheat, home, jog) + API drift cleanup.

## Locked v1 design
- **Fixed preheat presets** (no user customization v1):
  - PLA: hotend 200°C / bed 60°C
  - PETG: hotend 240°C / bed 80°C
  - ABS: hotend 240°C / bed 100°C
  - Cool Down: hotend 0°C / bed 0°C (both-to-zero)
- **Fixed jog feedrates:** XY 3000 mm/min, Z 600 mm/min
- **Fixed jog step picker:** 0.1 / 1 / 10 / 100 mm
- **Capability gating:** trust backend `PrinterBackendCapabilities.supportsTemperatureControl` flag (e.g. FlashForge bed). No client-side probing spike.
- **Cooldown semantics:** "Cool Down" preset sets both hotend and bed to 0.
- **Auth model:** match existing backend auth. Maintenance toggle still requires `farm_admin` role gate (issue #274).
- **State updates:** no optimistic UI. Wait for next `printerupdated` SignalR event.
- **Section visibility:** hide controls section when `printer.isOnline == false`.
- **Print-state blocking:** block controls client-side when `printing`/`paused`; backend enforcement validated in spike #279.
- **Routing:** human squad only (Hudson / Gorman / Newt / Ripley). No `squad:copilot`.

## GitHub issues created
#274–#289 on OlyForge3D/PrintFarmer. See `.squad/agents/dallas/history.md` for full task→issue mapping.


---

### 2026-05-21: Issue #275 — PrinterService.stop() is not a pure iOS-side alias

**By:** Gorman (iOS Networking) — requested by Jeff
**Status:** Investigation only, no code changes

**What:** iOS `PrinterService.stop(id:)` and `emergencyStop(id:)` call DIFFERENT URLs: `POST /api/printers/{id}/stop` vs `/emergency-stop`. The aliasing is server-side — `PrintersController.StopPrintAsync` is annotated "alias for emergency-stop for frontend compatibility" and forwards to `EmergencyStopAsync`.

**Why it matters:** Per the issue prompt, the iOS `stop()` was assumed to be a thin in-process alias. It isn't. Removing it requires either:
1. Deleting the backend `/stop` alias too (Lambert call), plus the iOS method, the protocol entry, the dedicated test (`testStopCallsCorrectEndpoint`), and updating `PrinterDetailViewModel.swift:429`. Coordinated cleanup.
2. OR keeping `/stop` for web/mobile parity and closing #275 as wontfix.

**Recommendation:** Bounce to Dallas/Lambert to decide whether the `/stop` alias endpoint should be retired. Until then, do not delete the iOS method — it correctly mirrors a real (if redundant) backend route.

**Files referenced:**
- mobile/PrintFarmer/Services/PrinterService.swift:47-51
- mobile/PrintFarmer/Protocols/PrinterServiceProtocol.swift:16-17
- mobile/PrintFarmerTests/Services/PrinterServiceTests.swift (`testStopCallsCorrectEndpoint`)
- mobile/PrintFarmer/ViewModels/PrinterDetailViewModel.swift:429
- src/api/Controllers/PrintersController.cs:2159, 2182-2201


---

# 2026-05-20: iOS Printer.progress decoder — clamp out-of-range backend values

**Issue:** #277 — Add unit test pinning Printer.progress 0–100 contract.

**Decision:** Clamp `progress` to `[0, 100]` at decode time (`Printer.init(from:)` in `mobile/PrintFarmer/Models/Models.swift`) before normalizing to the iOS internal `0.0…1.0` scale. Out-of-range backend payloads (`-5`, `150`) become `0.0` / `1.0` rather than producing `nil` or surfacing the drift to UI.

**Why clamp instead of reject (return `nil`):**

- The mobile app already silently normalizes `progress / 100.0` everywhere (`Printer` decoder, `DashboardViewModel` SignalR path, `PrinterDetailViewModel`, `PrinterListViewModel`). The contract is "iOS holds 0…1.0; backend holds 0…100." Rejecting one out-of-range value would leave the printer card without progress and surface a partial-decode failure to the user, which is worse than showing 0 % or 100 %.
- The PrintFarmer backend `CompletePrinterDto.Progress` is a server-computed `double` derived from g-code line counters; brief overshoots (e.g. `100.4`) and pre-start undershoots (`-0.0`) are observed in production logs. Clamping is the kindest interpretation.
- Aligns with the existing `PrintProgressBar` SwiftUI consumer, which assumes `0…1.0`.

**Dual-scale contract (documented in test header + decoder comment):**

| Layer | Range | Source |
|-------|-------|--------|
| Backend wire (`CompletePrinterDto.Progress`) | `0…100` | `src/api/...` |
| iOS `Printer.progress` (post-decode) | `0.0…1.0` | `mobile/PrintFarmer/Models/Models.swift` |
| SwiftUI consumers (`ProgressView`, `PrintProgressBar`) | `0.0…1.0` | iOS internal |

**Follow-up (out of scope for #277, flagged):**

- SignalR update paths in `DashboardViewModel:50`, `PrinterDetailViewModel:111` & `:141`, `PrinterListViewModel:46` divide by `100.0` without clamping — they should be updated to use the same clamp helper for parity. File a follow-up issue.
- The pre-existing `ModelDecodingTests.testPrinterDecodesFullJSON` asserts `printer.progress == 45.5` against a JSON `progress: 45.5` payload, which is incorrect for the post-decode (normalized) value — left alone since #277 is a pin, not a sweep.

**Validation:**

Local `swift test` cannot run the SPM `PrintFarmerTests` target on macOS because sibling test files / app sources transitively reference `UIKit` (`UIImpactFeedbackGenerator`) and iOS-only SwiftUI APIs (`.page(indexDisplayMode:)`). The local iOS Simulator is also out of date (`CoreSimulator 1051.49.0` vs runtime `1051.54.0`). The new tests are pure `Foundation` + `XCTest` and rely on CI for validation.

**Files:**

- Modified: `mobile/PrintFarmer/Models/Models.swift` (clamp added to `Printer.init(from:)`).
- Added: `mobile/PrintFarmerTests/Models/PrinterProgressContractTests.swift` (8 cases: 0/50/100/fractional/negative/overflow/null/missing).
- Modified: `mobile/PrintFarmer.xcodeproj/project.pbxproj` (registered new test file).


---

### 2026-05-21: Spike #279 verdict — server-side guards for /temps and /move during print

**By:** Ripley
**Issue:** [#279](https://github.com/OlyForge3D/PrintFarmer/issues/279)
**Verdict:** **(c) — DO NOT trust the backend.** iOS client must gate `/temps` and `/move` client-side based on cached `Printer.Status`.

**Findings:**
- Controller (`PrintersController.SetTempsAsync` / `MoveAsync` / `MoveToAsync`) has no state guard — only null-body validation.
- `PrintersService` has no state check; collapses every failure (offline, capability missing, firmware 409, exception) to `bool false` → controller returns 404.
- **Per-backend matrix:**
  - **Moonraker:** sends `M104`/`M140`/`G91 G0` as raw G-code mid-print with no resistance.
  - **PrusaLink:** firmware refuses with 409 mid-print, but plugin reduces to bool — clients can't distinguish.
  - **OctoPrint:** same — firmware 409 collapsed to bool.
  - **FlashForge:** `/temps` flows through; does NOT implement `ISupportsMovement` → `/move` returns 404.
  - **SDCP:** implements neither → both return 404.
- Test coverage: **zero** tests on `/temps` or `/move` paths (verified via coverage report `FNDA:0`).

**Impact for Hudson (#284–#286):**
- iOS controls section MUST disable temp/move controls when status ∈ `{Printing, Pausing, Paused, Resuming, Cancelling, Heating}`.
- Re-evaluate gate on every SignalR `printerupdated`.
- Even with client gating, expect Moonraker to silently accept `/temps` mid-print — operator-visible warning recommended.

**Follow-up filed:** [#290 — Add server-side guards for /temps and /move during print](https://github.com/OlyForge3D/PrintFarmer/issues/290) (P0).

**Comment:** https://github.com/OlyForge3D/PrintFarmer/issues/279#issuecomment-4509132269
