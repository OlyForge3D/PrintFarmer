---
name: PrintFarmer iOS
description: Native farm-floor companion design system
colors:
  background-light: "#ffffff"
  background-dark: "#0b1020"
  background-secondary-light: "#f8fafc"
  background-secondary-dark: "#0f172a"
  background-tertiary-light: "#f1f5f9"
  background-tertiary-dark: "#111827"
  text-primary-light: "#1e293b"
  text-primary-dark: "#e5e7eb"
  text-secondary-light: "#475569"
  text-secondary-dark: "#9ca3af"
  text-tertiary-light: "#64748b"
  text-tertiary-dark: "#6b7280"
  accent: "#10b981"
  accent-background-light: "#10b981"
  accent-background-dark: "#047857"
  accent-pressed-light: "#059669"
  accent-pressed-dark: "#036b4b"
  secondary-accent: "#1d4ed8"
  border-light: "#e2e8f0"
  border-dark: "#243145"
  success-light: "#059669"
  success-dark: "#10b981"
  error-light: "#dc2626"
  error-dark: "#f87171"
  warning-light: "#b45309"
  warning-dark: "#d97706"
  button-primary: "#047857"
  button-primary-text: "#ffffff"
rounded:
  printer-card: "14pt"
  printer-thumbnail: "10pt"
spacing:
  printer-card-padding: "10pt"
  status-horizontal: "8pt"
  status-vertical: "3pt"
---

## Overview

The native companion supports practical farm-floor work with scannable state,
clear actions and familiar iOS interaction. The descriptive direction
“quiet, utilitarian, data-forward” is an inference from the existing product
brief and implementation, not a separately confirmed brand decision.

The normative values above are extracted from
[`ThemeColors.swift`](PrintFarmer/Theme/ThemeColors.swift),
[`ActionButtonStyle.swift`](PrintFarmer/Views/Components/ActionButtonStyle.swift),
[`StatusBadge.swift`](PrintFarmer/Views/Components/StatusBadge.swift) and
[`PrinterCardView.swift`](PrintFarmer/Views/Components/PrinterCardView.swift).
Use those implementations as the source of truth for semantic roles and
variants; the values are not a replacement SwiftUI theme.

## Colors

Use the adaptive `pfBackground`, `pfCard`, `pfText*` and `pfBorder` roles
for surfaces and text. `pfCard` shares the secondary background pair. The
accent marks primary interaction; the secondary accent and semantic
success, warning and error roles communicate state. Fill and foreground
roles are not interchangeable: `pfErrorFill` and `pfWarningFill` are distinct
from their adaptive foregrounds. Additional hardware, maintenance, assigned
and temperature roles are defined in `ThemeColors.swift`.

Light, dark and system appearance are first-class. Check contrast in each
relevant appearance; a named color does not prove a given pairing passes.
Always pair status color with meaningful text or another non-color cue.

## Typography

Prefer SwiftUI system text styles that respond to Dynamic Type. The existing
status badge uses semibold `caption2`, uppercase text; printer cards use
semibold `subheadline` for names and `caption`/`caption2` for secondary
information. Monospaced digits help progress and time values scan. Do not
infer a fixed type scale or custom font from these component examples.

## Layout

Design for one-handed floor work and interruption. Respect safe areas and
the keyboard, and keep interactive targets at least 44 by 44 points. The
printer card changes its horizontal layout to vertical at accessibility
sizes and uses `ViewThatFits` where content may not fit. Preserve usable
large text, long content and distinct loading, offline and stale states.
**Incumbent limitation:** `PrinterCardView.swift` caps its rendered Dynamic
Type size at `.xxxLarge` when an accessibility size is requested. The
vertical layout does not establish support for larger accessibility text
sizes; this document does not certify that support.

The product defines Farm, Queue and Filament as the three core destinations,
with Scan as an action, not a fourth tab. Regular-width iPad uses a sidebar;
compact navigation and Scan are implemented in
[`ContentView.swift`](PrintFarmer/Views/ContentView.swift). Follow native
navigation and gestures rather than introducing a web-style shell.

## Elevation & Depth

Cards use adaptive surfaces and borders. The Scan control in `ContentView`
uses a black shadow at 0.2 opacity, radius 8 and vertical offset 4 points;
this is an observed component treatment, not a global shadow scale.
Prefer system materials for native bars and sheets.

## Shapes

The printer card uses a rounded rectangle and a bordered rounded thumbnail;
status badges use capsules. The dimensions above describe those components,
not a universal radius scale.

## Components

### Action buttons

`ActionButtonStyle` provides minimum heights for standard and prominent
actions (44 and 50 points respectively) and reduces opacity to 0.7 when
pressed. It does not itself assign
color, width or a corner shape. `FullWidthActionButton` provides an optional
full-width frame. Preserve accessible labels, disabled states and adequate
target width as well as height.

### Status badges and printer cards

`StatusBadge` pairs a semibold text label with a translucent semantic color
and exposes a status accessibility label. Printer cards expose a combined
accessibility label and distinguish read-only cached state with a hint.
Retain meaningful status and offline information, not color alone.

### Navigation

Use native SwiftUI navigation, platform controls and SF Symbols where
appropriate. Preserve the system back gesture, safe-area behavior and
VoiceOver labels. Product navigation and scope are specified in
[`PRODUCT.md`](PRODUCT.md); do not add a competing analytical shell.

## Cross-surface conventions

Follow the shared [cross-surface contract](../src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md#cross-surface-conventions):
preserve incumbent glyphs unless a redesign is explicitly scoped; show heater
and status activity with semantic color plus meaningful non-color information;
keep secondary explanations in accessible disclosure rather than extra inline
labels; preserve behavior between table/card views and web/native clients.
Essential status, errors, readings, units and field labels stay visible.

Adapt explanations to touch and VoiceOver; a hover tooltip is not a native
interaction requirement. Preserve native navigation and gestures, and the
same action meaning, availability, permissions and outcomes as web.
`TemperatureView.swift` currently colors a positive target red for hotend and
blue for bed, otherwise reducing opacity to 35 percent. Web heat glyphs use
finite current-temperature thresholds instead. This is an incumbent semantic
difference, not proven parity or permission to change heater behavior.

Misleading activity, clipped readings or inaccessible explanations falsify
the design. Operators and assistive-technology users could be harmed; wrong
hardware actions can have irreversible effects. Validate with isolated mock
state, never real heating or motion.

## Do's and Don'ts

- **Do** use semantic adaptive colors and verify both appearances.
- **Do** preserve Dynamic Type, VoiceOver, Reduce Motion and 44-point targets.
- **Do** show honest offline, stale, unavailable and error states.
- **Don't** use color alone to communicate status.
- **Don't** replace native controls or gestures merely for visual novelty.
- **Don't** treat an incumbent example as proof of contrast or device behavior;
  validate the affected surface.
