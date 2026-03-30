### 2026-03-26T15:41Z: Expose MachineHourlyRate and Wattage on printer modals
**By:** Jeff Papiez (via Copilot)
**What:** The Edit Printer and Add Printer modals must expose MachineHourlyRate and Wattage fields so users can configure per-printer cost overrides from the UI.
**Why:** User request — these fields exist on the Printer entity but aren't accessible through the frontend. Users need to set per-printer energy and machine cost overrides without touching the database directly.
