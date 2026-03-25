## Pending Ready compact-card fallback

- **Context:** `CompactPrinterCard` and `BedClearBanner` were only keying off `autoDispatchStatus.state === PendingReady`.
- **Decision:** Treat a failed `readyGateChecks["Bed Clear Confirmed"]` gate as the same operator-facing state as `PendingReady`.
- **Why:** The backend’s bulk/per-printer auto-dispatch payload already carries the real operator gate and attention message. If the row’s summary `state` is stale or flattened, the UI must still show `Pending Ready` and mount the banner.
- **Touched paths:**
  - `src/Web/ReactApp/src/common/utils/printerStateDisplay.ts`
  - `src/Web/ReactApp/src/features/printers/components/CompactPrinterCard.tsx`
  - `src/Web/ReactApp/src/features/printers/components/BedClearBanner.tsx`
  - related consistency surfaces: `DetailedPrinterCard`, `PrinterTableView`, `PrinterDetailsSidebar`, `PrintersPage`, `Layout`
