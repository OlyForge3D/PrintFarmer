# Phase 1b: Validation & Testing - Manual Checklist

**Date**: January 8, 2026  
**Status**: Ready for Manual Verification  
**Objective**: Validate Print Queue Dashboard functionality before production deployment

---

## ✅ Pre-Validation Status

### Automated Tests (PASSING)
- ✅ **Frontend Unit Tests**: 292/292 passing
  - QueueFiltersBar: 9/9 tests
  - QueueJobsTable: 14/14 tests
  - All filter logic validated
  - All user interactions mocked and verified

- ✅ **Backend Unit Tests**: 1634/1634 passing
  - PrintQueueService: All CRUD operations tested
  - GcodeUploadQuotaService: Bug fix validated
  - All database operations verified

### Code Quality (PASSING)
- ✅ ESLint: 0 errors, 0 warnings
- ✅ TypeScript compilation: 0 errors
- ✅ .NET build: 0 errors
- ✅ PrintFarmer design system: 100% applied

### Servers Running
- ✅ API Server: http://127.0.0.1:5245 (Running on port 5245)
- ✅ Frontend Server: http://127.0.0.1:8080 (Running on port 8080)
- ✅ Database: PostgreSQL (Healthy)
- ✅ SignalR Hub: /hubs/printers (Functional)

---

## 🧪 Manual Testing Checklist

### Section 1: Dashboard Loading & Navigation

**Test 1.1: Open Dashboard**
- [ ] Navigate to http://127.0.0.1:8080/printQueue
- [ ] Page loads without errors
- [ ] Title shows "PRINTFARMER" or "Print Queue"
- [ ] No JavaScript console errors (F12 → Console tab)

**Test 1.2: Component Visibility**
- [ ] Filter bar is visible at top of page
- [ ] Job list/table is visible below filters
- [ ] Empty state message appears if no jobs: "No jobs in queue"
- [ ] Loading state shows "Loading jobs..." when fetching
- [ ] All components use PrintFarmer styling (consistent colors, spacing)

**Test 1.3: Navigation Integration**
- [ ] Page accessible from main navigation
- [ ] Breadcrumb or nav shows current location
- [ ] Can navigate away and back without issues

---

### Section 2: Filter Functionality

**Test 2.1: Status Filter**
- [ ] Status dropdown is visible and interactive
- [ ] Options include: "All", "Queued", "Printing", "Paused", "Completed", "Failed", "Cancelled"
- [ ] Selecting a status filters the job list
- [ ] Clearing filter shows all jobs again
- [ ] No API errors in console during filter

**Test 2.2: Model Filter**
- [ ] Model dropdown is visible and interactive
- [ ] Shows all available printer models
- [ ] Selecting a model filters jobs to that printer model only
- [ ] Filter works correctly with other filters applied simultaneously

**Test 2.3: Material Filter**
- [ ] Material dropdown is visible and interactive
- [ ] Shows available material types
- [ ] Selecting material filters jobs correctly
- [ ] Works in combination with other filters

**Test 2.4: Refresh Button**
- [ ] Refresh button is visible
- [ ] Clicking refresh re-fetches job list
- [ ] Loading state shows during refresh
- [ ] Updated data displays after refresh

**Test 2.5: Clear Filters**
- [ ] "Clear All" button resets all filters to default
- [ ] Full job list displays after clearing
- [ ] No filter state is preserved after clearing

**Test 2.6: Filter Combinations**
- [ ] Apply Status=Queued + Model=X + Material=Y simultaneously
- [ ] Results correctly filtered by all three criteria
- [ ] Removing one filter keeps others applied
- [ ] Clearing all filters shows full list

---

### Section 3: Job List Display

**Test 3.1: Job Information Display**
- [ ] For each job, displays:
  - [ ] Job ID or identifier
  - [ ] Gcode filename
  - [ ] Target printer model
  - [ ] Current status (with color coding)
  - [ ] Priority (if applicable)
  - [ ] Estimated print time (if available)
  - [ ] Material type (if specified)

**Test 3.2: Visual Styling**
- [ ] Job rows have alternating colors (for readability)
- [ ] Status badges use correct color scheme:
  - [ ] Queued = Blue
  - [ ] Printing = Green
  - [ ] Paused = Yellow
  - [ ] Completed = Checkmark
  - [ ] Failed = Red
  - [ ] Cancelled = Gray
- [ ] Row hover state is visible (slight background change)
- [ ] Text is readable (good contrast)

**Test 3.3: Pagination**
- [ ] If 100+ jobs exist, pagination appears
- [ ] "Previous/Next" buttons work correctly
- [ ] Current page indicator shows (e.g., "Page 1 of 5")
- [ ] Changing pages shows different job sets
- [ ] Can jump to specific page

**Test 3.4: Sorting** (if implemented)
- [ ] Clicking column headers sorts by that field
- [ ] Ascending/descending toggle works
- [ ] Sort direction indicator is clear
- [ ] Sorting works with filters applied

---

### Section 4: Job Actions

**Test 4.1: Single Job Actions**
- [ ] Each job row has action buttons (3-dot menu or inline buttons)
- [ ] **Pause** button (for printing jobs):
  - [ ] Button is visible and enabled for printing jobs
  - [ ] Clicking pause shows confirmation dialog
  - [ ] Confirming pauses the job
  - [ ] Status changes to "Paused"

- [ ] **Resume** button (for paused jobs):
  - [ ] Button is visible for paused jobs
  - [ ] Clicking resumes printing
  - [ ] Status returns to "Printing"

- [ ] **Cancel** button (for queued/paused jobs):
  - [ ] Button is visible for cancellable jobs
  - [ ] Clicking shows confirmation: "Are you sure?"
  - [ ] Confirming removes job from queue
  - [ ] Status changes to "Cancelled"

- [ ] **View Details** button:
  - [ ] Shows detailed information about the job
  - [ ] Dialog/page displays: gcode details, materials, estimates, timestamps
  - [ ] Can close details view without side effects

**Test 4.2: Bulk Actions**
- [ ] Checkbox appears on each job row
- [ ] **Select All** checkbox selects all visible jobs
- [ ] Individual checkboxes work independently
- [ ] "Selected: N items" counter displays
- [ ] **Bulk Cancel** button appears when jobs selected
- [ ] Bulk cancel shows: "Cancel X jobs?"
- [ ] Confirming cancels all selected jobs
- [ ] Selection clears after bulk action

**Test 4.3: Priority Adjustment**
- [ ] Each job has priority selector (High/Normal/Low or numeric)
- [ ] Changing priority updates immediately
- [ ] Jobs reorder by priority in list
- [ ] Priority persists after refresh

---

### Section 5: Error Handling & Edge Cases

**Test 5.1: API Errors**
- [ ] If API is down, display friendly error message
- [ ] Error message shows: "Failed to load jobs. Retrying..." or similar
- [ ] Retry button appears for failed loads
- [ ] Clicking retry re-fetches data

**Test 5.2: Authorization**
- [ ] If user is not authenticated, redirected to login
- [ ] After login, print queue loads correctly
- [ ] Session timeout shows appropriate message

**Test 5.3: Empty States**
- [ ] When no jobs exist: "No jobs in queue" displays
- [ ] When all jobs filtered away: "No jobs match filter" displays
- [ ] Empty state styled with PrintFarmer design

**Test 5.4: Data Validation**
- [ ] Invalid job data doesn't crash UI
- [ ] Missing fields handled gracefully (show "N/A")
- [ ] Malformed responses show error without crashing

**Test 5.5: Network Issues**
- [ ] Slow network: Loading spinner appears
- [ ] Connection timeout: Friendly error message
- [ ] Partial data load: Shows what loaded, indicates incomplete

---

### Section 6: Real-Time Updates

**Test 6.1: SignalR Connection**
- [ ] WebSocket connection established (F12 → Network → WS tab shows /hubs/printers)
- [ ] Connection shows "Connected" status
- [ ] Remains connected while viewing page

**Test 6.2: Live Job Updates**
- [ ] When job status changes on server, updates in real-time without refresh
- [ ] Job completion/failure shows immediately
- [ ] New jobs in queue appear in list automatically
- [ ] Cancelled jobs disappear from queue

**Test 6.3: Multi-Tab Sync**
- [ ] Open dashboard in 2 browser tabs
- [ ] Change status in tab 1
- [ ] Tab 2 updates automatically
- [ ] No manual refresh needed

---

### Section 7: Performance & Load Testing

**Test 7.1: Normal Load (10-50 jobs)**
- [ ] Dashboard loads in < 2 seconds
- [ ] Filters respond instantly
- [ ] Scrolling is smooth (60 FPS)
- [ ] No memory leaks (F12 → Memory tab)

**Test 7.2: Heavy Load (100+ jobs)**
- [ ] Dashboard still loads (< 5 seconds)
- [ ] Pagination makes list manageable
- [ ] Filtering still responsive
- [ ] No lag when scrolling

**Test 7.3: Resource Usage**
- [ ] CPU usage stays < 50% during normal operation
- [ ] Memory usage doesn't grow unbounded
- [ ] Network requests are reasonable (gzip compression visible)

---

### Section 8: Accessibility & Responsive Design

**Test 8.1: Keyboard Navigation**
- [ ] Tab key navigates through all interactive elements
- [ ] Tab order is logical (left-to-right, top-to-bottom)
- [ ] Focus is clearly visible on all elements
- [ ] Enter/Space activates buttons correctly
- [ ] Escape key closes dialogs/modals

**Test 8.2: Screen Reader** (if available)
- [ ] Page title is announced
- [ ] Form labels are associated with inputs
- [ ] Button purposes are clear
- [ ] Table headers announce row/column
- [ ] Status changes are announced

**Test 8.3: Responsive Design**
- **Desktop (1920x1080)**:
  - [ ] Layout is full width
  - [ ] All elements visible without scrolling
  - [ ] Filters arranged horizontally

- **Tablet (768x1024)**:
  - [ ] Layout adapts to tablet width
  - [ ] Filters may stack vertically
  - [ ] Table columns adjust or scroll horizontally
  - [ ] Touch targets are adequate (>44px)

- **Mobile (375x667)**:
  - [ ] Layout is single column
  - [ ] Hamburger menu for navigation
  - [ ] Filters in collapsible section
  - [ ] Table simplified (some columns hidden)
  - [ ] Touch buttons are easy to tap

**Test 8.4: Color Contrast**
- [ ] All text passes WCAG AA contrast ratio (4.5:1)
- [ ] Status badges are distinguishable
- [ ] Use browser tool: Accessibility Insights or Wave

**Test 8.5: Motion & Animation**
- [ ] Any animations don't cause motion sickness
- [ ] Animations respect `prefers-reduced-motion` (if supported)
- [ ] No flashing (> 3 times per second)

---

### Section 9: Data Persistence & Consistency

**Test 9.1: State Persistence**
- [ ] Filters remain applied when navigating away and back
- [ ] Scroll position preserved (if applicable)
- [ ] Sort order persists during session

**Test 9.2: Data Consistency**
- [ ] Changes in one interface (e.g., UI) reflect in API
- [ ] API changes show in UI without stale data
- [ ] No duplicate entries in list
- [ ] Job IDs are unique

**Test 9.3: Concurrent Operations**
- [ ] Multiple users can view queue simultaneously
- [ ] Changes by one user visible to others (via SignalR)
- [ ] No race conditions with simultaneous actions
- [ ] Last-write-wins conflict resolution

---

### Section 10: Browser Compatibility

**Test 10.1: Modern Browsers**
- [ ] Chrome 120+: ✅ / ❌ / N/A
- [ ] Firefox 121+: ✅ / ❌ / N/A
- [ ] Safari 17+: ✅ / ❌ / N/A
- [ ] Edge 120+: ✅ / ❌ / N/A

**Test 10.2: Older Browsers** (if supported)
- [ ] IE 11: ✅ / ❌ / N/A
- [ ] Mobile Safari (iOS 15): ✅ / ❌ / N/A

---

## 📊 Test Results Summary

### Execution Date: _______________

### Overall Status: _______________

| Category | Tests | Pass | Fail | Notes |
|----------|-------|------|------|-------|
| Dashboard Loading | 3 | ___ | ___ | |
| Filters | 6 | ___ | ___ | |
| Job Display | 4 | ___ | ___ | |
| Job Actions | 3 | ___ | ___ | |
| Errors | 5 | ___ | ___ | |
| Real-Time | 3 | ___ | ___ | |
| Performance | 3 | ___ | ___ | |
| Accessibility | 5 | ___ | ___ | |
| Persistence | 3 | ___ | ___ | |
| Compatibility | 2 | ___ | ___ | |

### Failed Tests (if any)
```
1. 
2. 
3. 
```

### Notes
```
```

### Blockers for Production
```
```

### Recommendations
```
```

---

## 🚀 Sign-Off

**Tester Name**: _______________  
**Date Completed**: _______________  
**Status**: ☐ Ready for Production | ☐ Needs Fixes | ☐ Not Tested

**Signature**: _______________

---

## Next Steps

After Phase 1b validation:
1. **Phase 2**: Model-based filtering and history integration
2. **Phase 3**: Advanced job management (drag-to-reorder, notes, tagging)
3. **Phase 4**: Automation and intelligence (auto-queueing, scheduling)
