# Spaghetti Detection UI — Phase 1 Design

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-10  
**Status:** PROPOSED

## Problem

Backend has spaghetti detection via Obico ML with SignalR events (`FailureDetectionEvent`). No UI exists to show users when a failure is detected, what the confidence is, or whether the print was auto-paused.

## User Stories (Phase 1 Scope)

1. **As a user monitoring printers, I want to see a visual alert when spaghetti is detected** so I can intervene immediately.
2. **As a user, I want to know the detection confidence level** so I can assess false positives.
3. **As a user, I want to know if the print was auto-paused** so I understand if immediate action is required.
4. **As a user, I want this information visible on the printer card** so I don't miss critical events.

## Design Decisions

### 1. Where Should Status Live?

**Printer Cards (Primary Location)**
- **Compact Card:** Show a prominent inline alert/badge when failure is detected
- **Detailed Card:** Show a more detailed alert panel with confidence, timestamp, and auto-pause status
- **Rationale:** Users monitor printers on the grid/list view. Failure alerts must be visible at a glance without navigation.

**Admin/Settings (Secondary Location — Phase 2)**
- Settings for enabling/disabling auto-pause
- Confidence threshold configuration
- Detection history logs
- **Rationale:** Configuration and history are power-user features. Phase 1 focuses on real-time visibility.

**No Dedicated Page Needed (Phase 1)**
- Events are transient (SignalR only, no persistence yet)
- Grid/list view with inline alerts is sufficient for immediate response
- **Future:** If persistence is added (backend TODO), a dedicated history page makes sense

### 2. States the User Needs to See (Phase 1)

| State | Visual Treatment | Location |
|-------|-----------------|----------|
| **No failure detected** | Normal printer card appearance | Compact & Detailed |
| **Failure detected (printing)** | Prominent warning badge/alert, show confidence | Compact & Detailed |
| **Failure detected (auto-paused)** | Critical error alert, emphasize pause action | Compact & Detailed |
| **Monitoring active** | Subtle badge (existing Obico shield) | Compact & Detailed |

### 3. Component Contract (Phase 1)

#### Compact Printer Card
```tsx
// Add near top of card header (same area as Obico monitoring badge)
{latestFailureEvent && (
  <FailureDetectionBadge
    confidence={latestFailureEvent.confidence}
    autoPaused={latestFailureEvent.autoPaused}
    detectedAt={latestFailureEvent.detectedAt}
    compact={true}
  />
)}
```

#### Detailed Printer Card
```tsx
// Add as prominent alert panel below PrintProgressBar
{latestFailureEvent && (
  <FailureDetectionAlert
    printerName={printer.name}
    confidence={latestFailureEvent.confidence}
    autoPaused={latestFailureEvent.autoPaused}
    detectedAt={latestFailureEvent.detectedAt}
    onDismiss={() => setLatestFailureEvent(null)}
  />
)}
```

### 4. SignalR Event Handling

**Hook Pattern:**
```tsx
// In CompactPrinterCard / DetailedPrinterCard
const [latestFailureEvent, setLatestFailureEvent] = useState<FailureDetectionEvent | null>(null);

useEffect(() => {
  const hub = getFailureDetectionHub(); // New service
  
  hub.on('FailureDetected', (event: FailureDetectionEvent) => {
    if (event.printerId === printer.id) {
      setLatestFailureEvent(event);
      // Toast notification for immediate feedback
      toast.error(`Print failure detected on ${event.printerName} (${event.confidence}% confidence)`, {
        duration: 10000,
      });
    }
  });

  return () => hub.off('FailureDetected');
}, [printer.id]);
```

### 5. Visual Design (Industrial Aesthetic)

**Compact Badge (Inline, Non-Intrusive):**
- Small badge next to printer name
- Warning (yellow) for confidence <80%
- Error (red) for confidence ≥80% or auto-paused
- Icon: AlertTriangleIcon (lucide-react)
- Text: "Failure: 87%" (confidence only)

**Detailed Alert (Full-Width Panel):**
- Alert component (existing UI library)
- Type: `warning` (confidence <80%) or `error` (≥80% or auto-paused)
- Title: "Print Failure Detected"
- Body:
  - Confidence: "87% confidence"
  - Auto-pause status: "Print automatically paused" (if true)
  - Timestamp: "Detected 2 minutes ago"
  - Dismissible (X button) — clears local state only
- Positioned between PrintProgressBar and control sections

**Color Palette:**
- Warning: `bg-pf-warning-bg`, `text-pf-warning-text`, `border-pf-warning`
- Error: `bg-pf-error-bg`, `text-pf-error-text`, `border-pf-error`
- Matches existing PrintFarmer design tokens

### 6. Phase 1 Implementation Checklist

- [ ] Create `FailureDetectionBadge.tsx` (compact inline badge)
- [ ] Create `FailureDetectionAlert.tsx` (detailed alert panel)
- [ ] Create `useFailureDetectionHub.ts` (SignalR hook)
- [ ] Add SignalR event handling to `CompactPrinterCard`
- [ ] Add SignalR event handling to `DetailedPrinterCard`
- [ ] Add toast notifications for immediate feedback
- [ ] Test with backend SignalR events
- [ ] Add Vitest tests for components

### 7. Phase 2 Scope (Future)

- Persistence layer (backend): Store failure events in database
- History page: View all past detections with filtering
- Settings page: Configure auto-pause threshold, enable/disable per-printer
- Enhanced analytics: Failure rate trends, confidence distribution
- Camera snapshot capture at failure detection time
- Actionable buttons: "Resume Print", "View Camera", "Mark False Positive"

## Technical Notes

- **SignalR Hub:** Backend already broadcasts `FailureDetectionEvent` via SignalR
- **API Endpoints:** `/api/failure-detection/status`, `/api/failure-detection/analyze/{printerId}` exist but return minimal data
- **No Persistence Yet:** Events are transient. Phase 1 shows real-time events only. Refreshing page clears state.
- **Existing Obico Badge:** Separate from failure detection. Shows monitoring is active, not failure state.

## Dependencies

- Backend: `FailureDetectionController.cs` (already implemented)
- SignalR: `FailureDetectionEvent` payload (already defined in `api.ts`)
- UI Library: `Badge`, `Alert`, existing design tokens

## Risks & Mitigation

- **False Positives:** Show confidence % so users can assess reliability. Phase 2 adds threshold config.
- **Alert Fatigue:** Only show latest event. Toast notification is dismissible. Phase 2 adds history.
- **No Persistence:** User can't review past events. Phase 2 adds database + history page.

## Approval Checklist

- [ ] UI design reviewed by team
- [ ] Component contracts approved
- [ ] SignalR integration pattern confirmed
- [ ] Phase 1/2 scope boundary clear
