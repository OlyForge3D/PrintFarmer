# Start Harvest Page Redesign: Wireframe & Implementation Plan

## Goals
- Unify the Start Harvest and HarvestOperationDetails UIs for a consistent, actionable workflow.
- Merge harvest controls and printer status into a single scalable card per printer.
- Support hundreds of printers with fast, filterable, and responsive UI.
- Enable real-time feedback, error handling, and batch actions.

---

## Wireframe (Textual)

```
+---------------------------------------------------------------+
| [Header: Start Harvest]                                       |
| [Search] [Filter] [Group By] [Bulk Actions]                   |
+---------------------------------------------------------------+
| [Printer Card Grid/List - Virtualized]                        |
|                                                               |
| +-------------------+  +-------------------+                  |
| | Printer Name      |  | Printer Name      |   ...             |
| | [Status Icon]     |  | [Status Icon]     |                  |
| | [Model/Location]  |  | [Model/Location]  |                  |
| | [Harvest Progress]|  | [Harvest Progress]|                  |
| | [Errors/Warnings] |  | [Errors/Warnings] |                  |
| | [Start/Cancel Btn]|  | [Start/Cancel Btn]|                  |
| | [Settings Btn]    |  | [Settings Btn]    |                  |
| | [Details Btn]     |  | [Details Btn]     |                  |
| +-------------------+  +-------------------+                  |
|                                                               |
+---------------------------------------------------------------+
| [Selected Printer Details/HarvestOperationDetails Panel]       |
+---------------------------------------------------------------+
```

- **Header:** Page title, global actions (start all, cancel all, etc.)
- **Search/Filter/Group:** Controls for finding and organizing printers
- **Printer Card Grid/List:**
  - Virtualized for performance (react-window/react-virtualized)
  - Each card shows:
    - Printer name, status, model/location
    - Harvest progress bar, error/warning icons
    - Start/cancel, settings, and details buttons
    - Real-time updates (SignalR)
- **Details Panel:**
  - Shows HarvestOperationDetails for selected printer/operation
  - Includes discovered files, per-file progress, error actions

---

## Implementation Plan

### 1. Unify UI Components
- [x] Replace Start Harvest page content with HarvestOperationDetails for selected printer/operation
- [x] Refactor HarvestOperationDetails to support both history and live/active use

**2025-09-21: Step 1 complete.**
- The Start Harvest page now uses the unified HarvestOperationDetails panel for selected/active operations, with real-time updates and card click-to-details behavior.
- HarvestOperationDetails refactored to support both modal and inline panel usage, with flexible props for shared use.
- Next: Proceed to Printer Card redesign and virtualized layout for scalability.

### 2. Printer Card Redesign
- [x] Create new PrinterCard component with merged controls and status
- [x] Integrate harvest options (start/cancel/settings) into each card
- [x] Show real-time status and progress on card

**2025-09-21: Printer Card Redesign complete.**
- PrinterCard component created and integrated into the Start Harvest page grid
- Merged controls for start, cancel, settings, and details
- Real-time status and progress shown per printer
- PrintFarmer palette and accessibility patterns applied

### 3. Scalability & Layout
- [x] Implement virtualized grid/list for printer cards
- [x] Add search, filter, and grouping controls
- [x] Add compact/expanded card toggle

### 4. Responsive & Accessible UI
- [x] Use grid/masonry layout for cards
- [x] Ensure full keyboard and screen reader accessibility
- [x] Test on mobile and desktop

### 5. Actionable Controls & Feedback
- [x] Add batch actions (start/cancel all, retry/skip errored, etc.)
- [x] Show toast/alert feedback for actions
- [x] Ensure all actions update UI in real time

**2025-09-21: Batch actions and feedback complete.**
- Batch Start and Cancel All actions implemented above the printer grid
- Cancel All is fully wired to backend mutation, with per-operation and summary feedback
- Button disables while cancels are pending; errors and partial failures are clearly reported
- UI is actionable and robust for large printer fleets

### 6. Documentation & Progress Tracking
- [x] Update this .md file as each step is completed
- [ ] Add screenshots/wireframes as UI evolves

---

## Notes
- Use PrintFarmer palette and design system for all new UI.
- Prioritize performance and usability for large printer fleets.
- All controls must be actionable and provide immediate feedback.
- Real-time updates via SignalR are required for all status/progress.

---

## Troubleshooting: React-window & React 19

If you see an npm error about `react-window@1.8.7` and React 19 peer dependencies:

```
npm install --legacy-peer-deps
```

This switch allows installation to proceed despite peer dependency warnings. Alternatively, consider migrating to a virtualization library that supports React 19, such as `@tanstack/react-virtual`.

---

**Progress:**
- [x] Step 1: Unify UI Components
- [x] Step 2: Printer Card Redesign
- [x] Step 3: Scalability & Layout
- [x] Step 4: Responsive & Accessible UI
- [x] Step 5: Actionable Controls & Feedback
- [x] Step 6: Documentation & Progress Tracking (this file updated)
