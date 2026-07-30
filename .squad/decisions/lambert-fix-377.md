# Lambert Fix #377 — NFC Pairing Modal SignalR Wiring

**Date:** 2025-07-09  
**Author:** Lambert (Backend, covering frontend lockout)  
**PR:** #377 fix on `squad/361-nfc-pairing-modal`  
**Blocker resolved:** Bishop (NFC modal/hook not wired; `/api/nfc/link` returning 501)

---

## Problem

The `NfcPairingModal` and `useNfcPairingSession` were completely unwired:

1. `useNfcPairingSession` hook did not exist.
2. `useNfcEvents` was pointing at `/hubs/printers` — NFC events come from `/hubs/nfc` (PR #383 contract).
3. The existing NFC event types used fields (`deviceId`, `deviceName`, `scannedAt`) that don't match what the backend actually sends.
4. `NfcLinkRequest` passed `deviceId` instead of `printerId` to `POST /api/nfc/link`.
5. The modal had no "waiting for tag" (scanning) step and no "unavailable" (hub dropped) step.
6. Nothing in `Layout.tsx` rendered the pairing modal globally.

---

## Decisions Made

### 1. Separate `nfcHubService` from `PrinterSignalRService`

PR #383 routes NFC events exclusively through `/hubs/nfc`, not `/hubs/printers`. Created a dedicated `nfcHubService` singleton (`src/services/nfcHubService.ts`) that owns the `/hubs/nfc` connection. Removed dead NFC listeners from `PrinterSignalRService` to avoid confusion.

**Why not reuse printer hub?** The backend `NfcTagService` injects `IHubContext<NfcHub>` — events only reach `/hubs/nfc` clients. Keeping them in the printer service was dead code.

### 2. Type alignment with PR #383 backend contract

Replaced the speculative frontend event types with the actual backend payloads:
- `nfctagunknown`: `{ tagUid, printerId?, readAt }` — simplified, no `deviceId`/`deviceName`
- `nfctagread`: `{ tagUid, spoolId, spoolName?, printerId?, trayId?, readAt }` — replaces `nfctagknown`
- `NfcLinkRequest`: `{ tagUid, spoolId, printerId?, trayId? }` — matches `LinkNfcTagRequest` C# DTO

`NfcTagMismatchEvent` and `NfcReaderOfflineEvent` types removed — not in PR #383 backend contract. Can be re-added when that backend work ships.

### 3. Global wiring via Layout

The pairing modal should respond to any unknown NFC tag scan regardless of which page is active. Added `useNfcPairingSession` to `Layout.tsx` and rendered `<NfcPairingModal session={...} />` globally. This follows the same pattern as auth modals (LoginModal, RegisterModal) already in Layout.

### 4. Modal step derivation over effects

Avoided `react-hooks/set-state-in-effect` lint errors by deriving the `unavailable` and `scanning` steps as render-time conditional state updates (same pattern as the existing `trackedEvent` reset), rather than syncing via `useEffect`.

### 5. `isUnavailable` signal for dropped hub

When the `/hubs/nfc` connection drops while the modal is open, `isUnavailable` flips true and the modal shows an amber warning step with a "Close" button. Connection restoration is handled passively — the next `nfctagunknown` event clears the flag.

---

## Files Changed

| File | Change |
|---|---|
| `src/services/nfcHubService.ts` | **new** — `/hubs/nfc` singleton service |
| `src/features/nfc/types.ts` | Updated to match PR #383 backend payloads |
| `src/features/nfc/hooks/useNfcPairingSession.ts` | **new** — session state machine |
| `src/features/nfc/components/NfcPairingModal.tsx` | Added scanning/unavailable steps; fixed link payload |
| `src/features/nfc/hooks/useNfcEvents.ts` | Switched to nfcHubService; nfctagread replaces nfctagknown |
| `src/services/printer-signalr.ts` | Removed dead NFC event handlers |
| `src/common/components/Layout.tsx` | Wired NfcPairingModal globally |
| `src/test/features/nfc/useNfcPairingSession.test.ts` | **new** — 10 tests for SignalR state transitions |

---

## Test Coverage

10 new tests in `useNfcPairingSession.test.ts` covering:
- Initial state (closed, no tag)
- `nfctagunknown` → modal opens with captured tag
- Multiple tags → latest tag wins
- `startScanning()` → modal opens in scanning mode
- `close()` → full reset
- Hub drop while open → `isUnavailable = true`
- Hub drop while closed → no-op
- Reconnect via new tag → clears unavailable
- Unmount → subscriptions cleaned up

All 10 pass. Pre-existing 10 failures in unrelated test files (PrinterCostFields, FailureDetectionMonitoringOverlay, NewSliceJobPage, metadata-editors) pre-date this branch and are not introduced by these changes.
