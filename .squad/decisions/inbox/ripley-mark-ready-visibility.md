# Decision: Auto-Print Action Button Visibility Logic

**Author:** Ripley (Frontend Dev)
**Date:** 2025-07-25
**Status:** Implemented

## Context

The Auto-Print Dashboard showed "Mark Ready", "Skip", and "Cancel" buttons unconditionally on all printer cards regardless of printer state. This meant users could see "Mark Ready" on a printer that was actively printing — a confusing UX since the bed is obviously not clear.

## Decision

Action buttons are now conditionally rendered based on the printer's auto-print workflow state (`state` field) and whether it's actively printing (`currentJobName`):

| Button | Shown When | Rationale |
|--------|-----------|-----------|
| **Mark Ready** | `state === 'PendingReady'` AND not printing | Only meaningful when printer is waiting for bed-clear confirmation |
| **Skip** | `state === 'PendingReady'` AND `queueDepth > 0` | Only skip when awaiting confirmation and there's a job to skip |
| **Cancel** | `currentJobName` exists (actively printing) | Only cancel when there's an active print |

## Changes

- Added missing `state` field to frontend `AutoPrintStatus` TypeScript type (was already sent by backend but not consumed)
- Added `Printing` and `Awaiting Bed Clear` status badges for better visual feedback
- Updated tests to cover the new visibility logic (6 new test cases)

## Impact

Frontend-only change. No backend modifications needed — the `state` field was already being serialized.
