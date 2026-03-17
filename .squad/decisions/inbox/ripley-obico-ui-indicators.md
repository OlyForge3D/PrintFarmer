# Obico ML Monitoring UI Indicators

**Date:** 2026-03-17  
**Agent:** Ripley (Frontend Developer)  
**Status:** ✅ Implemented & Tested

## Context

The backend already has Obico ML print failure monitoring with:
- `PrintFailureMonitorService` capturing camera frames every 30s during prints
- `FailureDetected` SignalR event broadcast with confidence scores
- `Printer.ObicoServerId` FK indicating which printers are monitored
- Manual analysis endpoint for on-demand checks

The frontend had NO indicators showing:
1. Which printers are actively being monitored
2. When failures are detected by the ML system

## Decision

Implement three UI enhancements for Obico ML monitoring:

### 1. SignalR Event Listener for `FailureDetected`
- Register listener in `App.tsx` during SignalR connection
- Show toast notification immediately when failure detected
- Format: `⚠️ Failure detected on {printerName} (confidence: {X}%)`
- Include auto-pause status in message if applicable
- Use 8-second duration (longer than default) for critical warnings

### 2. "ML" Badge on Printer Cards
- Display shield icon + "ML" badge in both CompactPrinterCard and DetailedPrinterCard
- Show ONLY when printer has `obicoServerId` assigned AND is currently printing
- Position: Header section, after status pill
- Visual: Accent-colored with shield icon, subtle styling to avoid clutter
- Rationale: Badge only appears when monitoring is actively analyzing frames

### 3. TypeScript Type Definition
- Add `FailureDetectionEvent` interface to `api.ts`
- Fields: `printerId`, `printerName`, `jobId?`, `confidence`, `detectedAt`, `autoPaused`
- Matches backend's camelCase SignalR serialization

## Implementation

### Files Modified
1. **types/api.ts** — Added `FailureDetectionEvent` interface
2. **services/printer-signalr.ts** — Added callback type, event handler, subscription method
3. **icons/MdiIcons.tsx** — Added `ShieldIcon` component (mdiShield from @mdi/js)
4. **CompactPrinterCard.tsx** — Added ML badge logic and rendering
5. **DetailedPrinterCard.tsx** — Added ML badge logic and rendering
6. **App.tsx** — Registered failure detection listener with toast handler
7. **test/App.smoke.test.tsx** — Updated mock to include `onFailureDetected` method

### Code Patterns Followed
- SignalR event naming: lowercase `failuredetected` (matches backend convention)
- Toast notifications: sonner library with warning severity
- Badge styling: Tailwind with pf- design tokens, accent color scheme
- Icon integration: MDI icons via @mdi/js package (v7.4.47)
- React Query: No additional hooks needed (SignalR handles real-time updates)

## Alternatives Considered

### Badge Visibility Strategy
- **Rejected:** Show badge whenever printer has `obicoServerId` assigned
- **Chosen:** Show badge only when printer is printing AND has `obicoServerId`
- **Rationale:** Monitoring only actively checks frames during prints, so badge indicates "currently monitoring" not just "configured to monitor"

### Toast Notification Approach
- **Rejected:** In-app notification center with persistence
- **Chosen:** Immediate toast with auto-dismiss
- **Rationale:** Failure detection is time-sensitive — toast provides immediate user attention without requiring separate notification management UI

### Badge Icon
- **Rejected:** Eye icon (mdiEye) — implies "watching" but less clear about protection
- **Rejected:** Alert icon (mdiAlert) — too alarming, badge is informational
- **Chosen:** Shield icon (mdiShield) — clearly conveys monitoring/protection concept
- **Rationale:** Shield icon universally understood as "protected" or "monitored" status

## Testing

- ✅ All 1471 existing tests pass
- ✅ ESLint clean (0 errors)
- ✅ Production build succeeds (7.38s)
- ✅ TypeScript strict mode validation
- ✅ SignalR mock updated for test compatibility

## Notes

- Backend already sends events with proper camelCase serialization
- No API changes needed — all data already present in printer DTOs
- Badge appears/disappears reactively based on printer state updates via SignalR
- Toast is non-blocking and auto-dismisses after 8 seconds
- Works across all printer backends (Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge)

## Future Enhancements (Out of Scope)

1. **Notification History** — Persist failure detection events for later review
2. **Confidence Threshold Settings** — UI for configuring detection sensitivity
3. **Manual Analysis Button** — Quick-access button to trigger on-demand frame analysis
4. **Detection Statistics** — Dashboard showing false positive rate, detection accuracy
5. **Image Preview** — Show the actual frame that triggered the detection
