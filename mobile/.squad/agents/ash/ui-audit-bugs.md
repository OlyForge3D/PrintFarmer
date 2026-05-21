# UI Code Audit — Bug Report

**Auditor:** Ash (Tester/QA)
**Date:** 2025-07-18
**Scope:** All SwiftUI views, ViewModels, Models, Services, Extensions
**Total bugs found:** 10
**Severity breakdown:** 3 High, 5 Medium, 2 Low

---

## Critical / High Severity

### BUG-1: "Available" spool filter always returns zero results — backend `inUse` fallback logic inverted
**Severity:** High
**File(s):** `~/s/PFarm1/src/infra/Parsing/SpoolmanJsonParser.cs` (lines 112-119), observed via `PrintFarmer/ViewModels/SpoolInventoryViewModel.swift` (line 57)
**Description:** The backend's `SpoolmanJsonParser` has a fallback for when the Spoolman API doesn't return an `in_use` field. It falls back to `!archived` — but `archived: false` (the default for most spools) results in `inUse = true`, marking ALL non-archived spools as "in use." The iOS filter `!spool.inUse && !(spool.archived ?? false)` then correctly returns zero results because every spool has `inUse == true`.
**Expected:** Spools not actively loaded on a printer should have `inUse = false` and appear in the "Available" filter.
**Actual:** All non-archived spools are marked `inUse = true` when Spoolman doesn't return an explicit `in_use` field, making the Available filter return zero results.
**Suggested fix:** In `SpoolmanJsonParser.cs`, change the fallback logic. When `in_use` is absent and `archived` is `false`, default `inUse` to `false` (not in use), not `!archived`. The fallback `inUse = !archived` conflates "not archived" with "in use," which are different concepts. Proposed fix:
```csharp
// Instead of: inUse = !archived.Value;
// Use: only set inUse=true if archived is explicitly true (meaning NOT in use)
if (archived.HasValue && archived.Value)
    inUse = false; // archived → definitely not in use
// else leave inUse as null → defaults to false at line 153
```
**Owner:** Lambert (backend networking concern, but ultimately a backend fix)

---

### BUG-2: SettingsView URL change doesn't update APIClient or force re-login
**Severity:** High
**File(s):** `PrintFarmer/Views/Settings/SettingsView.swift` (lines 97-101)
**Description:** When the user changes the server URL in Settings, it only writes to `UserDefaults`. It does NOT call `APIClient.updateBaseURL()` or force the user to log out. The UI message says "You will need to sign in again" but nothing enforces this. All subsequent API calls continue hitting the OLD server URL until the app is restarted.
**Expected:** Changing the server URL should either (a) update the APIClient's base URL and log the user out, or (b) at minimum call `APIClient.updateBaseURL()` so subsequent calls go to the new server.
**Actual:** Only `UserDefaults` is updated. APIClient keeps using the old URL. The user stays logged in to the old server with stale credentials.
**Suggested fix:** After saving the new URL, call `await authViewModel.logout()` which will clear auth state and force re-login. The APIClient will pick up the new URL on next login via `AuthService.login(serverURL:)`.
```swift
Button("Save") {
    let trimmed = newServerURL.trimmingCharacters(in: .whitespacesAndNewlines)
    if !trimmed.isEmpty {
        UserDefaults.standard.set(trimmed, forKey: APIClient.serverURLKey)
        Task { await authViewModel.logout() }
    }
}
```
**Owner:** Ripley

---

### BUG-3: PrinterDetailView NFC scanner never configured — scan always fails
**Severity:** High
**File(s):** `PrintFarmer/Views/Printers/PrinterDetailView.swift` (lines 56-59), `PrintFarmer/ViewModels/PrinterDetailViewModel.swift` (lines 65-67, 77-79)
**Description:** The `.task` block in `PrinterDetailView` calls `viewModel.configure(printerService:)` but never calls `viewModel.configureNFCScanner()`. The `nfcScanner` property remains `nil`. When the user taps the NFC scan button (line 115), `handleNFCScanToLoad()` hits the guard `let nfcScanner` check and always shows "NFC scanning is not available on this device."
**Expected:** NFC scanning should work on the printer detail page, allowing users to scan a spool tag to load filament.
**Actual:** NFC scan always fails with "not available" error because the scanner service is never injected.
**Suggested fix:** Add `viewModel.configureNFCScanner(services.nfcService)` to the `.task` block:
```swift
.task {
    viewModel.configure(printerService: services.printerService)
    #if canImport(UIKit)
    viewModel.configureNFCScanner(services.nfcService)
    #endif
    await viewModel.loadPrinter()
}
```
**Owner:** Ripley

---

## Medium Severity

### BUG-4: "Empty" spool filter false positive when remaining weight is nil
**Severity:** Medium
**File(s):** `PrintFarmer/ViewModels/SpoolInventoryViewModel.swift` (lines 66-71), `PrintFarmer/ViewModels/SpoolPickerViewModel.swift` (lines 62-66)
**Description:** The `.empty` filter case treats a spool with `remainingWeightG == nil` and `initialWeightG != nil` as empty. This is incorrect — nil remaining weight means the data hasn't been tracked yet (e.g., a newly added spool that hasn't been weighed), not that the spool is empty.
**Expected:** Spools with unknown remaining weight should NOT appear in the "Empty" filter.
**Actual:** Any spool with an initial weight but no remaining weight data shows up as "Empty."
**Suggested fix:** Remove the `else if` fallback. Only treat a spool as empty when `remainingWeightG` is explicitly `0`:
```swift
case .empty:
    guard let remaining = spool.remainingWeightG else { return false }
    return remaining == 0
```

---

### BUG-5: AddSpool color picker selection is never sent to backend
**Severity:** Medium
**File(s):** `PrintFarmer/ViewModels/AddSpoolViewModel.swift` (lines 114-119), `PrintFarmer/Models/FilamentModels.swift` (lines 84-94)
**Description:** The AddSpoolView has a full color picker UI (hex swatches, text input), but the `SpoolmanSpoolRequest` model has no `colorHex` field. The selected color is stored in `viewModel.colorHex` but never included in the create request. Users pick a color thinking it will be saved, but it's silently discarded.
**Expected:** The selected color should be sent to the backend and associated with the new spool.
**Actual:** Color selection is cosmetic only — the spool is created without the user's chosen color.
**Suggested fix:** Either (a) add `colorHex` to the request model if the backend supports it, or (b) remove the color picker from AddSpoolView to avoid misleading users. The color should come from the filament association, so at minimum add a note explaining this in the UI.

---

### BUG-6: Multiple delete Tasks create race condition on spool array
**Severity:** Medium
**File(s):** `PrintFarmer/Views/Filament/SpoolInventoryView.swift` (lines 254-258)
**Description:** When deleting multiple spools via swipe, each spool spawns a separate `Task { await viewModel.deleteSpool(spool) }`. Each `deleteSpool` call removes the spool from the `spools` array after the API call succeeds. With concurrent Tasks, the second delete may operate on a stale snapshot of the array, or the UI may animate incorrectly because the data source changes mid-animation.
**Expected:** Deletes should be sequential or batched to avoid array mutation conflicts.
**Actual:** Concurrent Tasks mutate the same array, risking inconsistent UI state.
**Suggested fix:** Collect all spools to delete, then delete them sequentially in a single Task:
```swift
.onDelete { indexSet in
    let spoolsToDelete = indexSet.map { viewModel.filteredSpools[$0] }
    Task {
        for spool in spoolsToDelete {
            await viewModel.deleteSpool(spool)
        }
    }
}
```

---

### BUG-7: NFCWriteView always reports failure (hardcoded false callback)
**Severity:** Medium
**File(s):** `PrintFarmer/Views/Filament/SpoolInventoryView.swift` (lines 122-128)
**Description:** The `NFCWriteView` sheet's `onWrite` closure always returns `false`. The comment says "Placeholder: Lambert's NFCService.writeTag() will be called here." Users who attempt NFC tag writing will always see "Failed to write NFC tag."
**Expected:** NFC tag writing should either work or the button should be hidden/disabled until the service is implemented.
**Actual:** Users can access the write UI but it always fails with no indication it's unimplemented.
**Suggested fix:** Either wire up the NFC write service or hide the "Write NFC Tag" context menu item until the feature is ready. A stub that always fails is a bad UX.

---

### BUG-8: NotificationRow onTapGesture conflicts with List swipe actions
**Severity:** Medium
**File(s):** `PrintFarmer/Views/Notifications/NotificationsView.swift` (lines 86-88)
**Description:** Using `.onTapGesture` on a List row interferes with SwiftUI's built-in row interaction and swipe gesture recognizers. This can cause: (a) rows not highlighting on tap, (b) swipe-to-delete/mark-read becoming unreliable, (c) VoiceOver not announcing the row as a tappable element.
**Expected:** Tapping a notification should reliably mark it as read and navigate to the related job.
**Actual:** Tap and swipe gestures can conflict, making interaction unreliable especially with VoiceOver.
**Suggested fix:** Replace `.onTapGesture` with a `Button` wrapping the row content, or use `NavigationLink` for the tap action and keep swipe actions separate.

---

## Low Severity

### BUG-9: JobDetailView progress can show 100% with "0m remaining" while job is still printing
**Severity:** Low
**File(s):** `PrintFarmer/Views/Jobs/JobDetailView.swift` (lines 98-111)
**Description:** Progress is calculated as `elapsed / estimatedPrintTime`. When a print takes longer than the estimate (common), progress clamps to 100% and the "remaining" label disappears. The user sees a full progress bar but the job is still running, with no visual indication of the overshoot.
**Expected:** Progress should reflect actual printer-reported progress, or at minimum show "overtime" state when elapsed exceeds the estimate.
**Actual:** Progress bar shows 100% and time label disappears while the print continues, confusing users.
**Suggested fix:** Add an overtime indicator, e.g.: if `elapsed > eta`, show "Overtime: +X minutes" instead of hiding the time label.

---

### BUG-10: `.constant()` binding anti-pattern for alert dismissal (5 instances)
**Severity:** Low
**File(s):**
- `PrintFarmer/Views/Filament/SpoolInventoryView.swift` (line 101)
- `PrintFarmer/Views/Filament/SpoolPickerView.swift` (line 99)
- `PrintFarmer/Views/Printers/PrinterDetailView.swift` (lines 49, 73)
- `PrintFarmer/Views/Jobs/JobDetailView.swift` (line 45)
**Description:** These alerts use `.constant(viewModel.someError != nil)` as the `isPresented` binding. This creates a new constant binding on each view evaluation rather than a proper two-way binding. While it works in most cases (the view re-evaluates after the button sets the error to nil), it's fragile and can cause alert flicker in rapid state change scenarios.
**Expected:** Alerts should use proper `Binding` instances that SwiftUI can read AND write.
**Actual:** Works most of the time but is an anti-pattern that can cause subtle UI glitches.
**Suggested fix:** Use a computed `Binding`:
```swift
.alert("Error", isPresented: Binding(
    get: { viewModel.actionError != nil },
    set: { if !$0 { viewModel.actionError = nil } }
)) { ... }
```

---

## Summary

| Severity | Count | Key Issues |
|----------|-------|------------|
| High     | 3     | Available filter broken, Settings URL change broken, NFC on printer detail broken |
| Medium   | 5     | Empty filter false positive, color not saved, delete race condition, NFC write stub, notification tap conflict |
| Low      | 2     | Progress bar overtime, alert binding anti-pattern |
| **Total**| **10** | |

## Top 3 for GitHub Issues
1. BUG-1: Available filter always empty (squad:lambert — backend fix needed)
2. BUG-3: PrinterDetailView NFC scanner never configured (squad:ripley — 1-line fix)
3. BUG-2: SettingsView URL change doesn't update APIClient (squad:ripley)
