# G-code Harvest UX/Progress Overhaul Plan

## Objective
Deliver a robust, user-friendly G-code harvest experience that provides real-time per-file progress, error handling, and actionable review of discovered files, even after cancellation.

---

## Plan Steps

### 1. Analyze Current State
- Review frontend harvest progress UI and SignalR event handling
- Review backend harvest operation logic and SignalR/API event emission
- Identify gaps in per-file progress, file discovery, error reporting, and completion/cancellation handling

### 2. Backend Enhancements
- Ensure backend emits per-file progress events (file path, status, thumbnail, error if any) via SignalR
- Ensure backend includes discovered files and errors in operation state (API and SignalR)
- Ensure operation completion/cancellation is reported with all discovered files and their statuses
- Add endpoints or event payloads for retry/skip actions on errored files if not present

### 3. Frontend Real-Time Progress & File List
- Update frontend to listen for per-file progress events
- Display discovered files in real time, with thumbnails if available
- Show per-file status (pending, added, skipped, errored, etc.)
- Show overall progress and allow cancellation

### 4. Error Handling & Actions
- Display errors for individual files inline
- Provide UI controls to skip or retry errored files
- Allow user to review and act on files after cancellation (e.g., add, skip, retry)

### 5. Post-Cancel/Complete Review
- After completion or cancellation, show all discovered files and their statuses
- Allow user to take action on any files not yet added (e.g., add, skip, retry)
- Persist operation state for later review in history

### 6. UI/UX Polish
- Ensure responsive, accessible, and visually clear UI for all states
- Add loading, empty, and error states as needed
- Add tooltips/help for new controls

### 7. Testing & Validation
- Manual and automated tests for all new flows
- Validate with real printers and simulated errors

---



## Progress Tracking

- [x] 1. Analyze Current State
	- Frontend and backend gaps identified.
- [x] 2. Backend Enhancements
	- Backend emits per-file progress events (file path, status, thumbnail, error if any) via SignalR.
	- Discovered files/errors included in operation state.
- [x] 3. Frontend Real-Time Progress & File List
	- Frontend listens for per-file progress events and updates file list in real time.
	- UI shows discovered files, status, and errors inline.
 - [x] 4. Error Handling & Actions
	- Display errors for individual files inline.
	- Provide UI controls to skip or retry errored files.
	- Allow user to review and act on files after cancellation.
- [x] 5. Post-Cancel/Complete Review
	- After completion or cancellation, show all discovered files and their statuses.
	- Allow user to take action on any files not yet added.
	- Persist operation state for later review in history.
- [x] 6. UI/UX Polish
	- Responsive, accessible, and visually clear UI for all states.
	- Loading, empty, and error states added. Tooltips/help for new controls.
- [ ] 7. Testing & Validation
	- Manual and automated tests for all new flows.
	- Validate with real printers and simulated errors.

---



## What Remains

- Finalize and run comprehensive tests (manual and automated).

---

## Notes
- This plan will be updated as new requirements or discoveries arise.
- Each step will be marked complete as work progresses.
