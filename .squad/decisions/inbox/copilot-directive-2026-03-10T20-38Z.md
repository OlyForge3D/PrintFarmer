### 2026-03-10T20:38Z: Auto-Print vs Auto-Dispatch separation

**By:** Jeff Papiez (via Copilot)

**What:**

1. **Auto-Print and Auto-Dispatch are two separate features:**
   - **Auto-Print** = per-printer hardware capability (automatic bed clearing after print completion). Future feature — no current printers support it. Should be a setting in the Add/Edit Printer modal, NOT on print cards or queue dashboard.
   - **Auto-Dispatch** = system automatically sends queued jobs to ready/idle printers. This is what the current "Auto-Print" toggle was actually being used for. Should have both a system-level toggle (on queue dashboard) and per-printer opt-in (icon toggle on printer cards).

2. **Remove "Auto-Print" toggle from printer cards and queue dashboard.** Replace with Auto-Dispatch controls:
   - Queue dashboard: system-level Auto-Dispatch toggle (replaces current Auto-Print toggle)
   - Printer cards: icon toggle for per-printer auto-dispatch opt-in (replaces label+toggle)

3. **No unassigned jobs in the queue.** If auto-assign can't find a matching printer, the file should NOT be queued. User must manually select a printer. (Reverses the recent change that created unassigned jobs.)

4. **No idle threshold delay for upload-and-print.** If printer is available and ready, dispatch immediately. No artificial delay.

5. **"Ready" flag:** After a print completes, user needs to indicate the printer is ready for the next print (bed cleared). This is the gate between consecutive prints, not between first upload-and-print.

6. **Smart dispatching:** If only one printer of a required type exists, queue to it and print automatically.

**Why:** User request — clarifying the design intent for the auto-print/auto-dispatch system. The current naming conflates two different concepts.
