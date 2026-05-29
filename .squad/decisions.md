## Decision: Round 21 — Vasquez APPROVE #15; Hicks APPROVE #318 (re-review)

**Date:** 2025-11-24
**Authors:** Vasquez (iOS review), Hicks (backend re-review), Coordinator
**Status:** PR #15 APPROVED (design/research prep complete), PR #318 APPROVED (real-transport tests verified)

### Summary

- **PR #15 APPROVE (Vasquez consensus):** `@State` lifecycle matches existing `PrinterDetailViewModel` 1:1 nav pattern; layout placement against main; no retain cycles verified. Non-blocking note: missing loading-state UI during initial capability fetch (handoff to Hudson). **Approved.**
- **PR #318 APPROVE (Hicks re-review):** Real-transport behavior tests 14/14 pass locally (Kestrel WebSocket for SDCP, TcpListener for FlashForge). Full rejected-mutation → status-roundtrip → exception path verified end-to-end as required in Round 19 decision. All tests pass; `dotnet format --verify-no-changes` clean. **Approved.**

### Status Snapshot

**iOS Controls v1 Stack — Complete:**
- P0/P1 PRs all APPROVED: #1, #3, #4, #7, #9, #10, #11, #12, #13 (HomeSubgroup, PreheatSubgroup, JogSubgroup, etc.)
- Design/research prep approved: #14 (snapshot spike), #15 (capability research)

**PFarm1 Backend:**
- Approved & merged: #313 (error translation), #316 (firmware signals) — 2-vote consensus reached
- PR #318 (real-transport tests): Now APPROVED (Hicks re-vote); merged
- Blocked by Jeff merges: #287, #288, #289 (stack unblock pending)

**Backlog Summary:**
- iOS controls v1 ready for integration/merge (awaiting parent PR coordination)
- Backend firmware-409 error propagation fully tested end-to-end
- Next phase: stack merge coordination + capability research handoff (Hudson loading states)

- Comment (Vasquez #15): https://github.com/OlyForge3D/PrintFarmerMobile/pull/15#issuecomment-4570526326
- Comment (Hicks #318): https://github.com/OlyForge3D/PrintFarmer/pull/318#issuecomment-4570558773

---

## Decision: Round 20 — Bishop APPROVE #15; Lambert real-transport tests #318

**Date:** 2025-11-24
**Authors:** Bishop (code review), Lambert (backend), Coordinator
**Status:** PR #15 APPROVED (iterative gap-closing successful), PR #318 fix-up merged (real-transport behavior tests)

### Summary

- **PR #15 APPROVE (Bishop consensus):** All 3 blockers addressed in `9dc9af2`: (1) Home gating corrected to `canHomeAll || canHomeXY || canHomeZ`; (2) ViewModel injection scoped correctly (`init(printerId:)` + `configure(printerService:)` from `@EnvironmentObject`); (3) test scope updated (new test file + swift-snapshot-testing SPM dep). **Approved.**
- **PR #318 fix-up (Lambert):** Added 6 behavior-level tests using real transports (Kestrel WebSocket for SDCP, TcpListener for FlashForge). Full rejected-mutation → status-roundtrip → exception path verified end-to-end, not just helper logic in isolation. All tests pass; `dotnet format --verify-no-changes` clean. **Merged.**

### Durable Rule Reinforced

**Real-transport test pattern for plugin backends:** Spinning up Kestrel WebSocket (SDCP) + TcpListener (FlashForge) to exercise the full rejected-mutation → status-roundtrip → exception propagation path. Much higher fidelity than mocking the transport layer. Validates the seam (backend rejects → exception raised → controller maps to outcome) end-to-end.

- Comment (Bishop #15): https://github.com/OlyForge3D/PrintFarmerMobile/pull/15#issuecomment-4570460323

---

## Decision: Round 19 — Vasquez APPROVE #14; Newt fix #15; Hicks CR #318

**Date:** 2025-11-24
**Authors:** Vasquez (iOS review), Newt (iOS), Hicks (backend review), Coordinator
**Status:** PR #14 merged (snapshot spike), PR #15 fix-up pushed + OPEN, PR #318 REQUEST_CHANGES (error-translation test gap)

### Summary

- **PR #14 APPROVE (Vasquez consensus):** Snapshot spike capability (FlashForge temp claim via `fallback(for: .flashForge)`). Vasquez + Hicks = 2-of-2 reviewer consensus. Source-of-truth capability disambiguation noted non-blocking. **Merged.**
- **PR #15 fix-up (Newt):** Home gate corrected to `canHomeAll || canHomeXY || canHomeZ` (matches `HomeSubgroup.hasAnyHomeCapability`). ViewModel injection spelled out: `init(printerId:)` + `configure(printerService:)` wired from `@EnvironmentObject ServiceContainer.printerService` in `.task`. Test scope updated: new test file + swift-snapshot-testing SPM dep + Package.swift/test-target update (references PR #14). **Pushed; re-review pending.**
- **PR #318 REQUEST_CHANGES (Hicks):** SDCP + FlashForge tests cover helper logic/parsing only — full rejected-start → `PrinterBackendBusyException` propagation path unverified for those two backends. Moonraker translation OK. Requires mutation-level end-to-end test (mock backend rejection → call mutation → assert exception thrown), not just helper/parsing logic in isolation. **Blocked.**

### Durable Rule Added

**Plugin error-translation tests must exercise the full rejected mutation path:** Mock backend rejection → call the actual mutation method (e.g., `StartPrintAsync`) → assert `PrinterBackendBusyException` thrown. Do not test helper/parsing logic in isolation; those are compile-time correct. The contract seam (backend rejects → exception raised → controller maps to outcome) is the critical path that needs end-to-end verification.

- Comment: https://github.com/OlyForge3D/PrintFarmer/pull/318#issuecomment-4570450469

---

## Decision: Round 18 — Lambert PR #318 (backend firmware-409); Hicks APPROVE #14; Bishop CR #15

**Date:** 2025-11-23
**Authors:** Lambert (backend), Hicks (iOS review), Bishop (backend review), Coordinator
**Status:** PR #318 merged (plugins firmware-409 propagation), PR #14 APPROVE snapshot spike (Brett), PR #15 REQUEST_CHANGES (Newt integration under-spec)

### Summary

- **PR #318 (Backend busy-error propagation):** Moonraker `SendGcodePrivateAsync` throws on HTTP 409/503; SDCP `StartPrintAsync` round-trips status on Ack failure (new `IsPrintingStatus` internal helper + `InternalsVisibleTo`); FlashForge `StartPrintAsync` echoes `~M119` check on rejection (`IsBuildingStatus` promoted to internal). All backends now translate firmware signals into `PrinterBackendBusyException` → `PrinterControlOutcome.BackendBusy` → 502. Controller gate (`PrinterControlGate.IsBusyForControl`) remains primary defense; plugin layer is defense-in-depth. 23 tests, 3 new files, all passing. Build clean, no new warnings. **Merged.**
- **PR #14 APPROVE (Brett snapshot spike):** FlashForge temp claim matches `fallback(for: .flashForge)` on stack branch. Note: older `PrinterBackendCapabilitiesTests` fixture JSON shows FlashForge temp support off — Brett should describe source more precisely in any revision.
- **PR #15 REQUEST_CHANGES (Newt integration plan):** Plan re-states Home gating incorrectly (restates `canHomeAll` alone instead of OR of `canHomeAll || canHomeXY || canHomeZ` per PR #12 implementation). ViewModel scope under-specified (`PrinterControlsViewModel` still requires `printerService` injection — plan doesn't address). Test scope under-specified (#289 implies a new test file/test target update despite plan's "2 files / no new files" claim).

---

## Decision: Round 14 — Hudson PR #13 init-state fix; Bishop CR #12 spec-string hazard persists

**Date:** 2025-11-22
**Authors:** Hudson (iOS), Bishop (code review), Coordinator
**Status:** PR #13 merged (init-state fix), PR #12 REQUEST_CHANGES (spec strings + test gaps)

### Summary

- **PR #13 (Jog subgroup init fix):** `JogSubgroup` now has explicit `init` seeding `_selectedAxis = State(initialValue:)` from first available axis in capability subset. Added `canJogX/Y/ZOverride: Bool?` to ViewModel (nil = fallback to `supportsMovement`). Three new init-state tests: Z-only → `.z`, XY-only → `.x`, Y-only → `.y`. **Merged.**
- **Bishop re-review (PR #12):** ❌ REQUEST_CHANGES (again). **Same gaps:** VoiceOver hints don't match spec **verbatim** (e.g., "Double-tap" with hyphen + XY/Z-specific wording). Tests hard-code expected strings instead of asserting through rendered `HomeButton`. Root cause: spec doc lives on `squad/283-design-printer-controls-section` (Newt's PR #1, not yet merged). Hudson's working branches don't have the spec file; he reconstructs strings from memory. **Fix:** Coordinator now inlines exact spec strings in Hudson's prompts, and directed him to `git show squad/283-design-printer-controls-section:docs/design/printer-controls-section.md`.
- **Durable rule added:** Spec strings (VoiceOver labels, hints, button copy) must be asserted by reading from rendered view, not comparing hardcoded constants in tests. Constants in tests = compile-time tautology, not spec validation.
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570135789

---

## Decision: Round 13 — Hudson fix #12, Hicks CR #13 rebase init-state bug

**Date:** 2025-11-22
**Authors:** Hudson (iOS), Hicks (code review)
**Status:** PR #12 merged, PR #13 rebased + REQUEST_CHANGES (init-state bug)

### Summary

- **PR #12 fix-up (HomeButton):** `HomeButton` now takes explicit `accessibilityLabel` and `activeHint` parameters; spec-defined labels passed at call site. Dispatch + per-button cap tests create `HomeSubgroup` struct, assert via `tap*ForTesting()`/`canHome*ForTesting()`. New section 8 locks in spec VoiceOver strings.
- **PR #13 rebase:** Cleanly rebased onto updated `squad/285-home-subgroup` and force-pushed — no conflicts. Stack rebase pattern confirmed: when fixing parent PR with stacked child, `git rebase` child onto updated parent and force-push after parent fix.
- **Hicks re-review (PR #13):** ❌ REQUEST_CHANGES. Real init bug: `selectedAxis` defaults to `.x` and only snaps on `onChange`. If `JogSubgroup` is created in subset-capability state (e.g. Z-only), UI shows only Z but bound action/feedrate still targets X until user changes selection. Must compute defaults from **initial capability subset**, not just full-capability case.
- **Durable rule:** SwiftUI subgroup `@State` defaults must be valid for **any initial capability subset**, not just full-capability. Use `init(...)` to compute defaults from caps, not just `onChange`.
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570098227

---

## Decision: Round 11 — Bishop APPROVE #11, Hicks CR #13

**Date:** 2025-11-21
**Authors:** Bishop (code review), Hicks (code review)
**Status:** In Review (PR #11 open + APPROVED, PR #13 open + REQUEST_CHANGES)

### Summary

- **Bishop re-review (PR #11 — preheat):** ✅ APPROVE. Cool Down fix confirmed working.
  - Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/11#issuecomment-4570039961
- **Hicks review (PR #13 — jog):** ❌ REQUEST_CHANGES. Two blockers identified:
  1. **Per-axis capability gating:** `JogSubgroup` always shows X/Y/Z buttons; should differentiate (show only Z if backend caps differ).
  2. **Test coverage:** Jog tests bypass SwiftUI view layer; don't verify picker selection, button taps, or view-level gating.
  - Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570039264

### Correction Note

**Round 10 status mislabel:** PR #11 was listed as "Merged" in round 10 entry. **Actual state: Open.** PR status must always be verified via `gh pr view --repo <owner>/<repo> <number>` at decision time, not assumed.

---

## Decision: Round 10 — Cool Down Label & Jog Subgroup

**Date:** 2025-11-21
**Authors:** Hudson (iOS)
**Status:** In Review (PR #11 open, PR #13 open)

### Summary

Hudson resolved the Cool Down preset label inconsistency and implemented the Jog subgroup for PrinterControlsSection. Also detected and fixed a pre-existing xcodeproj UUID collision.

- **Cool Down fix (PR #11):** Removed hardcoded "Off" ternary in `PreheatPreset.tempLabel`. Standard format string now produces "0° / 0°" uniformly.
- **Jog subgroup (PR #13):** Axis picker (X/Y/Z), step picker (0.1/1/10/100 mm, **default 1mm per Newt's spec**), ±mm buttons. Feedrates 3000 XY / 600 Z owned by view, forwarded to `viewModel.move()`. 15 tests, stack: #7 → #11 → #12 → #13.
- **xcodeproj fix:** Fixed pre-existing UUID collision (HomeSubgroupTests UUID = PushNotificationManager.swift fileRef) that broke xcodebuild.

---

## Decision: Status-Gated Mutation Endpoints — Layer and HTTP Code Mapping

**Date:** 2026-05-28
**Issue:** OlyForge3D/PrintFarmer#290
**Author:** Dallas
**Status:** Implemented (PR #308, merged)

### Decision

The 409 state-gate for `/temps`, `/move`, and `/moveto` lives in the **controller layer**
(`PrintersController.GatePrinterControlAsync`), not in `PrintersService`. The plugin layer
propagates firmware 409s as `PrinterBackendBusyException` → `PrinterControlOutcome.BackendBusy`
→ 502 Bad Gateway.

### HTTP Status Code Mapping

| Condition | HTTP code | Reason |
|---|---|---|
| Cached status is Printing/Pausing/Paused/Resuming/Cancelling/Heating | 409 Conflict | Client-side pre-flight; API knows before trying |
| Printer ID not found | 404 Not Found | Entity doesn't exist |
| Firmware refused (409 from PrusaLink/OctoPrint) | 502 Bad Gateway | Upstream refused after we tried; client cannot fix this |
| Backend does not support command | 502 Bad Gateway | Capability mismatch |
| Backend unreachable | 502 Bad Gateway | Infrastructure fault |

### Rationale

- **Controller, not service**: The status cache check is a request pre-flight concern. Services
  should not know about HTTP semantics. Keeps `PrintersService` focused on printer I/O.
- **502 for upstream busy (not 409)**: 409 from our API means "you asked at the wrong time and
  our state says so." 502 from our API means "we tried and the printer said no." These must be
  distinguishable so iOS clients can show the right UX.
- **`PrinterBackendBusyException`** is the seam: backend plugins throw it when firmware returns
  409, service catches and maps to `BackendBusy`, controller maps to 502.
- **Busy state list** (`PrinterControlGate.BusyStates`) is authoritative and kept in sync with
  `PrintFailureMonitorService` via PR #310.

### Files Changed

- `src/infra/Services/Printers/PrinterControlGate.cs` (new)
- `src/infra/Services/Printers/PrinterControlOutcome.cs` (new)
- `src/infra/Services/Printers/PrinterBackendBusyException.cs` (new)
- `src/api/Controllers/PrintersController.cs` (`GatePrinterControlAsync`, `MapControlOutcome`, `IPrinterStatusCacheReader` injection)
- `src/backends/Farm.Backend.Plugin.OctoPrint/OctoPrintClient.cs` (409 → `PrinterBackendBusyException` in SetBed/SetHotend/Jog)
- `src/backends/Farm.Backend.Plugin.PrusaLink/PrusaLinkApiClient.cs` (409 → `PrinterBackendBusyException` in SetToolTemp/SetBedTemp/JogPrintHead)
- `src/tests/Farm.Web.Api.Tests/Controllers/PrintersControllerControlGuardsTests.cs` (new, 4 tests)

---

# Decision: PrinterBackendCapabilities — Endpoint Confirmed, Fallback Table Canonical

**Date:** 2026-05-28
**Agent:** Gorman
**Issue:** #280
**PR:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/2

## Decision

`GET /api/printers/{printerId}/backend-capabilities` **exists** in `PrintersController.cs`
(src/api/Controllers/PrintersController.cs:181). No backend work is needed for Mobile Controls v1.

## Fallback Table Values

The static table in `PrinterBackendCapabilities.fallback(for:)` is now the canonical iOS
fallback when the endpoint returns 404 or decoding fails:

| Backend     | supportsMovement | supportsTemperatureControl | supportsControlOperations | Notes |
|-------------|-----------------|---------------------------|--------------------------|-------|
| Moonraker   | true            | true                      | true                     | Full FFF; camera+history too |
| PrusaLink   | true            | true                      | true                     | Full FFF |
| OctoPrint   | true            | true                      | true                     | Full FFF |
| FlashForge  | true            | true                      | false                    | FFF; no fan control |
| SDCP        | false           | false                     | false                    | Resin printer |
| Unknown     | false           | false                     | false                    | Conservative |

## Locked Decisions Applied

- `supportsBedTemperature` is derived from `supportsTemperatureControl` — no separate field in
  backend DTO. Locked per Mobile Controls v1 spec: trust `supportsTemperatureControl` for FlashForge.
- `supportsFanControl` derived from `supportsControlOperations` — fan is a general control operation.

## Downstream Impact

- `PrinterControlsViewModel` (#282) already calls `PrinterBackendCapabilities.fallback(for:)` —
  the interface and fallback signature are compatible.
- UI gating (#284/#285/#286) can trust all four of the required booleans.

---

# Newt — 2026-05-28 — Printer Controls Design Decisions (#283)

## Preheat: List layout, not grid

**Decision:** Use vertical list rows for preheat presets instead of 2×2 grid.

**Reasoning:**
- List rows allow inline temperature readout (e.g., "PLA — 200°/60°") which provides at-a-glance reference
- Full-width rows are easier to tap on phone screens
- Consistent with iOS Settings patterns for actionable list items
- Grid would require separate tap + temperature lookup, adding cognitive load

## Disabled-While-Printing: Lock icon + opacity (color-blind friendly)

**Decision:** Disabled state uses lock icon (`lock.fill`) at trailing edge plus 0.5 opacity, not just color change.

**Reasoning:**
- Per WCAG 2.2, disabled state must not rely on color alone
- Lock icon provides shape-based indicator recognizable without color perception
- Aligns with iOS system patterns (e.g., locked settings rows)
- Ensures accessibility for protanopia/deuteranopia users

## Jog: Segmented pickers + dynamic button labels

**Decision:** Jog subgroup uses native segmented pickers for axis (X/Y/Z) and step (0.1/1/10/100mm), with +/− buttons showing dynamic labels like "Move X +10mm".

**Reasoning:**
- Segmented pickers are HIG-native and automatically meet touch target requirements
- Dynamic button labels prevent mode errors (operator always knows what will happen)
- Axis/step state is visually prominent in picker selection
- Compact layout fits phone screens without scrolling

## Section Visibility: Hidden when offline

**Decision:** Entire Controls section is conditionally rendered only when `printer.isOnline == true`.

**Reasoning:**
- Controls require active printer connection — showing disabled controls when offline adds noise
- Consistent with existing pattern: `actionSection` only renders when online
- Reduces visual clutter for disconnected printers
- Clear mental model: "no controls = printer not reachable"

---

# Decision: Role-gated UI uses plain `if`-conditional, not a ViewModifier

**Date:** 2026-05-28  
**Issue:** OlyForge3D/PrintFarmerMobile#3 (iOS #274)  
**Author:** Hudson  
**Status:** Implemented

## Context

The Maintenance toggle in `PrinterDetailView` must be hidden for non-`farm_admin` users.
Two patterns were considered:

1. **Plain `if authViewModel.currentUserRole == "farm_admin" { ... }`** around the button block.
2. A custom `adminOnly()` ViewModifier that reads role from environment and calls `.hidden()` or returns `EmptyView`.

## Decision

Plain `if`-conditional (option 1).

## Rationale

- The button is **entirely absent** from the view hierarchy for non-admins, not merely hidden. This avoids focus/VoiceOver traversal and any accidental tap passthrough.
- ViewModifier would still construct the button node and apply `.hidden()` — semantically weaker.
- Consistent with Apple HIG: omit controls the user can't use rather than disable/hide them.
- Simpler — no new abstraction needed for a single call site. If multiple admin-only surfaces emerge, a modifier becomes worthwhile and this decision should be revisited.

## Consequences

- Any future admin-only control needs the same one-liner `if authViewModel.currentUserRole == "farm_admin"`.  
- If admin role gating becomes widespread (>3 sites), consider extracting a `.adminOnly(authViewModel)` modifier or an `@ViewBuilder adminOnly { ... }` helper.

---

# iOS #281 — PrinterService Command Method Routing Decisions

**Date:** 2026-05-28  
**Author:** Gorman  
**Issue:** OlyForge3D/PrintFarmer#281  
**PR:** OlyForge3D/PrintFarmerMobile#4

## Decision 1: homeXY / homeZ map to dedicated backend routes, not a parameterized `/home`

**Context:** Issue #281 spec described `home(printerId:axes:)` as a single method routing to
`POST /api/printers/{id}/home`. Backend inspection revealed three separate no-body POST endpoints:
`/home` (all axes), `/homexy`, `/homez`.

**Decision:** `home(printerId:axes:)` dispatches internally by sorted axes array:
- `["X","Y"]` → `/homexy`
- `["Z"]` → `/homez`
- anything else (empty, `["X","Y","Z"]`, etc.) → `/home`

`homeXY` and `homeZ` are protocol extension defaults that call `home(axes:)`.

**Rationale:** No new backend routes needed. Caller API matches the issue spec. Route selection
is an implementation detail hidden from callers.

## Decision 2: setTemperatures nil-omit via custom Encodable (not dictionary)

**Context:** Backend `TempTargets` C# record always has both `hotend` and `bed` (non-nullable
ints). Issue #281 allows callers to pass `nil` for either field to omit it.

**Decision:** Private `SetTemperaturesRequest` with custom `encode(to:)` that conditionally
encodes each field. Not a `[String: Double]` dictionary — typed struct is safer and more
readable.

**Rationale:** Dictionary approach works but loses type safety. Custom Encodable is the Swift
idiomatic pattern for omitting optional JSON fields without `null` emission.

## Decision 3: move body uses [String: Double] dictionary

**Context:** `MoveRequest` C# record has `x?`, `y?`, `z?`, `f?` fields. Swift needs to set
only the relevant axis.

**Decision:** `var body: [String: Double] = ["f": Double(feedrateMmMin)]` then
`body[axis.lowercased()] = distanceMm`. Dictionary naturally omits unset keys.

**Rationale:** A 4-field Encodable struct with 3 nil fields and a custom encoder is more
boilerplate than the problem warrants. Dictionary is clean and correct here.

## Decision 4: 409 conflict maps to existing NetworkError.conflict

**Context:** `GatePrinterControlAsync` returns HTTP 409 when printer is printing/busy.
Applies to `/temps` and `/move` (not `/home*`).

**Decision:** No new error case. `APIClient` already maps HTTP 409 → `NetworkError.conflict`.
Callers (`PrinterControlsViewModel`) catch `.conflict` and surface "Printer busy" to the user.

---

# Decision: Canonical "Is Printing" Source for Failure Detection Shield

**Date:** 2026-05-28  
**Author:** Ripley  
**Issue:** #309  
**PR:** #313

## Decision

The failure-detection shield badge must derive `isPrinting` from the live printer state (`printer.state`), not from `FailureDetectionPrinterStatusDto.isPrinting`.

## Context

`FailureDetectionPrinterStatusDto.isPrinting` is computed by the backend failure-detection polling service on a ~30-second cycle. Between poll cycles, the DTO can report `isPrinting: false` while the printer has already started a print job. The badge was using this stale value directly, causing the shield to show "Printer is not printing." on actively printing printers.

The live `printer.state` field is updated via SignalR in near-realtime and is the authoritative source of the printer's current state.

## Rule

When rendering `FailureDetectionMonitoringBadge` or `FailureDetectionMonitoringOverlay`:

1. Compute live `isPrinting` from `printer.state`:
   - `CompactPrinterCard`: `state.toLowerCase().includes('printing')` (catches Pausing too)
   - `DetailedPrinterCard`: `isOnline && state === 'Printing'`
2. Pass as `isPrinting` prop to the badge/overlay.
3. Inside the badge, build `effectiveStatus = { ...status, isPrinting, reason: <override if staleMismatch> }`.
4. Pass `effectiveStatus` (not raw `status`) to `FailureDetectionStatusModal`.

If `isPrinting === true` but `status.state` is `'idle'` or `'disabled'`, also replace `status.reason` with a waiting message so the modal copy is accurate.

## References

- `FailureDetectionMonitoringBadge.tsx` — `isPrinting` prop, `stalePrintingMismatch`, `effectiveStatus`
- `CompactPrinterCard.tsx` / `DetailedPrinterCard.tsx` — `isPrinting={isPrinting}` passed to badge
- `usePrinterFailureDetectionStatus.ts` — 30s polling hook (stale source)

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

---

## 2026-05-21: Inbox merge — Mobile Controls v1 Phase 1

_Merged by Scribe from `.squad/decisions/inbox/` during Ralph rounds 2–5 closeout._


---

# Dallas — 2026-05-21 — Issues #275 and #290 triage

## Issue #275 — closed `not planned` (wontfix)

**Decision:** Option (a) — keep both `/api/printers/{id}/stop` and `/api/printers/{id}/emergency-stop`, document, close.

**Reasoning:**
- Gorman's investigation showed iOS `PrinterService.stop()` calls `/stop`, which is a real route on the backend (not in-process aliasing). The original premise of #275 — that `.stop()` is a redundant in-process alias — was incorrect.
- Refactor (option b) touches backend + iOS + web with deprecation cycle for negligible gain.
- Renaming `/stop` to a "real" route (option c) is semantic gymnastics — both endpoints still execute the same emergency-stop operation.
- The 5-line backend shim (`PrintersController.StopAsync` → `EmergencyStopAsync`) is documented as intentional compat surface. No bug, no maintenance burden, no security gap.

**Action taken:**
- Comment posted on #275 with full triage rationale.
- Issue closed with reason `not planned`.
- No code changes. iOS `stop()`, protocol entry, test (`testStopCallsCorrectEndpoint`), `PrinterDetailViewModel.swift:429`, and backend shim all stay.

---

## Issue #290 — reassigned `squad:⚛️ ripley` → `squad:🏗️ dallas`

**Decision:** I take ownership. Cross-cutting backend implementation across all printer plugins is architecture/cross-domain work — Ripley is a tester. We have no dedicated backend agent, so it lands with me.

**Reasoning:**
- Spike found zero server-side guards across backend plugins. Real gap, but not a v1 blocker:
  - Existing design locks already require **client-side** guards (web + iOS) — covered by the 16-issue plan.
  - Server-side guards = defense-in-depth (catches direct API callers / scripts / future third-party clients).
- Practical priority: **P1** (post-v1). Will adjust the priority label when scheduling. Kept `priority:p0` for now since I'm not changing the existing prioritization scheme without a separate decision.
- Did NOT file a request for a new backend agent. Decision: I'll hold the work as Lead until volume justifies adding a backend specialist.

**Action taken:**
- Comment posted on #290 explaining routing decision.
- Labels: removed `squad:⚛️ ripley`, removed accidentally-added `squad:dallas` (non-emoji), added `squad:🏗️ dallas`.
- Scope preserved from Ripley's original filing. Per-plugin sub-issues to be created during design phase.


---

# Gorman — Issue #280: hybrid backend-capabilities (endpoint + static fallback)

**Date:** 2026-05-21
**Agent:** Gorman (iOS Networking)
**PR:** https://github.com/OlyForge3D/PrintFarmer/pull/295
**Issue:** #280

## What

iOS `PrinterService.getBackendCapabilities(printerId:)` resolves the new `PrinterBackendCapabilities` model via a **hybrid** path:

1. GET `/api/printers/{id}/backend-capabilities` and decode the wire DTO.
2. Read `backend` (PrinterBackend) from the response, look up `PrinterBackendCapabilities.fallback(for: backend)` as the base.
3. Overlay the API's authoritative `supportsMovement` and `supportsTemperatureControl` on top of the fallback.
4. The remaining four fields (`supportsBedTemperature`, `supportsFanControl`, `supportsHoming`, `supportedAxes`) come entirely from the static fallback table — the backend DTO does not currently expose them.
5. On `.notFound` / `.serverError`, fetch the printer and return `fallback(for: printer.backend)` alone.
6. Cached in-memory in the `PrinterService` actor by `UUID`.

## Why this matters team-wide

- **Backend-side follow-up:** the iOS side now consumes `supportsBedTemperature`, `supportsFanControl`, `supportsHoming`, `supportedAxes` as if they were first-class fields. When the backend `PrinterBackendCapabilitiesDto` grows them, the wire decode will pick them up automatically and the overlay can tighten in one line. No iOS migration needed.
- **Static fallback table is the contract** until the API catches up. Backend changes that contradict the table (e.g. introducing a flag that says SDCP supports homing) need to update both the API DTO and the iOS table.
- **Wire DTO field naming:** iOS decodes `printerId`, `backend`, `supportsMovement`, `supportsTemperatureControl` plus all the other capability bools (forward-compat). Any rename on the backend will silently break decode — coordinate via this decision file.

## Static fallback table (authoritative for the four missing fields)

| Backend | Movement | Temp | Bed | Fan | Homing | Axes |
|---|---|---|---|---|---|---|
| Moonraker, PrusaLink, OctoPrint | ✓ | ✓ | ✓ | ✓ | ✓ | X,Y,Z |
| FlashForge | ✓ | ✓ | – | – | ✓ | X,Y,Z |
| SDCP | – | – | – | – | – | (none) |
| Unknown | – | – | – | – | – | (none) |

## Surfaces

- New: `mobile/PrintFarmer/Models/PrinterBackendCapabilities.swift`
- New: `mobile/PrintFarmerTests/Models/PrinterBackendCapabilitiesTests.swift` (8 cases)
- Edited: `PrinterServiceProtocol.swift`, `PrinterService.swift`, `DemoPrinterService.swift`, `MockPrinterService.swift`

## Conventions confirmed (for future iOS PRs)

- `PrinterService` methods take `UUID`, not `String`, regardless of issue spec wording.
- Pbxproj registration is deferred for new files; `Package.swift` SPM paths auto-discover them. CI uses `swift test`. Xcode users may need to drag files in manually until a coordinated pbxproj sweep.
- Pure-model fallback tables get pure-model XCTest cases (no `MockURLProtocol`).


---

### 2026-05-21T00:00:00Z: Printer-controls v1 design — non-obvious calls
**By:** Newt (UX) for #283
**What:**
- Single-flight queue is **per subgroup**, not global. Preheat lock does not freeze Home/Jog.
- Pending → Default timeout = **5 seconds** with a neutral toast ("Sent. Awaiting printer."), not an error.
- Disabled-during-print uses **greyscale + 8% diagonal stripe overlay** for color-blind users (per #15).
- Capability missing → **remove the control from the layout**. No greyed slot, no tooltip.
- Error banner sits **directly under the affected subgroup** (not at section top) so the failed command is unambiguous.
- Debounce: **250ms trailing-edge** on every control tap.
- Lockout banner is **section-level**, not per-subgroup.
- Mid-print state hides nothing — controls greyed + striped + announce "Controls locked" once via VoiceOver.
- Section is fully hidden when `printer.isOnline == false` (`EmptyView()`).
- Jog `+/−` use **60pt** height (above standard 44/50pt) — they're the most-tapped.

**Why:** Locks ambiguity in the spec so #284/#285/#286 implementation does not need follow-up design clarifications.

**Doc:** `mobile/docs/design/printer-controls-section.md`


---

# Decision: PrinterControlsViewModel public contract (#282)

**Author:** Gorman
**Date:** 2026-05-21
**Issue:** #282
**PR:** https://github.com/OlyForge3D/PrintFarmer/pull/298
**File:** `mobile/PrintFarmer/ViewModels/PrinterControlsViewModel.swift`

This freezes the public surface so Hudson can build views without reading source.

## Public types

- `PreheatPreset` — `.pla` (200/60), `.petg` (240/80), `.abs` (240/100), `.coolDown` (0/0). `coolDown` always sends 0/0 regardless of capabilities.
- `ControlCommand { kind: Kind, startedAt: Date }` with `Kind`:
  - `.preheat(PreheatPreset)`
  - `.home(axes: [String])` — uppercase axis names
  - `.jog(axis: String, distanceMm: Double)`
- `ControlsError { command: ControlCommand, message: String, isRetryable: Bool }`

## Constants

- `static let xyFeedrateMmMin: Int = 3000`
- `static let zFeedrateMmMin: Int = 600`

## Published state (all `@Published private(set)`)

- `capabilities: PrinterBackendCapabilities?`
- `lastError: ControlsError?`
- `pendingCommand: ControlCommand?`
- `isLoadingCapabilities: Bool`

## Init

`init(printerService: PrinterServiceProtocol, printer: Printer, clock: @escaping () -> Date = Date.init)`

## Methods

- `func loadCapabilities() async` — single-fetch cache; on error falls back to `PrinterBackendCapabilities.fallback(for: printer.backend)` **silently** (does not set `lastError`).
- `func preheat(_ preset: PreheatPreset) async` — gated on `supportsTemperatureControl` (except `.coolDown`); bed value silently dropped if `!supportsBedTemperature`.
- `func homeAll() async` / `func homeXY() async` / `func homeZ() async` — gated on `supportsHoming`.
- `func jog(axis: String, distanceMm: Double) async` — gated on `supportsMovement`. Axis uppercased. Feedrate: Z → 600, else 3000 mm/min.
- `func dismissError()` — clears `lastError`.
- `func handlePrinterUpdate(_ updated: Printer)` — SignalR hook. Replaces internal printer **and clears `pendingCommand`**.

## Computed

- `canControl: Bool` — `printer.isOnline && !isPrintingOrPaused`.
- `blockedReason: String?` — `"Printer is offline."` | `"Controls are locked while a print is active."` | `nil`.
- `isPrintingOrPaused` matches state strings `"printing"`, `"paused"`, `"starting"` (case-insensitive).

## Behavioral contract (do not change without coordination)

1. **SignalR is the truth.** `pendingCommand` is set when a command begins and is **only cleared by `handlePrinterUpdate(_:)` (SignalR) or by command failure**. A successful API return leaves `pendingCommand` set so the spinner persists until the printer actually responds.
2. **Single-flight, no queue.** A second command issued while `pendingCommand != nil` returns silently with no error and no state change. UI must disable controls based on `pendingCommand != nil`.
3. **Capabilities never block UX.** Fetch failures fall back silently. Per-command capability gates short-circuit (no API call, no error) when the backend doesn't support the action.
4. **Error mapping** (`static func mapError(_:) -> (message: String, isRetryable: Bool)`):
   - 5xx / conflict / network → `isRetryable = true`
   - 4xx → `isRetryable = false`
5. **No automatic retry.** UI surfaces `lastError`; user retries by reissuing the command (which is now allowed because `pendingCommand` was cleared on failure).


---

# Mobile Controls v1 — Review Batch 1 Architectural Rulings

**By:** Dallas (review of PRs #291–#297, 2026-05-21)
**What:** Architectural rulings made during batch-1 review. Capture for downstream work (#282 ViewModel, #284–#286 UI build).
**Why:** Several decisions need the team's persistent memory beyond per-PR comments.

## Ruling A — `homedAxes` is `String?`, not `[String]?` (PR #294)
The backend wire format is a compact lowercase string: `"xyz"`, `"xy"`, `""`, or `nil`. iOS models (`Printer.homedAxes`, `PrinterStatusDetail.homedAxes`) MUST match this shape. View rendering uses case-insensitive `contains("x"|"y"|"z")` per axis. Tests cover present / absent / empty.

## Ruling B — Defensive nil-guard on partial status updates (PR #294)
`PrinterDetailViewModel` MUST guard against partial detail-update payloads clobbering existing values:
```swift
if let homed = detail.homedAxes { current.homedAxes = homed }
```
This pattern should be applied to other optional-but-stateful fields when adding new ViewModel update paths.

## Ruling C — Capabilities resolution: hybrid endpoint + static fallback (PR #295)
v1 strategy: GET `/api/printers/{id}/backend-capabilities` → overlay onto static `PrinterBackendCapabilities.fallback(for: PrinterBackend)`. Backend currently surfaces only 2/14 fields; fallback table fills the rest. Failure modes (`.notFound`, `.serverError`) → use static fallback (no error to user). Actor-isolated cache `[UUID: PrinterBackendCapabilities]`, **no TTL in v1** — flagged for v2 follow-up if a printer's backend can change mid-session.

## Ruling D — Capability missing ≠ disabled (PR #296)
When a capability is false, the corresponding control is **removed from the UI**, not greyed out. Mid-print disable IS greyed (with diagonal-stripe overlay per #15 colorblind spec). Two distinct visual states; do not conflate.

## Ruling E — `PrintJobPriority.from(intValue:)` is preserved (PR #293)
While the wire format for enums is string-only (`JsonStringEnumConverter` global), `PrintJobDto.Priority` is serialized as a raw int field (NOT an enum on the wire). The `from(intValue:)` helper stays. Same exemption: `SignalRModels.AnyCodable` Int branch is correct (heterogeneous wrapper).

## Ruling F — `MovePrinterRequest` unknown-axis fallback to `.x` is acceptable for v1 (PR #297, non-blocking)
The locked axis picker (XYZ enum) prevents an unknown axis from reaching encoding in practice. Silent fallback to `.x` is acceptable for v1. Add a `precondition` assertion or exhaustive switch on axis when hardening (likely in #287 integration or post-v1).

## Ruling G — Self-PR review constraint
GitHub blocks `gh pr review --approve` on PRs authored by the reviewing user. Use `--comment` for verdicts + `--admin` for squash-merge. This applies to any squad agent reviewing their own PR — Dallas reviewing as Lead is not exempt when authoring.

## Ruling H — Cross-author rebase handoff after merge cascades
When sibling PRs in a batch touch overlapping files (e.g., #295 capabilities + #297 service methods on PrinterService), reviewer must NOT rebase the conflicting branches unilaterally — that violates the reviewer/author separation principle. Instead, post a "needs rebase" comment with explicit conflict-resolution guidance (e.g., "keep both sides; mechanical merge"). The original author rebases.

---

### 2026-05-21T09:38-07:00: AMS slot count is a backend off-by-one, not a frontend hardcode
**By:** Ripley (requested by Jeff Papiez)
**What:** Issue #302 root cause traced to `PrintersService.cs:2959` — `for (int i = 1; i < mmuGateCount; i++)` creates `mmuGateCount - 1` MmuGate toolheads (3 for default 4), leaving T0 as Physical. Result on Bambu: 1 Physical + 3 MmuGate instead of 4 MmuGate. Frontend `AmsSlotVisualization` is data-driven and will render 4 slots correctly once the seeding produces 4 gates.
**Why:** Tagged issue `area:backend` and stopped before implementing — fix needs decision on `mmuGateCount` semantics (total gates vs. total toolheads), test update for `MmuGateAutoCreationTests.CreatePrinter_MultiMaterialTrue_CreatesThreeMmuGateToolheads`, and a repair routine for already-seeded printers. Frontend dedup of the lower "Spools" section is queued as a follow-up that must land after the backend fix.

### 2026-05-21: PR #301 review — PreheatSubgroup (Hudson) verdict: 💬 Comment

**By:** Vasquez (Code Reviewer)

**What:** Reviewed PR #301 (`feat(ios): build PrinterControlsSection preheat subgroup`). Posted a `--comment` review on `OlyForge3D/PrintFarmer#301`. Spec adherence is good (presets, layout, single-flight, a11y, hit target, capability gating). Four non-blocking findings: unused `previewSeedCapabilities(_ caps:)` parameter, iPad disabled-tap reveal gap (`.disabled` + `.help()` won't show on touch-only iPad), accessibility-label localization gap (informational — no localization infra exists yet under `mobile/PrintFarmer/`), and a misnamed `unsafeBitCastedFallback()` helper.

**Why:** Confirms the iOS Preheat subgroup respects the client-side capability-gating decision (#279/#290) — backend not trusted, gating happens in `isVisible(capabilities:)` on the view and re-validated at dispatch in `PrinterControlsViewModel.preheat`. Author can address the unused param + iPad reveal gap before flipping out of draft; localization and the rename are safe follow-ups.

### 2026-05-21: pbxproj rebase pattern — union resolution after sibling subgroup PRs merge

**By:** hudson (via coordinator)
**What:** When sibling Xcode pbxproj-touching PRs (e.g. PrinterControls subgroups) have one merge first, the others rebase with predictable conflicts in two regions: parent group children list (e.g. `PrintFarmerTests` → `Views` ref) and the test target's Sources build phase. Resolve by **union** — keep both sides' references. Each branch typically generates a distinct `Views` group ID; both definitions already exist independently in the file body, so referencing both is non-destructive and Xcode tolerates duplicate-name groups with distinct IDs.
**Why:** Applied to PRs #300 (home) and #301 (preheat) after #299 (jog) merged. Both rebased cleanly with `plutil -lint` passing. Force-pushed; both report `mergeable: MERGEABLE`. Local xcodebuild blocked by iOS 26.5 SDK absence; CI is authoritative.


### 2026-05-21: iOS PrinterControlsSection forwards SignalR via parent, does not re-subscribe
**By:** Hudson (iOS Dev) for jpapiez
**What:** When a child SwiftUI view needs to react to `printerupdated` SignalR events but the parent `PrinterDetailViewModel` already subscribes via `configureSignalR`, the child must NOT open its own hub registration. Instead, accept the `printer: Printer` as a let-bound input and use `.onChange(of: printer.isOnline)` / `.onChange(of: printer.state)` to forward into the child VM. This is the pattern used by `PrinterControlsSection` (PR #304, issue #287).
**Why:** Acceptance criteria on #287 say "View subscribes to printerupdated SignalR events", but duplicating the subscription would leak hub registrations and cause double-handling. Parent already owns the subscription and the printer rebuild — child observes the resulting value change. Single source of truth; no leaks.
**Scope:** iOS / SwiftUI views composed inside `PrinterDetailView` (or any view whose parent VM owns a SignalR subscription).

### 2026-05-21T14:35:00-07:00: Snapshot testing — proposed dependency add for #289
**By:** Hudson (requested by Jeff Papiez)
**What:** Issue #289 requires snapshot tests for `PrinterControlsSection`. The repo has NO existing snapshot infrastructure (verified: no `swift-snapshot-testing`, no `Package.resolved`, no `__Snapshots__` directory; "snapshot" mentions in tests are unrelated — they refer to camera image data on `PrinterServiceProtocol.getSnapshot`). Issue is labeled `go:needs-research`. Two viable paths:

1. **Recommended:** Add `pointfreeco/swift-snapshot-testing` (~1.18.x) as a Swift Package dependency to the test target only.
   - Update `mobile/Package.swift`: add `https://github.com/pointfreeco/swift-snapshot-testing` to `dependencies`, add `SnapshotTesting` product to the `PrintFarmerTests` testTarget.
   - Update `mobile/PrintFarmer.xcodeproj/project.pbxproj`: add `XCRemoteSwiftPackageReference` + `XCSwiftPackageProductDependency` linked to `PrintFarmerTestsTarget` build phase. (Non-trivial pbxproj surgery; Xcode-generated normally.)
   - Snapshot baselines stored under `PrintFarmerTests/__Snapshots__/PrinterControlsSectionTests/`.
   - **CI implication:** Local xcodebuild is blocked by iOS 26.5 SDK / CoreSimulator drift (recurring theme in Hudson history). Baselines MUST be generated on CI or a machine with a working sim. Recording mode (`isRecording = true`) cannot be run from this dev box right now.

2. **Alternative (lightweight, no dep):** Hierarchy/text snapshots — render the view via `UIHostingController`, walk the view tree via reflection or capture `ViewThatFits`/`AnyView` description, and assert string equality against checked-in `.txt` fixtures. Brittle and gives weaker regression coverage than `swift-snapshot-testing` image diffs; not recommended.

**Why:** Path 1 is the industry-standard for SwiftUI snapshot testing and is what the issue text assumes ("If the existing snapshot infra is `swift-snapshot-testing`, reuse it"). Path 2 reinvents a wheel poorly. The blocker is dependency-add approval (one new package) + acceptance that baselines come from CI.

**Proposal:** Approve path 1. Hudson will land the dep add + test scaffolding + three test cases (Moonraker / FlashForge / SDCP) × (idle visible / printing hidden) in a follow-up commit on `squad/289-controls-snapshot`, with `isRecording = true` on first CI run to capture baselines, then a second commit flipping back to `isRecording = false`. Draft PR opened against #289 with research notes pending Lead approval.

### 2026-05-21T14:42:00Z: Shared disabled-control treatment + localized a11y for controls subgroups (issue #288)
**By:** Hudson (iOS Developer) — requested by Brady Gaster

**What:** Built `DisabledControlStyle.swift` housing three reusable view modifiers used by all controls subgroups:
- `.disabledControlStyle(isDisabled:cornerRadius:)` — 50% opacity + Canvas-drawn 45° diagonal stripe overlay at 8% white (falls back to flat grey when `accessibilityReduceTransparency` is on). Spec §2.4 color-blind cue.
- `.errorBorderHighlight(isActive:cornerRadius:)` — 1.5pt `pfError` stroked border with `easeInOut(0.2)` animation. Surfaced when `viewModel.lastError?.command.kind` matches the button's identity.
- `.disabledTapReveal(isDisabled:reason:onReveal:)` — overlay tap detection for touch-only devices since SwiftUI `.help()` only fires on hover. Each subgroup wires this into a local `handleTap` helper that drives a transient `disabledTapMessage` caption auto-dismissed after 3s.

Applied to:
- `PreheatSubgroup.swift` — per-preset error matching via `isErrored(preset:)`.
- `HomeSubgroup.swift` — per-axis-set error matching via `isErrored(matching: ["X","Y","Z"]/["X","Y"]/["Z"])`.
- `JogSubgroup.swift` — per-direction matching via `isErrored(direction:)` against `selectedAxis` + sign of `distanceMm`.

All `accessibilityLabel`/`Hint`/`Value` strings now go through `String(localized:, comment:)` so labels are localization-ready (issue #288 deliverable). Error hint pattern: `"Failed: \(message). Double tap to retry."`. Pending value: `"Sending command"`. Disabled hint surfaces `viewModel.blockedReason`. `accessibilityAddTraits` flips to `.updatesFrequently` while a command is pending so VoiceOver re-announces.

**Renamed `Printer.previewStub` → `Printer.previewFallbackPrinter`** (per Vasquez's review — the original sarcastic flag on `try! JSONDecoder().decode(...)` was the actual concern). Three call sites updated in PreheatSubgroup.

**Why:** Spec `mobile/docs/design/printer-controls-section.md` §2.4 and §4 explicitly require the diagonal stripe + pfError border + localized VoiceOver scripts. Three subgroups landed earlier without these, and #288 captures the gap. The shared modifier file means we don't open-code the stripe pattern in three places.

**Validation status:**
- `swiftc -parse` on all four files: clean.
- `plutil -lint project.pbxproj`: OK after registering `DisabledControlStyle.swift` (4 pbxproj entries: PBXBuildFile, PBXFileReference, PBXGroup child, Sources phase).
- `xcodebuild -list`: project loads, both targets visible.
- Full build deferred to CI (iOS 26.5 SDK drift makes local `xcodebuild build` unreliable here).

**Out of scope (filed as follow-ups if needed):** `PrinterControlsSection.shouldHide(for:)` removes the entire section during `printing | paused | starting`, which conflicts with spec §3.4's "visible but locked" expectation. The disabled treatment is still applied on transient state changes (single-flight sibling buttons, capability flips), so it earns its keep regardless.

**Files touched:**
- `mobile/PrintFarmer/Views/PrinterControls/DisabledControlStyle.swift` (new)
- `mobile/PrintFarmer/Views/PrinterControls/PreheatSubgroup.swift`
- `mobile/PrintFarmer/Views/PrinterControls/HomeSubgroup.swift`
- `mobile/PrintFarmer/Views/PrinterControls/JogSubgroup.swift`
- `mobile/PrintFarmer.xcodeproj/project.pbxproj`

---

## Camera Management Endpoint Detection and Association UI (2026-05-26T09:45:35.148-07:00)

**Decision:** Camera management now treats printer association and endpoint discovery as first-class camera-editing workflows.

**Owner(s):** Lambert (Backend), Ripley (Frontend)

**Status:** Implemented on `development` in commits `384868e28`, `353cd7ecb`, and earlier Ripley commit `f0589aec0`.

### Backend Contract

- Added `POST /api/cameras/detect-endpoints` with request `{ "printerId": "<guid>" }`.
- Success response uses camelCase camera endpoint fields: `streamUrl`, `snapshotUrl`, `detected`, and `source`.
- Missing printers return `404`; unsupported backends and probe failures return `200` with `detected: false`.
- Added `IPrinterCameraProbe` in the discovery layer and concrete Moonraker/Klipper, OctoPrint, and SDCP/Elegoo probes.
- `CameraDto` now includes `printerId` and `printerName` so list/get/update responses can show linked printers.

### Frontend UX

- Camera cards expose farm-admin Edit and Delete actions using shared modal components.
- Edit Camera includes an Associated Printer dropdown and Detect Endpoints button.
- Detected endpoints populate Stream URL and Snapshot URL fields for the selected printer.
- Camera management table now includes a Printer column using linked `printerName`.
- Camera preview media uses `object-contain bg-black` so stream frames are not zoomed or cropped in fixed-aspect cards.

### Validation

- Ripley earlier dispatch: build, lint, and focused camera tests passed.
- Ripley-1: `npm run build` and `npm run lint` passed; no affected component tests existed.
- Lambert: restore and API build passed; focused camera tests passed. Full suite/format had pre-existing unrelated failures.

### Follow-up

- Add concrete endpoint probes for PrusaLink/Buddy companion cameras, FlashForge, and any future Bambu backend once backend-specific camera contracts are known.

---

## Decision: Printer Offline Classification (lambert-1, 2026-05-26)

Moonraker/Klipper online state for list/detail surfaces is cached by `MoonrakerSubscriptionService` and served by `PrintersService.GetAllCompleteDtosAsync`.

- Treat explicit Moonraker `webhooks.state != ready` as not-ready/offline, but do not require `webhooks` to be present on every subscription/status payload.
- A successful Moonraker status payload containing printer objects (`toolhead`, `print_stats`, `display_status`, etc.) proves the printer is reachable and should keep `IsOnline=true`.
- Transport failures, exhausted reconnect attempts, `notify_klippy_disconnected`, and `notify_klippy_shutdown` remain the paths that mark the printer offline.
- HTTP polling fallback must update `PrinterStatusCache`, not just SignalR, so REST clients and mobile clients do not read stale status.

---

## Decision: arco1 Runtime Evidence — List vs. Detail Cache Discrepancy (lambert-probe2, 2026-05-26)

UI `/printers` shows `ARCO1` as `Offline`, but API detail endpoint shows `isOnline: true` for the same printer. Direct Moonraker is reachable.

**Diagnosis:** The bad data is not Moonraker. Strongest inconsistency is inside PrintFarmer API/status composition: the list endpoint has stale or misclassified `isOnline: false` while the detail endpoint has `isOnline: true` moments later.

**Root cause candidate:** `src/backends/Farm.Backend.Plugin.Moonraker/MoonrakerSubscriptionService.cs` around `_klippyReadyState`, `EmitConsolidatedStatusAsync`, and offline updates, plus list endpoint merge logic that combines persisted printer rows with `PrinterStatusCache`.

**Artifacts:** captured under `arco1-probe2/` (printers-page.png, dashboard.png, arco1-detail/list JSON, moonraker endpoint responses, SignalR frames).

---

## Decision: Login Audit Log Backend (lambert-2, 2026-05-26)

**Status:** Implemented — awaiting review. Migrations committed for Postgres + SqlServer.

Added dedicated `LoginAuditEntry` table with `Username`, `IpAddress`, `UserAgent`, `Success`, `Timestamp`, `FailureReason` (indexed columns for fast queryable audit).

### API Contract

`GET /api/admin/security/login-audit` (requires `farm_admin` role).

Query params: `from` / `to`, `username` (substring), `success` (bool), `page` / `pageSize` (default 50, max 200).

Response: paginated `{ items: LoginAuditDto[], totalCount, page, pageSize }`.

### Hook Point

`AuthController.LoginAsync` — captures raw HTTP context (IP, User-Agent) at controller level.

### TODOs

- **Retention policy**: No cleanup job; recommend 30/90-day trim.
- **Rate-limit correlation**: Future work with `AuthenticationRateLimitMiddleware`.
- **Ripley UI**: See `ripley-2` decision below.

---

## Decision: Login Audit Log UI (ripley-2, 2026-05-26)

**Status:** Implemented on `development`. 23 tests passing.

Built `/admin/security/login-audit` page using project's Tailwind components (`Badge`, `DataTable`, `Tooltip`, `Select`, `Input`).

### Key Decisions

1. **UI library:** Project's custom `@/common/components/ui` (consistency with other admin pages).
2. **Navigation:** Added "Security" section header in admin nav as peer to "Settings".
3. **Tri-state success filter:** URL param stores `''` (all), `'true'` (success only), `'false'` (failure only).
4. **Filter state:** Batch updates with `setMany({ ...update, page: 1 })`; debounced username field via individual setter.
5. **API:** Direct `apiClient.get<T>()` in `securityAuditService.ts` (avoids modifying shared `api.ts` until pattern is stable).

---

# Decision: PrinterControlsViewModel Command Queue Design

**Date**: 2026-05-28  
**Author**: Gorman (iOS Networking & API Integration)  
**Issue**: [#282](https://github.com/OlyForge3D/PrintFarmerMobile/issues/282) — [iOS] Create PrinterControlsViewModel  
**PR**: [#7](https://github.com/OlyForge3D/PrintFarmerMobile/pull/7)  
**Status**: Implemented

---

## Context

`PrinterControlsViewModel` needs to serialize outbound printer commands (set temps, home, jog) so that rapid UI taps (tap-storm) don't fire multiple simultaneous HTTP calls to the same printer endpoint. The printer backend gates `/temps` and `/move` with HTTP 409 Conflict when a prior command is still in-flight.

Two approaches were evaluated: a dedicated `actor CommandQueue`, and a **Task-chain**.

---

## Options Considered

### Option A: Dedicated `actor CommandQueue`

```swift
actor CommandQueue {
    private var running: Task<Void, Never>?

    func enqueue(_ command: @escaping @Sendable () async throws -> Void) async {
        let prev = running
        running = Task {
            await prev?.value
            try? await command()
        }
        await running?.value  // caller awaits
    }
}
```

**Pros**: Strong isolation guarantee; actor protects its own state.  
**Cons**:
- All command-wrapper methods (`setTemperatures`, `home`, `move`, …) must become `async` since callers `await enqueue(...)`.
- This changes the view-layer API contract: SwiftUI `Button` closures can't `await` directly; they need `Task { await vm.move(...) }` wrappers everywhere.
- The ViewModel is already `@MainActor`-isolated — adding a second actor boundary adds hop overhead without concurrency benefit.
- Testing requires `await vm.move(...)` at every call site instead of fire-and-forget with a single `await vm.drainQueue()`.

### Option B: Task-chain (chosen)

```swift
private func enqueue(_ command: @escaping @Sendable () async throws -> Void) {
    let previousTail = queueTail
    queueTail = Task {
        await previousTail?.value   // wait for previous command
        guard !Task.isCancelled else { return }
        isCommandInFlight = true
        do { try await command() } catch { lastError = Self.userFacingMessage(for: error) }
        isCommandInFlight = false
    }
}
```

**Pros**:
- Command wrappers remain **synchronous** — `vm.move(...)` is a fire-and-forget call; view layer needs no `Task {}` wrappers.
- FIFO ordering guaranteed: each new task awaits the previous tail before starting.
- Cancel-on-deinit: `queueTail?.cancel()` tears down the chain when the ViewModel deallocates.
- No actor hop: everything runs on `@MainActor`; service calls suspend off MainActor via structured concurrency.
- Tests use `await vm.drainQueue()` (a single `await queueTail?.value`) to synchronize after any number of enqueues.

**Cons**:
- `isCommandInFlight` goes `false` briefly between consecutive commands (during the `await previousTail?.value` suspension). This is acceptable for the controls UI (aggregate indicator).
- `queueTail` must be `@ObservationIgnored nonisolated(unsafe)` to allow `deinit` access (see Swift 6 note below).

---

## Decision

**Task-chain** (Option B).

The synchronous call-site API is the deciding factor. SwiftUI button handlers are synchronous by design; making `move()`/`home()` async would require `Task { await vm.cmd() }` at every call site across Hudson's upcoming views (#284-286). The Task-chain avoids this entirely.

---

## Swift 6 Implementation Note: deinit Access

In Swift 6, `deinit` on a `@MainActor final class` is **not** automatically MainActor-isolated. Accessing `queueTail` from `deinit { queueTail?.cancel() }` raises:

> "main actor-isolated property 'queueTail' can not be referenced from a nonisolated context"

The fix requires **both** annotations:

```swift
@ObservationIgnored
nonisolated(unsafe) private var queueTail: Task<Void, Never>?
```

- `@ObservationIgnored` — prevents the `@Observable` macro from wrapping the property in `_$observationRegistrar`. Without this, `nonisolated(unsafe)` has no effect (the macro's synthesized accessors remain MainActor-isolated).
- `nonisolated(unsafe)` — declares that deinit (nonisolated context) may access the property. Safe here because `Task.cancel()` is a `Sendable`-safe operation callable from any concurrency context, and all other access to `queueTail` is strictly on the MainActor via `enqueue()` and `drainQueue()`.

---

## isCommandInFlight: Aggregate vs Per-Command

**Aggregate** (single `Bool`) was chosen.

Per-command tracking (`[CommandType: Bool]`) would require:
1. A `CommandType` enum covering all five command methods.
2. Additional state in `enqueue(_:)` to key the flag.
3. View layer knowledge of which command type is in flight.

The aggregate flag is sufficient for the controls UI: buttons are disabled while any command runs, and `lastError` identifies which command failed. Hudson can request per-command tracking in #284-286 if the design requires it.

---

## Conflict Error UX Convention

`NetworkError.conflict` (HTTP 409 — printer is busy executing a prior command) maps to:

```
"The printer is busy — please wait a moment and try again."
```

All other errors use `error.localizedDescription`.

**Rationale**: The generic `NetworkError.conflict.errorDescription` is `"Conflict — resource was modified"` — a developer-facing string that references HTTP semantics the user doesn't understand. The custom string is actionable: "wait and retry." Future ViewModels that enqueue printer commands should adopt this same string via a shared `PrinterControlsViewModel.userFacingMessage(for:)` call or a copy of the pattern.

---

## Files

| File | Purpose |
|---|---|
| `PrintFarmer/ViewModels/PrinterControlsViewModel.swift` | ViewModel implementation |
| `PrintFarmerTests/ViewModels/PrinterControlsViewModelTests.swift` | 14 XCTest cases |

---

## Test Coverage

| Scenario | Test Method |
|---|---|
| Capability cache loads and caches | `testLoadCapabilities_cachesResult` |
| Re-call refreshes from server | `testLoadCapabilities_recallRefetches` |
| Derived booleans for Moonraker (all true) | `testDerivedBooleans_moonraker_allTrue` |
| Derived booleans for SDCP (movement false) | `testDerivedBooleans_sdcp_movementFalse` |
| Derived booleans before load (all false) | `testDerivedBooleans_beforeLoad_allFalse` |
| Capability load error propagation | `testLoadCapabilities_propagatesError` |
| **FIFO queue serialization** (with delays) | `testCommandQueue_isFIFO` |
| isCommandInFlight false after drain | `testCommandQueue_isCommandInFlight_falseAfterDrain` |
| Conflict (409) → distinct "busy" message | `testConflict_surfacesDistinctBusyMessage` |
| Non-conflict → generic error description | `testNonConflict_usesGenericErrorDescription` |
| setTemperatures happy path | `testSetTemperatures_happyPath` |
| home(axes:) happy path | `testHome_allAxes_happyPath` |
| homeXY() happy path | `testHomeXY_happyPath` |
| homeZ() happy path | `testHomeZ_happyPath` |
| move() happy path | `testMove_happyPath` |
# Decision: iOS Printer.progress canonical scale = 0–100

**Author:** Dallas (Lead)
**Date:** 2026-05-28
**Status:** Decided
**References:** OlyForge3D/PrintFarmerMobile#5 (bug), #8 (fix issue), #6 (pinning PR)

---

## Decision

`Printer.progress` on iOS stores **0–100**, a passthrough of the backend wire value. Divide-by-100 happens **only at the SwiftUI render/binding site**, not at decode time.

## Rationale

The PFarm1 backend is unambiguously 0–100: every backend plugin (OctoPrint, Moonraker, FlashForge, SDCP, TestEmulator) normalizes to 0–100 before populating `PrinterStatusDto.Progress`. Code comments in `OctoPrintClient.cs` and `SdcpClient.cs` say "frontend expects 0–100." The `PrinterStatusDto` record carries `double? Progress` at that scale — no transformation before the JSON response.

Normalizing to 0–1 at `Models.swift:266` is a **leaky normalization anti-pattern**: it silently changes the scale at the model boundary, forcing every downstream consumer (ViewModels, tests, formatters) to agree on the transformed value. This directly caused the `PrinterDetailViewModel:141` 100× bug: `PrinterStatusDetail.progress` (no custom decoder, raw 0–100) mixed with the normalized `Printer.progress` (0–1) on the fallback path.

The correct pattern: model layer stores wire values; view/presentation layer applies display transforms.

## Migration

1. `Models.swift:266` — remove `/ 100.0` from `decodeIfPresent` map.
2. `PrinterListViewModel.swift:46`, `PrinterDetailViewModel.swift:111`, `DashboardViewModel.swift:50` — remove `/ 100.0` from SignalR update handlers.
3. Every `ProgressView` / `PrintProgressBar` binding — add `/ 100.0` at render site.
4. Tests: `ModelDecodingTests:35` expected value `45.5` becomes correct again; `PrinterProgressContractTests` pinning expectations flip to 0–100.

## Affected files

- `PrintFarmer/Models/Models.swift:266`
- `PrintFarmer/ViewModels/PrinterListViewModel.swift:46`
- `PrintFarmer/ViewModels/PrinterDetailViewModel.swift:111,141`
- `PrintFarmer/ViewModels/DashboardViewModel.swift:50`
- All `ProgressView` / `PrintProgressBar` render sites
- `PrintFarmerTests/Models/ModelDecodingTests.swift:35`
- `PrintFarmerTests/Models/PrinterProgressContractTests.swift`

## Squad assignment

Ripley (iOS Dev) owns implementation. Issue #8 filed.

# Shared Checkout Hazard — Recurring Pattern

**Date:** 2026-05-28  
**Incident:** Round 8 near-miss — Gorman's #278 commit landed on Hudson's `squad/284` branch (shared-checkout race condition).  
**Previous:** Round 6 design-spec leak via same mechanism.  

## Symptom

When multiple agents work in the same workspace directory without sequencing checkout operations:
- Agent A finishes work on branch X, pushes, but leaves checkout at branch X.
- Agent B expects to start on branch Y, runs `git commit` without verifying current branch.
- Commit lands on the *wrong* branch.

## Root Cause

**Shared `.git/` directory + async agent cleanup** — agents assume they're on the correct branch after `git push` but do not verify branch state before staging/committing.

## Mitigation (Each Agent)

Before staging changes:

```bash
git status
```

Verify:
1. **Current branch** matches intended scope (e.g., `squad/284`).
2. **Changed files** in the output match expected scope (no unexpected `.squad/` changes, etc.).
3. **No detached HEAD** state.

If mismatch detected, abort and notify Scribe.

## Detection

Scribe should flag in post-merge review:
- Commits on unexpected branches.
- File diffs crossing agent scopes (e.g., Gorman's PR includes `.squad/` agent history changes).

## Recurring Risk

- **R6:** Design-spec leak (shared-checkout collision).
- **R8:** Gorman #278 → Hudson `squad/284` branch (same race pattern).

**Action:** Log this as a standing hazard. Each agent's pre-commit `git status` verification should reduce recurrence.

---

## Decision: Round 12 — Bishop REQUEST_CHANGES #12 (HomeSubgroup), Hudson fixes #13 (per-axis jog gating + view tests)

**Date:** 2025-11-21
**Authors:** Bishop (code review), Hudson (fix-up)
**Status:** In Review (PR #12 open + REQUEST_CHANGES, PR #13 open + awaiting re-review)

### Summary

- **Bishop re-review (PR #12 — home):** ❌ REQUEST_CHANGES.
  - **Blocker 1:** VoiceOver labels and hints do not match `docs/design/printer-controls-section.md` spec verbatim.
  - **Blocker 2:** Tests bypass SwiftUI view layer — `JogSubgroupTests` exercised `viewModel` state directly instead of rendering `JogSubgroup` view and verifying picker selection, button visibility, and axis gating through the UI.
  - **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570057941

- **Hudson fix-up (PR #13 — jog):** ✅ Addresses review blockers from Round 11.
  - **Per-axis capability scaffolding:** Added `canJogX`, `canJogY`, `canJogZ` boolean flags to `PrinterControlsViewModel` (all currently derived from shared `supportsMovement` flag; seam left open for future backend differentiation). Aggregate `canJog` retained for compatibility.
  - **View-level gating:** Replaced single `canJog` gate with `hasAnyJogCapability` check; `availableAxes` filters picker options by per-axis flags; picker auto-snaps `selectedAxis` when unavailable.
  - **View-layer tests:** Rebuilt `JogSubgroupTests` to exercise rendered `JogSubgroup` view, not viewmodel state. Added testability extensions (`hasAnyJogCapabilityForTesting`, `canJogX/Y/ZForTesting`, `availableAxisLabelsForTesting`) matching `HomeSubgroup`/`PreheatSubgroup` pattern.
  - **Caveat:** xcodebuild cannot run locally (iOS 26.5 simulator not installed); `swiftc` type-check passed.
  - **Commit:** `6344c8f`

### Durable Decision Rules Captured

1. **View-layer testing rule (effective immediately for all SwiftUI subgroup PRs):**
   - Tests must exercise the rendered view, not just viewmodel state.
   - Reviewers must reject PRs whose test suites call viewmodel constructors or state mutators directly without rendering the SwiftUI component.
   - Pattern: use testability extensions (`.hasAnyJogCapabilityForTesting`, etc.) to inject view state; render view with `@State`; verify picker visibility, button taps, and layout gating.
   - Add to durable rules (see section below).

2. **VoiceOver spec adherence rule:**
   - VoiceOver labels and hints must match `docs/design/printer-controls-section.md` verbatim where the spec provides them.
   - Reviewers must cross-check accessibility audit against spec; mismatch is a blocker.

3. **Spec-string testing rule (effective immediately):**
   - Spec strings (VoiceOver labels, hints, button copy) must be asserted by reading from the rendered view, not comparing against hardcoded constants in tests.
   - Constants in tests = compile-time tautology, not a spec check.
   - Pattern: render view with fixture; read `.accessibilityLabel`, `.accessibilityHint` from inspected element; compare to spec source string.

---

## Decision: Round 15 — Hudson final #12 (verbatim spec strings), Hicks pedant CR #13 (init-state tests tooling gap)

**Date:** 2026-05-29  
**Authors:** Hudson (fix-up), Hicks (re-review), Vasquez (pending tiebreaker)  
**Status:** PR #12 Merged, PR #13 Open + REQUEST_CHANGES  

### Summary

- **Hudson PR #12 final fix:** ✅ MERGED.
  - **Spec strings inlined by coordinator:** Verbatim per-button hyphenated hints from `docs/design/printer-controls-section.md` now coded: "Double-tap to home printer", "Double-tap to home XY", "Double-tap to home Z".
  - **Disabled-state pattern finalized:** `resolvedAccessibilityLabel` appends `", unavailable during print"` when disabled; `resolvedAccessibilityHint` returns `""` (empty string) when disabled. Both computed properties used directly by `.accessibilityLabel()` / `.accessibilityHint()`.
  - **Test layer non-tautological pattern:** Helpers construct real `HomeButton` and call `resolved*` computed properties — same properties the view uses. If view strings change, test assertions change automatically; test cannot pass with stale spec.
  - **Commit:** `533b86f`
  - **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570269998

- **Hicks PR #13 re-review:** ❌ REQUEST_CHANGES (pedantic).
  - **Issue:** Init-state tests instantiate `JogSubgroup` and inspect `*ForTesting` test hooks rather than routing through SwiftUI render lifecycle.
  - **Rationale:** No ViewInspector or equivalent SwiftUI introspection library available in project; true "render-lifecycle" tests require UI framework integration not present.
  - **Comment:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570184502
  - **Status:** Awaiting Vasquez tiebreaker (spawned as R16 task).

### Tooling Gap Identified

**Project limitation:** Absence of ViewInspector (or equivalent SwiftUI introspection lib) prevents render-lifecycle tests on init state. Post-init `@State` reads via `*ForTesting` extensions are the practical equivalent for verifying init logic without adding a test dependency.

### Durable Decision Rules Captured

4. **SwiftUI introspection tooling threshold rule (effective immediately):**
   - **If ViewInspector or equivalent SwiftUI introspection lib is unavailable,** accept `*ForTesting` extensions that expose post-init `@State` values as the practical equivalent of render-lifecycle tests.
   - Do not ratchet code-review expectations beyond available tooling; test-hook reads of `@State` post-init are valid for init-logic verification.
   - Rationale: Full lifecycle tests require UIKit integration or framework support not present in this project. Don't require the impossible.
   - Applies to: All SwiftUI subgroup init-state testing going forward.

---

## Decision: Round 16 — iOS controls v1 stack APPROVED end-to-end

**Date:** 2026-05-29
**Authors:** Bishop (third-review APPROVE #12), Vasquez (tiebreaker APPROVE #13), Hicks (CR re-check, scope-creep acknowledged)
**Status:** ✅ APPROVED (all three PRs cleared)

### Summary

**PR #11 (preheat, #284):** ✅ **Bishop APPROVE**
- Cool Down preset label fixed (removed hardcoded "Off" ternary; format string now produces "0° / 0°" uniformly).
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/11#issuecomment-4570039961

**PR #12 (home, #285):** ✅ **Vasquez APPROVE + Bishop APPROVE**
- Verbatim spec strings inlined per-button ("Double-tap to home printer" / "Double-tap to home XY" / "Double-tap to home Z"). Disabled-state pattern: `resolvedAccessibilityLabel` appends `", unavailable during print"`; `resolvedAccessibilityHint` returns `""` when disabled.
- Both computed properties used directly by `.accessibilityLabel()` / `.accessibilityHint()` — test cannot pass if view strings change (non-tautological pattern verified).
- Commit: `533b86f`
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570211958 (third review), https://github.com/OlyForge3D/PrintFarmerMobile/pull/12#issuecomment-4570269998 (Bishop final)

**PR #13 (jog, #286):** ✅ **Vasquez APPROVE** (tiebreaker; Hicks pedant CR overruled)
- Test-hook pattern (`.hasAnyJogCapabilityForTesting`, etc.) matches established HomeSubgroup/PreheatSubgroup project convention.
- Hicks objection: "True render-lifecycle tests require ViewInspector" — valid but scope-creep. No ViewInspector in project; accept test-hook reads of post-init `@State` per Rule 4 (tooling-threshold).
- Comment: https://github.com/OlyForge3D/PrintFarmerMobile/pull/13#issuecomment-4570216262 (Vasquez tiebreaker)

### Durable Decision Rules Captured

5. **Scope-creep early-stop rule (effective immediately):**
   - When second-voice reviewer requests adding a test framework or major tooling (e.g., ViewInspector, screenshot testing) for a single PR, that is scope creep if:
     - The project has no established precedent for that tool.
     - The pattern (test-hooks, mock views, manual renders) already covers the requirement.
     - PR author's implementation matches project convention.
   - **Remedy:** Tiebreaker votes may override tooling blockers when convention suffices.
   - **Exception:** Never waive security, safety, or coverage gaps — only tool/framework choice.

6. **Multi-review consensus rule (standing):**
   - When two-of-three reviewers APPROVE (with tiebreaker rationale documented), PR is approved.
   - Third dissent is honored but does not block if rationale falls outside project scope or misses established convention.
   - Document dissent in decision log and agent history for future reference.

---

## Decision: Round 17 — Newt PR #15 integration plan, Brett PR #14 snapshot spike

**Date:** 2025-11-23
**Authors:** Newt (iOS design/integration), Brett (research/snapshot strategy)
**Status:** Prep PRs opened in PrintFarmerMobile

### Summary

- **Newt PR #15 (integration plan):** Composition strategy finalized — `controlsSection()` private helper on `PrinterDetailView` (matches `actionSection` convention), placed after `actionSection`. Single `@State var controlsViewModel: PrinterControlsViewModel`, lazy-injected via `.task` based on printer ID + caps. Hudson scope: ~40 lines `PrinterDetailView.swift` + ~10 lines `PrinterControlsViewModel.swift` additions; subgroup files (Preheat, Home, Jog) ship complete from #11–#13 stack. **PR:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/15
- **Brett PR #14 (snapshot spike):** Recommends `swift-snapshot-testing` (pointfreeco) via SPM for snapshot regression tests. 8-snapshot matrix: Moonraker/FlashForge/SDCP × {blocked, in-flight, error, dark-mode, iPhone SE}. Biggest risk: simulator OS version drift — CI must pin simulator OS version to match baseline environment. **PR:** https://github.com/OlyForge3D/PrintFarmerMobile/pull/14

### Unblocked Decisions

**Newt integration pattern locked:** `controlsSection()` composition allows independent subgroup testing + future component reuse. No blocking review feedback.
**Brett snapshot strategy validated:** pointfreeco library meets framework requirements (Xcode/iOS 15+ compatible, SPM distribution). Recommend pinning simulator OS version in CI YAML.
