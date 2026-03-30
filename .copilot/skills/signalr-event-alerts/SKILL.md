# SKILL: SignalR Event Alerts

**Category:** Real-time UI Patterns  
**Last Updated:** 2026-03-10  
**Applies To:** React components subscribing to SignalR events for real-time alerts

## Pattern Overview

This skill documents the established pattern for displaying real-time alerts triggered by SignalR events in PrintFarmer's React frontend.

## Core Components

### 1. SignalR Service Hook
```tsx
import { printerSignalR } from '@/services/printer-signalr';

// In component (e.g., CompactPrinterCard, DetailedPrinterCard)
const [latestEvent, setLatestEvent] = useState<EventType | null>(null);

useEffect(() => {
  const unsubscribe = printerSignalR.onEventName((event: EventType) => {
    if (event.printerId === printer.id) { // Filter to relevant printer
      setLatestEvent(event);
      toast.error(`Event occurred: ${event.message}`, { duration: 10000 });
    }
  });

  return unsubscribe; // Cleanup on unmount
}, [printer.id]);
```

### 2. Compact Badge (Inline Indicator)
- Use existing `Badge` component from `@/common/components/ui`
- Position near printer name/header
- Show critical info only (e.g., confidence %, status text)
- Variant: `warning` (<80% confidence) or `error` (≥80% confidence)

**Example:**
```tsx
{latestEvent && (
  <Badge variant={latestEvent.confidence >= 80 ? 'error' : 'warning'} size="sm">
    Failure: {latestEvent.confidence}%
  </Badge>
)}
```

### 3. Detailed Alert Panel
- Use existing `Alert` component from `@/common/components/ui`
- Position below progress indicators, above control sections
- Show full context: confidence, timestamp, auto-action taken
- Dismissible with onDismiss callback

**Example:**
```tsx
{latestEvent && (
  <Alert 
    type={latestEvent.autoPaused ? 'error' : 'warning'}
    title="Event Detected"
    onClose={() => setLatestEvent(null)}
  >
    <div className="space-y-1">
      <div>• Confidence: {latestEvent.confidence}%</div>
      {latestEvent.autoPaused && <div>• Print automatically paused</div>}
      <div>• Detected {formatRelativeTime(latestEvent.detectedAt)}</div>
    </div>
  </Alert>
)}
```

### 4. Toast Notification
- Use `sonner` for immediate feedback
- Duration: 10 seconds (allows user to notice and navigate)
- Keep concise but informative

**Example:**
```tsx
toast.error(`Print failure detected on ${event.printerName} (${event.confidence}% confidence)`, {
  duration: 10000,
});
```

## Key Files

- `/src/Web/ReactApp/src/services/printer-signalr.ts` — SignalR service with event callbacks
- `/src/Web/ReactApp/src/common/components/ui/Badge.tsx` — Inline badge component
- `/src/Web/ReactApp/src/common/components/ui/Alert.tsx` — Full-width alert panel
- `/src/Web/ReactApp/src/types/api.ts` — Event type definitions

## Design Guidelines

### Visual Hierarchy
- Compact badge: Small, inline, non-intrusive (for grid/list view)
- Detailed alert: Full-width, prominent, actionable (for expanded view)
- Toast: Transient, high-priority notification

### Color Semantics
- `warning` (yellow): Medium confidence, non-critical
- `error` (red): High confidence or critical action taken (auto-pause)
- Use existing `pf-warning-*` and `pf-error-*` design tokens

### State Management
- Local state per printer card (`useState`)
- Filter events by `printerId` to avoid cross-contamination
- Dismissible alerts clear local state only (no backend persistence in Phase 1)

### Accessibility
- Alert panels use semantic HTML (`role="alert"` for errors)
- Toast notifications use `sonner` library's built-in a11y
- Dismissible alerts have visible "×" button with `aria-label`

## Common Pitfalls

1. **Forgetting to filter events by printer ID** → Shows alerts for all printers
2. **Not cleaning up subscriptions** → Memory leaks on unmount
3. **Hardcoding thresholds** → Use props/config for reusability
4. **Forgetting toast notifications** → User misses real-time feedback

## When to Use This Pattern

- ✅ Real-time SignalR events requiring immediate user attention
- ✅ Events tied to specific printers or entities
- ✅ Critical alerts that may require user action
- ❌ Background/informational events (use polling or silent state updates)
- ❌ Events requiring complex multi-step workflows (use modal instead)

## Reusable Component Template

Consider creating a generic `<SignalRAlert>` wrapper for future events:

```tsx
interface SignalRAlertProps<T> {
  event: T | null;
  printerId: string;
  onDismiss: () => void;
  renderCompact: (event: T) => React.ReactNode;
  renderDetailed: (event: T) => React.ReactNode;
  mode: 'compact' | 'detailed';
}

export function SignalRAlert<T>({ event, printerId, onDismiss, renderCompact, renderDetailed, mode }: SignalRAlertProps<T>) {
  if (!event) return null;
  return mode === 'compact' ? renderCompact(event) : renderDetailed(event);
}
```

## Related Skills

- **Toast Notification Patterns** (use `sonner`, duration guidelines)
- **State Management in React** (local vs global state)
- **SignalR Integration** (subscription lifecycle, cleanup)

## Examples in Codebase

- Obico monitoring badge (existing): `ShieldIcon` + "ML" text when monitoring active
- Failure detection (planned): Warning/error badge + detailed alert panel
