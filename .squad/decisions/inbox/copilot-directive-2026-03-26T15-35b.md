### 2026-03-26T15:35Z: Fix job scheduling UX — add job picker
**By:** Jeff Papiez (via Copilot)
**What:** The ScheduleModal's raw Job ID text input must be replaced with a searchable job picker. Also add a "Schedule" action on jobs in the queue page so the modal opens pre-populated.
**Why:** User request — current UX requires manually typing a 36-character GUID with no way to discover valid job IDs.
