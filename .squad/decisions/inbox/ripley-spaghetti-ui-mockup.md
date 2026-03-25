# Spaghetti Detection UI — Visual Mockup

## Compact Printer Card (Grid View)

```
┌──────────────────────────────────────┐
│ ┌────────────────────────────────┐   │
│ │ [Printer Name]  [Printing]  🛡️│   │  ← Existing: name, state, Obico shield
│ │                  [⚠️ Failure: 87%] │  ← NEW: Inline failure badge
│ └────────────────────────────────┘   │
│                                      │
│ [Camera Feed Thumbnail]               │
│                                      │
│ [Progress Bar: 45%]                   │
│ [Job Name: complex_part.gcode]        │
│                                      │
│ [Expand] [History] [Files]           │
└──────────────────────────────────────┘
```

**Badge Variants:**
- ⚠️ Yellow/Warning: Confidence <80% → `bg-pf-warning-bg text-pf-warning-text`
- 🔴 Red/Error: Confidence ≥80% or auto-paused → `bg-pf-error-bg text-pf-error-text`

## Detailed Printer Card (Expanded View)

```
┌────────────────────────────────────────────────────────┐
│ ┌──────────────────────────────────────────────────┐   │
│ │ [Printer Name]  [Printing]  🛡️                   │   │
│ └──────────────────────────────────────────────────┘   │
│                                                        │
│ [Action Bar: Pause | Cancel | Emergency Stop]          │
│                                                        │
│ ┌─────────────────────────────────────────────────┐  │
│ │ 🔴 Print Failure Detected                    [×]│  │  ← NEW: Alert panel
│ │                                                  │  │
│ │ • Confidence: 87%                                │  │
│ │ • Print automatically paused                     │  │
│ │ • Detected 2 minutes ago                         │  │
│ └─────────────────────────────────────────────────┘  │
│                                                        │
│ [Progress Bar: 45%]                                    │
│ [Job: complex_part.gcode]                              │
│                                                        │
│ [Camera Feed (if available)]                           │
│                                                        │
│ [Temperature Controls]                                 │
│ [Movement Controls]                                    │
│ [Filament Controls]                                    │
└────────────────────────────────────────────────────────┘
```

**Alert Panel Variants:**
- **Warning** (Confidence <80%):
  - `type="warning"`
  - Title: "Print Failure Detected"
  - Body: "• Confidence: 72%\n• Detected 30 seconds ago"
  - Border: `border-pf-warning`

- **Error** (Confidence ≥80% OR auto-paused):
  - `type="error"`
  - Title: "Print Failure Detected"
  - Body: "• Confidence: 87%\n• Print automatically paused\n• Detected 2 minutes ago"
  - Border: `border-pf-error`

## Toast Notification (Immediate Feedback)

When failure is detected, a toast appears:

```
┌──────────────────────────────────────────────┐
│ 🔴 Print failure detected on Printer A       │
│    87% confidence                            │
└──────────────────────────────────────────────┘
```

Duration: 10 seconds (allows user to notice and navigate)

## Industrial Aesthetic Alignment

**Color Palette:**
- Warning: Yellow/amber tones matching PrintFarmer's warning system
- Error: Red tones matching critical alerts
- Consistent with existing `pf-warning-*` and `pf-error-*` design tokens

**Typography:**
- Header: `font-bebas uppercase tracking-wide` (existing printer card style)
- Badge text: `text-xs font-medium` (compact, scannable)
- Alert body: `text-sm` (readable, informative)

**Spacing:**
- Badges: `px-1.5 py-0.5` (tight, inline)
- Alerts: `p-3` (generous, prominent)
- Consistent with existing UI library components

**Icons:**
- AlertTriangleIcon (lucide-react) for compact badge
- ShieldIcon continues to indicate Obico monitoring is active (separate concern)

## Phase 2 Enhancements (Future)

1. **Camera Snapshot Capture:** Show captured image at failure time
2. **Actionable Buttons:** "Resume Print", "View Camera", "Mark False Positive"
3. **Confidence Threshold Slider:** User-configurable in settings
4. **History Timeline:** Vertical timeline of all detections with thumbnails
5. **Analytics Dashboard:** Failure rate trends, confidence distribution charts
