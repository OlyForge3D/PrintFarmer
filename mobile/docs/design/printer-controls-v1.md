# Printer Controls v1 — UX Spec

**Status:** v1 locked
**Issue:** #283
**Implementers:** #284 (Preheat), #285 (Home), #286 (Jog)
**Owner:** Newt (UX) → Hudson (iOS)
**Last updated:** 2026-05-21

This spec defines the visual hierarchy, component anatomy, interaction model, accessibility, and edge cases for the **Printer Controls** section that lives inside `PrinterDetailView`. Three subgroups in fixed order: **Preheat → Home → Jog**.

---

## 1. Visual Hierarchy

The section sits inside `PrinterDetailView` as a single SwiftUI `VStack` with three `GroupBox`-style subgroups separated by 16pt vertical spacing. The whole section is **conditionally rendered** — when `printer.isOnline == false`, the entire `ControlsSection` view returns `EmptyView()`. No placeholder, no greyed shell.

```
PrinterDetailView (existing)
├── Status header (existing)
├── Camera preview (existing)
├── Controls Section ← NEW (this spec)
│   ├── Section header  "Controls"  .title3 .semibold  pfTextPrimary
│   ├── Mid-print lockout banner (only when state == .printing | .paused)
│   ├── PreheatSubgroup
│   ├── Divider (Color.pfBorder, 1pt)
│   ├── HomeSubgroup
│   ├── Divider (Color.pfBorder, 1pt)
│   └── JogSubgroup
├── Predictive insights (existing)
└── Auto dispatch (existing)
```

**Spacing tokens (reuse existing iOS patterns):**

| Slot | Value | Notes |
| --- | --- | --- |
| Section outer padding | 16pt horizontal, 12pt vertical | Matches existing `PrinterDetailView` cards |
| Inter-subgroup gap | 16pt | `VStack(spacing: 16)` |
| Subgroup header → controls | 8pt | |
| Inter-control gap (within a subgroup) | 8pt | Matches `ActionButtonStyle` stack convention |
| Divider thickness | 1pt | `Color.pfBorder` |

**Typography (existing tokens):**

| Element | Token |
| --- | --- |
| Section header "Controls" | `.title3.weight(.semibold)` `.pfTextPrimary` |
| Subgroup label (Preheat / Home / Jog) | `.headline` `.pfTextPrimary` |
| Helper text (e.g., "Disabled while printing") | `.footnote` `.pfTextSecondary` |
| Button label | `.subheadline.weight(.medium)` |
| Inline values (temps, step) | `.subheadline.monospacedDigit()` |

**Color tokens (from `Color+Pf` in `Theme/ThemeColors.swift`):**

| Purpose | Token |
| --- | --- |
| Subgroup container background | `Color.pfCard` |
| Container border | `Color.pfBorder` |
| Primary CTA fill | `Color.pfButtonPrimary` |
| Primary CTA text | `Color.pfButtonPrimaryText` |
| Secondary action fill | `Color.pfBackgroundTertiary` |
| Secondary action text | `Color.pfTextPrimary` |
| Pending tint | `Color.pfAssigned` (teal) |
| Error banner fill | `Color.pfError.opacity(0.12)` |
| Error banner stroke / icon / text | `Color.pfError` |
| Disabled overlay | `Color.pfTextTertiary.opacity(0.30)` plus pattern (see §2.4) |
| Hot bed/hotend cue (Cool Down) | `Color.pfSecondaryAccent` |

**Iconography (SF Symbols only — no MDI on iOS):**

| Action | SF Symbol |
| --- | --- |
| Preheat (PLA / PETG / ABS) | `thermometer.high` |
| Cool Down | `thermometer.snowflake` |
| Home All | `house.fill` |
| Home XY | `move.3d` (fallback `arrow.up.left.and.arrow.down.right`) |
| Home Z | `arrow.up.and.down` |
| Jog + | `plus.circle.fill` |
| Jog − | `minus.circle.fill` |
| Pending | `progressView` (SwiftUI built-in spinner) |
| Error | `exclamationmark.triangle.fill` |
| Capability missing | (no icon — control is hidden, not flagged) |

---

## 2. Component Anatomy

### 2.1 Phone (single column)

```
┌─────────────────────────────────────┐
│ Controls                            │ ← .title3.semibold
├─────────────────────────────────────┤
│ ⓘ Controls disabled while printing. │ ← lockout banner (only when state==printing|paused)
├─────────────────────────────────────┤
│ Preheat                             │
│ ┌──────────────┐  ┌──────────────┐ │
│ │ 🌡 PLA       │  │ 🌡 PETG      │ │  ← 2-column grid, equal width
│ │ 200° / 60°   │  │ 240° / 80°   │ │
│ └──────────────┘  └──────────────┘ │
│ ┌──────────────┐  ┌──────────────┐ │
│ │ 🌡 ABS       │  │ ❄ Cool Down  │ │
│ │ 240° / 100°  │  │ 0° / 0°      │ │
│ └──────────────┘  └──────────────┘ │
├─────────────────────────────────────┤
│ Home                                │
│ ┌─────────────────────────────────┐ │
│ │ 🏠 Home All                     │ │  ← full-width prominent
│ └─────────────────────────────────┘ │
│ ┌──────────────┐  ┌──────────────┐ │
│ │ ⤢ Home XY    │  │ ↕ Home Z     │ │  ← 2-column standard
│ └──────────────┘  └──────────────┘ │
├─────────────────────────────────────┤
│ Jog                                 │
│ Axis  [ X ][ Y ][ Z ]               │  ← segmented Picker
│ Step  [0.1][ 1 ][10 ][100] mm       │  ← segmented Picker
│ ┌──────────────┐  ┌──────────────┐ │
│ │     −        │  │      +       │ │  ← 60pt height, side-by-side
│ └──────────────┘  └──────────────┘ │
└─────────────────────────────────────┘
```

### 2.2 iPad (≥ regular width — `horizontalSizeClass == .regular`)

Two columns:

```
┌─────────────────────────────────┬─────────────────────────────────┐
│ Preheat                         │ Home                            │
│ ┌──────┬──────┬──────┬────────┐ │ ┌─────────────────────────────┐ │
│ │ PLA  │ PETG │ ABS  │ Cool   │ │ │   🏠 Home All               │ │
│ └──────┴──────┴──────┴────────┘ │ └─────────────────────────────┘ │
│  (4-up row, all visible)        │ ┌──────────────┬──────────────┐ │
│                                 │ │ Home XY      │ Home Z       │ │
│                                 │ └──────────────┴──────────────┘ │
├─────────────────────────────────┴─────────────────────────────────┤
│ Jog (full width)                                                  │
│ Axis [ X ][ Y ][ Z ]    Step [0.1][1][10][100] mm                 │
│ ┌────────────────────────┬────────────────────────┐               │
│ │           −            │           +            │               │
│ └────────────────────────┴────────────────────────┘               │
└───────────────────────────────────────────────────────────────────┘
```

Use `ViewThatFits` or `horizontalSizeClass` to switch layouts. No new breakpoints introduced.

### 2.3 Subgroup specifications

#### Preheat

- Four buttons, fixed order: **PLA, PETG, ABS, Cool Down**.
- Each button shows: icon, material label (`.subheadline.weight(.medium)`), temperatures `H°/B°` (`.caption.monospacedDigit()`).
- Cool Down uses `pfSecondaryAccent` tint for icon + label to differentiate from heat actions.
- Tap → calls `PrinterService.setTemperatures(printerId:hotend:bed:)` with the locked preset values.
- Buttons are `.standard` (44pt) height. Phone: 2×2 grid. iPad: 1×4 row.
- **No custom temp input. No long-press. No swipe.**

#### Home

- Three buttons in fixed order: **Home All, Home XY, Home Z**.
- Home All is `.prominent` (50pt) and full-width — it is the primary action.
- Home XY and Home Z are `.standard` (44pt), 2-up row.
- Tap → `PrinterService.home(printerId:axes:)` with `["X","Y","Z"]`, `["X","Y"]`, or `["Z"]`.

#### Jog

- Axis picker: SwiftUI `Picker(.segmented)` with X / Y / Z. Default selection: `X`.
- Step picker: segmented picker with `0.1`, `1`, `10`, `100` (display label `mm` outside the picker). Default: `1`.
- `−` and `+` buttons: 60pt height (taller than `.prominent`) because they are the most-tapped controls and benefit from generous targets. Side-by-side, equal width, 8pt gap.
- Tap `+` → `move(printerId, axis: selectedAxis, distanceMm: +selectedStep, feedrateMmMin: feedrate)`.
- Tap `−` → same with negated distance.
- Feedrate is selected by axis: 3000 for X/Y, 600 for Z. Caller-side constants — never shown in UI.

### 2.4 State variants per control

Every button supports five states. Visual treatment:

| State | Visual | Interaction |
| --- | --- | --- |
| **Default** | Full-color fill, label and icon at full opacity | Enabled, accepts taps |
| **Disabled (mid-print)** | Greyscale fill (`pfBackgroundTertiary`), label at 50% opacity, **diagonal stripe pattern** at 8% opacity overlay (color-blind cue per #15) | Not tappable; tap is swallowed silently. Lockout banner explains why. |
| **Capability missing** | **Control is removed from the layout entirely.** | n/a — graceful absence, no greyed slot, no tooltip. Surrounding controls reflow. |
| **Pending** | Label hidden, `ProgressView()` (small) centered. Button stays at full size. Tint: `pfAssigned`. Border thickens to 1.5pt. | Not tappable. All sibling buttons in the subgroup also disable for the duration to prevent burst-spam. |
| **Error** | Reverts to default appearance, but a 1.5pt `pfError` border is applied for 4 seconds, plus an inline banner appears below the subgroup. | Tappable (retry by tapping the same control again, or tap "Retry" in the banner). |

The **diagonal stripe pattern** on disabled state is the color-blind cue called out by #15. Implement as a subtle `LinearGradient` or `Canvas` overlay with 8% white-on-charcoal stripes at 45°. Greyscale alone is not enough — printing red/green colorblind users can mistake greyed for active.

---

## 3. Interaction Model

### 3.1 Single-flight queue (per subgroup)

Each subgroup has its own **single-flight in-flight slot**. While one command from a subgroup is pending, all other buttons in *that subgroup* are disabled. Other subgroups remain interactive. This prevents "Preheat PLA + Preheat ABS" stacked commands without freezing the whole panel.

Example: tapping **Preheat PLA** disables PETG/ABS/Cool Down until the command resolves; Home and Jog remain live.

**Why per-subgroup, not global?** Operators commonly preheat while jogging the bed for tramming. Global locking would feel slower than the printer.

### 3.2 Lifecycle (per command)

```
   tap
    │
    ▼
[ debounce 250ms ]   ← swallow accidental double-taps
    │
    ▼
[ optimistic? NO — UI does not show the new temp/position ]
    │
    ▼
[ button → Pending state, sibling buttons in subgroup disabled ]
    │
    ▼
[ POST /api/printers/{id}/{temps|home|move} ]
    │
    ├── 4xx/5xx ────────► [ Error state + banner + auto-clear pending ]
    ├── network failure ─► [ Error state + banner ]
    └── 200 OK
         │
         ▼
   [ wait for printerupdated SignalR event matching printerId ]
         │
         ├── event arrives within 5s ────► [ Pending → Default, banner clears ]
         └── timeout (5s, no event) ─────► [ Pending → Default with subtle toast
                                            "Sent. Awaiting printer." — not an error,
                                            because the HTTP call succeeded ]
```

**Debounce window:** 250ms (single trailing-edge debounce). Below 250ms feels sticky on iOS 17 button presses; above 400ms users start re-tapping.

**Pending → Default transition:** crossfade 150ms.

**Pending timeout:** 5 seconds. After 5s with no `printerupdated`, the button returns to Default and a non-blocking toast says "Sent. Awaiting printer." This is **not** an error — the API accepted the command, the printer just hasn't echoed state yet.

### 3.3 Error banner

Position: **directly below the affected subgroup**, full width, slides down 200ms. Anchored to the subgroup so the operator always sees which command failed.

```
┌─────────────────────────────────────┐
│ Home                                │
│ [Home All] [Home XY] [Home Z]       │
│ ┌───────────────────────────────┐   │
│ │ ⚠ Home Z failed: printer busy │ ← .pfError.opacity(0.12) fill,
│ │   [ Retry ]              [ × ]│    1pt .pfError stroke
│ └───────────────────────────────┘   │
└─────────────────────────────────────┘
```

- Banner shows the user-friendly server error (truncated to 80 chars; full text via VoiceOver).
- `Retry` re-issues the same command with the same payload.
- `×` dismisses the banner without retrying.
- Banner auto-dismisses after **8 seconds** if untouched.
- Only **one banner per subgroup** at a time; a new error replaces the previous.

### 3.4 Mid-print lockout

When `printer.state` is `.printing` or `.paused`:

- A single banner appears at the top of the section (above Preheat):
  ```
  ⓘ Controls disabled while printing. Pause and stop the job to regain control.
  ```
  Tone: `.pfWarning.opacity(0.10)` fill, `.pfWarning` icon, `.pfTextPrimary` body.
- All buttons enter **Disabled** visual state (greyscale + stripe pattern).
- Taps are absorbed silently — no toast spam.
- VoiceOver announces "Controls locked" once on focus entry.

The mid-print state is read from the same `printer.state` already wired into `PrinterDetailView`.

### 3.5 Capability gating

Capability is provided per printer (e.g., FlashForge omits `bedTemp`). When a capability is missing, the control is **removed from the layout**, not disabled. Implementation:

```swift
if printer.capabilities.contains(.bedTemp) {
    PreheatButton(.pla, hotend: 200, bed: 60)
}
```

If the entire **Preheat** subgroup loses all capabilities (no hotend, no bed), the Preheat header and its container are also hidden — surrounding subgroups reflow. The same logic applies to Home and Jog.

If all three subgroups are empty, the whole Controls section hides (same as offline). This is the only case where a printer is online but Controls is invisible — extremely rare and acceptable.

---

## 4. Accessibility

All controls must satisfy:

- **Touch target ≥ 44×44pt.** Already enforced by `ActionButtonStyle.standard` / `.prominent`. Jog `±` use 60pt.
- **Dynamic Type.** All labels use system text styles (`.subheadline`, `.caption`, etc.). At `.accessibility5`, the 2×2 Preheat grid collapses to a single column (1×4) via `ViewThatFits`.
- **VoiceOver labels and hints** on every control.
- **Color contrast ≥ 4.5:1** for text, ≥ 3:1 for icon-only. The dark theme `pfButtonPrimary` (#047857) on `pfButtonPrimaryText` (#fff) measures 4.6:1 — passes.
- **Reduce Motion** honored: pending crossfade and banner slide become instant when `accessibilityReduceMotion == true`.

### 4.1 VoiceOver script per control

| Control | Label | Hint | Traits | State announcement |
| --- | --- | --- | --- | --- |
| Preheat PLA | "Preheat for PLA" | "Sets hotend to 200 degrees, bed to 60 degrees." | `.button` | "Pending" / "Failed: <reason>. Double-tap to retry." |
| Preheat PETG | "Preheat for PETG" | "Sets hotend to 240 degrees, bed to 80 degrees." | `.button` | (same pattern) |
| Preheat ABS | "Preheat for ABS" | "Sets hotend to 240 degrees, bed to 100 degrees." | `.button` | (same pattern) |
| Cool Down | "Cool down" | "Sets hotend and bed to 0 degrees." | `.button` | (same pattern) |
| Home All | "Home all axes" | "Homes X, Y, and Z." | `.button` | (same pattern) |
| Home XY | "Home X and Y" | "Homes X and Y axes only." | `.button` | (same pattern) |
| Home Z | "Home Z" | "Homes Z axis only." | `.button` | (same pattern) |
| Axis picker | "Jog axis" | "Choose X, Y, or Z axis to move." | (segmented `Picker` defaults) | Reads selected value |
| Step picker | "Jog step distance" | "Choose how many millimeters each tap moves." | (segmented `Picker` defaults) | Reads selected value with "millimeters" suffix |
| `+` button | "Jog forward" | "Moves <axis> positive <step> millimeters." (label is dynamic) | `.button` | (same pattern) |
| `−` button | "Jog backward" | "Moves <axis> negative <step> millimeters." | `.button` | (same pattern) |
| Lockout banner | "Controls locked while printing" | "Pause and stop the job to regain control." | `.staticText`, `.updatesFrequently` removed | Read once on focus |
| Error banner | "Error: <full server message>" | "Double-tap Retry to send the command again." | `.staticText` + adjacent `.button` for Retry | Read on appearance |

**Disabled controls** keep their label, append "disabled" trait, and the hint changes to "Disabled while printing."

**Hidden controls** (capability missing) are not in the accessibility tree — they don't read at all.

### 4.2 Focus order

VoiceOver swipe order: section header → lockout banner (if any) → Preheat header → 4 preheat buttons → Home header → 3 home buttons → Jog header → axis picker → step picker → `−` → `+` → error banner (if any).

---

## 5. Edge Cases

| Case | Behavior |
| --- | --- |
| Printer goes offline mid-pending | Pending button reverts to Default. Toast: "Connection lost." Section hides on next render once `isOnline == false`. |
| Printer transitions idle → printing while user is in Jog | All Jog controls flip to Disabled with stripe overlay; lockout banner slides in. Pending Jog command (if any) shows error "Job started — control released." |
| `printerupdated` event arrives but temps don't match request (printer rejected) | Treat as silent success — the operator may have changed presets manually; we don't fight the printer's reality. No banner. |
| Two operators control same printer | Server is single source of truth via `printerupdated`. UI only reflects events; no client-side merge logic. |
| User taps `+` 10 times quickly | First tap → Pending; taps 2–10 are debounced/dropped. After Pending clears, taps resume. **No queueing of jog deltas in v1.** |
| Capability list empty | Subgroup hidden. If all three subgroups empty, whole section hidden. |
| Section opens while command still pending from a previous detail-view visit | Pending state restored from in-memory `ControlsViewModel`. If app was backgrounded > 60s, pending is cleared (assume timed out). |
| Network 401 / token expired | Standard app-wide auth interceptor handles this; banner shows "Sign in again" with deep link to settings. |

---

## 6. SwiftUI Skeleton (for Hudson)

This is structure only — no business logic. Use existing services and view models.

```swift
struct PrinterControlsSection: View {
    @ObservedObject var vm: ControlsViewModel
    let printer: Printer

    var body: some View {
        guard printer.isOnline else { return AnyView(EmptyView()) }
        return AnyView(
            VStack(alignment: .leading, spacing: 16) {
                Text("Controls")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(Color.pfTextPrimary)

                if printer.isLocked {
                    LockoutBanner()
                }

                PreheatSubgroup(vm: vm, capabilities: printer.capabilities)
                Divider().background(Color.pfBorder)
                HomeSubgroup(vm: vm, capabilities: printer.capabilities)
                Divider().background(Color.pfBorder)
                JogSubgroup(vm: vm, capabilities: printer.capabilities)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .background(Color.pfCard)
        )
    }
}

struct PreheatSubgroup: View {
    @ObservedObject var vm: ControlsViewModel
    let capabilities: PrinterCapabilities

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Preheat").font(.headline).foregroundStyle(Color.pfTextPrimary)
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 8),
                                 GridItem(.flexible(), spacing: 8)],
                      spacing: 8) {
                if capabilities.contains(.hotend) || capabilities.contains(.bed) {
                    PreheatButton(preset: .pla, vm: vm)
                    PreheatButton(preset: .petg, vm: vm)
                    PreheatButton(preset: .abs, vm: vm)
                    PreheatButton(preset: .coolDown, vm: vm)
                }
            }
            if let err = vm.preheatError { ErrorBanner(error: err, onRetry: vm.retryPreheat) }
        }
    }
}

struct JogSubgroup: View {
    @ObservedObject var vm: ControlsViewModel
    let capabilities: PrinterCapabilities

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Jog").font(.headline).foregroundStyle(Color.pfTextPrimary)
            Picker("Jog axis", selection: $vm.axis) {
                ForEach(JogAxis.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)

            Picker("Jog step distance", selection: $vm.step) {
                ForEach(JogStep.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)

            HStack(spacing: 8) {
                JogButton(direction: .negative, vm: vm)
                JogButton(direction: .positive, vm: vm)
            }
            .frame(minHeight: 60)

            if let err = vm.jogError { ErrorBanner(error: err, onRetry: vm.retryJog) }
        }
    }
}
```

**Helper enums** (Hudson decides exact ownership — model layer or view-local):

```swift
enum PreheatPreset { case pla, petg, abs, coolDown
    var hotend: Double { ... }   // 200, 240, 240, 0
    var bed: Double { ... }      // 60, 80, 100, 0
    var label: String { ... }
}
enum JogAxis: String, CaseIterable, Identifiable { case x, y, z; var id: String { rawValue } }
enum JogStep: Double, CaseIterable, Identifiable { case p1 = 0.1, one = 1, ten = 10, hundred = 100; var id: Double { rawValue } }
```

---

## 7. Design tokens — quick reference

| Token | Value (dark) | Used for |
| --- | --- | --- |
| `pfCard` | `#0f172a` | Section + subgroup container |
| `pfBackgroundTertiary` | `#111827` | Disabled fill, secondary button fill |
| `pfBorder` | `#243145` | Divider, container border |
| `pfButtonPrimary` | `#047857` | Home All, primary CTAs |
| `pfButtonPrimaryText` | `#ffffff` | Primary CTA text |
| `pfTextPrimary` | `#e5e7eb` | Labels |
| `pfTextSecondary` | `#9ca3af` | Helper text, temp values |
| `pfTextTertiary` | `#6b7280` | Disabled text base |
| `pfAssigned` | `#22d3ee` | Pending tint |
| `pfError` | `#dc2626` | Error border, banner stroke |
| `pfWarning` | `#d97706` | Lockout banner |
| `pfSecondaryAccent` | `#1d4ed8` | Cool Down accent |

All listed tokens already exist in `mobile/PrintFarmer/Theme/ThemeColors.swift` — **no new tokens introduced**.

---

## 8. Open questions for follow-up issues

These are out of scope for v1 but worth filing now:

- Custom temperature input (deferred — see #283 "Out of Scope").
- Hold-to-jog (long-press auto-repeat) — would need feedrate UX and runaway protection.
- Macro buttons (e.g., "Tram bed", "Belt test") — separate epic.
- iPad-specific large-jog gesture controls — separate epic.

---

**Implementation handoff:** Hudson can build #284 (Preheat), #285 (Home), #286 (Jog) directly from §2, §3, §4, and §6. The `ControlsViewModel` shape is implied but Hudson owns its API.
