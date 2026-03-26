# Decision: Job Scheduling UX — Job Picker

**Author:** Ripley (Frontend Dev)
**Date:** 2026-03-27
**Status:** Implemented

## Context

The `ScheduleModal` required users to manually type a 36-character GUID into a text input to schedule a job. No discovery or browsing mechanism existed.

## Decision

Replaced the raw text input with a `Select` dropdown that:
- Fetches available jobs via `apiClient.getJobQueue()` with `useQuery`
- Filters to only Queued/Assigned status (not Printing, Completed, etc.)
- Shows `{jobName} — {printerName || 'Unassigned'}` per option
- Supports pre-selection via the existing `jobId` prop
- Shows an empty state message when no schedulable jobs exist

Added a "Schedule" action button on each Queued/Assigned job row in `QueueJobsTable`, wired through `PrintQueueDashboardPage` to open the modal with that job pre-filled.

## Files Changed

- `src/Web/ReactApp/src/features/scheduling/components/ScheduleModal.tsx`
- `src/Web/ReactApp/src/features/queue/components/QueueJobsTable.tsx`
- `src/Web/ReactApp/src/features/queue/pages/PrintQueueDashboardPage.tsx`
- `src/Web/ReactApp/src/test/features/scheduling/ScheduleModal.test.tsx` (new)
