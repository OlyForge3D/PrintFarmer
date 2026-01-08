# Phase 2C: "History" Tab Implementation Guide

**Start Date**: January 8, 2026  
**Estimated Duration**: 15 hours (2-3 days)  
**Status**: 🔄 IN PROGRESS

---

## Overview

The "History" tab displays completed, failed, and cancelled print jobs with comprehensive filtering, analytics, and the ability to rerun completed jobs. This helps users track job outcomes, identify failure patterns, and quickly rerun successful prints.

### Key User Stories
1. As a farm manager, I want to see all completed and failed jobs so I can track what's been done
2. As a farm manager, I want to filter history by date, status, model, and material to find specific jobs
3. As a farm manager, I want to see success rates and failure reasons to identify issues
4. As a farm manager, I want to rerun a completed job quickly
5. As a farm manager, I want to see material usage statistics

---

## Component Architecture

```
PrintQueueDashboardPage
└── Tabs
    └── Tabs.Panel (id="history")
        └── QueueHistoryTab (Container Component)
            ├── HistoryFiltersBar (Filter controls)
            │   ├── DateRangePicker
            │   ├── StatusSelect (Completed, Failed, Cancelled, All)
            │   ├── ModelSelect
            │   ├── MaterialSelect
            │   └── SortSelect
            ├── HistoryStatisticsPanel (Summary stats)
            │   ├── Total Completed Badge
            │   ├── Success Rate %
            │   ├── Average Duration
            │   ├── Failure Breakdown
            │   └── Material Usage Top 5
            └── HistoryJobsGrid (Paginated grid layout)
                └── HistoryJobCard (for each job)
                    ├── JobHeader (name, printer, date)
                    ├── JobStats (duration, completion %)
                    ├── StatusIndicator (color-coded)
                    ├── FailureReason (if failed)
                    ├── MaterialInfo
                    └── HistoryJobActions (Rerun, Details)
```

---

## Implementation Tasks

### Task 2C.1: Create QueueHistoryTab Component

**File**: `src/features/queue/components/QueueHistoryTab.tsx`

**Responsibilities**:
- Fetch completed/failed jobs from API
- Parse and format dates
- Handle pagination
- Calculate statistics (success rate, avg duration, failure reasons)
- Manage filter state (date range, status, model, material)
- Pass data to child components

**Key Functions**:
```typescript
interface HistoryJob {
  id: string;
  name: string;
  printerModel: string;
  material: string;
  status: 'completed' | 'failed' | 'cancelled';
  duration: number; // seconds
  completionPercentage: number;
  startedAt: string;
  completedAt: string;
  failureReason?: string;
}

interface HistoryStats {
  totalCompleted: number;
  totalFailed: number;
  totalCancelled: number;
  successRate: number; // percentage
  averageDurationMinutes: number;
  failureReasons: { [key: string]: number };
  topMaterials: { material: string; count: number }[];
}

function getHistoryStats(jobs: HistoryJob[]): HistoryStats
function filterByDateRange(jobs: HistoryJob[], start: Date, end: Date): HistoryJob[]
function filterByStatus(jobs: HistoryJob[], statuses: string[]): HistoryJob[]
```

**Tests to Write**:
- Fetching and parsing history data
- Date range filtering
- Status filtering
- Statistics calculation
- Pagination handling
- Error states
- Empty data

**Estimated Time**: 2 hours

---

### Task 2C.2: Create HistoryJobCard Component

**File**: `src/features/queue/components/HistoryJobCard.tsx`

**Structure**:
```typescript
interface HistoryJobCardProps {
  job: HistoryJob;
  onRerun: (jobId: string) => Promise<void>;
  onViewDetails?: (jobId: string) => void;
}
```

**Features**:
- Job name and printer model
- Completion date/time
- Job duration (HH:MM:SS format)
- Completion percentage (visual progress bar)
- Status badge (Completed: green, Failed: red, Cancelled: gray)
- Failure reason (if failed, e.g., "Thermal runaway", "Layer shift")
- Material type with color swatch
- "Rerun Job" button (for completed jobs only)
- "View Details" link

**Layout** (Tailwind):
```
┌─────────────────────────────────────────┐
│ model.stl - Prusa CORE One              │ (Completed ✓)
├─────────────────────────────────────────┤
│ Completed: Jan 8, 2026 at 14:32         │
│ Duration: 2:45:30                       │
│ Progress: ███████████████░░░ 95%        │
│ Material: PLA (Orange)                  │
│                                         │
│ [Rerun] [View Details]                  │
└─────────────────────────────────────────┘
```

**Styling Requirements**:
- Card layout with PrintFarmer tokens
- Status color-coded badge (success=green, error=red, cancelled=gray)
- Progress bar with gradient
- Material swatch color indicator
- Responsive: Full width on mobile, grid on desktop

**Tests to Write**:
- Card rendering with various statuses
- Progress bar display
- Rerun button callback
- Failure reason display
- Material display

**Estimated Time**: 3 hours

---

### Task 2C.3: Create HistoryFiltersBar Component

**File**: `src/features/queue/components/HistoryFiltersBar.tsx`

**Controls**:

1. **Date Range Picker**
   - Start date input
   - End date input
   - Quick buttons: "Last 7 days", "Last 30 days", "Last 90 days", "All time"
   - Default: Last 30 days

2. **Status Filter Multi-Select**
   - Options: "Completed", "Failed", "Cancelled", "All"
   - Default: "All"
   - Can select multiple

3. **Model Select Dropdown**
   - Options: "All Models", then list of models from history
   - Default: "All Models"

4. **Material Filter Multi-Select**
   - Options: "All Materials", then list of materials from history
   - Default: "All Materials"

5. **Sort Dropdown**
   - Options:
     - Newest First (default)
     - Oldest First
     - Longest Duration
     - Shortest Duration
     - By Printer Model

6. **Reset Filters Button**
   - Clears all filters to defaults
   - Shows "Reset Filters" text

**Layout**:
```
Date Range: [Start] [End] [Last 7d] [Last 30d] [Last 90d] [All]
Status: [Completed ✓] [Failed] [Cancelled]
Model: [All Models ▼]
Material: [All Materials ▼]
Sort: [Newest First ▼] [Reset Filters]
```

**Estimated Time**: 2 hours

---

### Task 2C.4: Create HistoryStatisticsPanel Component

**File**: `src/features/queue/components/HistoryStatisticsPanel.tsx`

**Displays**:
- Total completed jobs (badge)
- Total failed jobs (badge)
- Success rate percentage with trend
- Average job duration (with best/worst indicators)
- Failure reasons breakdown (top 3)
- Top 5 materials used (with usage count)

**Layout** (Tailwind Grid):
```
┌──────────────────────────────────────────────────┐
│ 📊 Job History Overview                          │
├──────────────────────────────────────────────────┤
│ [Completed: 156] [Failed: 12] [Cancelled: 3]     │
│ Success Rate: 92.9% (↑ 2.1% from last month)     │
│ Avg Duration: 1h 23m (Fastest: 12m, Longest: 6h)│
├──────────────────────────────────────────────────┤
│ Top Failure Reasons:                             │
│  • Thermal runaway: 5 (41.7%)                    │
│  • Layer shift: 4 (33.3%)                        │
│  • Other: 3 (25%)                                │
├──────────────────────────────────────────────────┤
│ Top Materials Used:                              │
│  • PLA (Orange): 47 prints                       │
│  • PETG (Blue): 32 prints                        │
│  • ABS (Black): 28 prints                        │
│  • TPU (Gray): 15 prints                         │
│  • Nylon (White): 8 prints                       │
└──────────────────────────────────────────────────┘
```

**Estimated Time**: 2 hours

---

### Task 2C.5: Implement "Rerun Job" Functionality

**File**: Modifications to QueueHistoryTab.tsx and confirmation modal

**Flow**:
1. User clicks "Rerun" button on completed job card
2. Confirmation modal appears: "Rerun this print job?"
3. User confirms
4. Job is added back to print queue
5. Toast notification: "Job added to queue"
6. Switch to "All Jobs" tab to show newly queued job

**Implementation**:
- Add `onRerun` callback to HistoryJobCard
- Create rerun handler in QueueHistoryTab
- Call `printQueueService.addJobToQueue()` or similar
- Handle success/error states
- Refresh history after rerun

**Estimated Time**: 1.5 hours

---

## API Integration

### Endpoint: Get Job History (Assumed)
```
GET /api/printQueue/history?status=completed&limit=50&offset=0&startDate=2024-01-01&endDate=2024-12-31
```

**Expected Response**:
```typescript
interface HistoryJobResponse {
  id: string;
  fileName: string;
  printerModel: string;
  material: string;
  status: 'Completed' | 'Failed' | 'Cancelled';
  totalTime: number; // seconds
  progress: number; // 0-100
  startedAt: string; // ISO date
  completedAt: string; // ISO date
  failureReason?: string;
}
```

**NOTE**: If this endpoint doesn't exist, we'll need to:
1. Check what history endpoints are available
2. Create a backend endpoint if needed
3. Or adapt to use existing endpoints

---

## Data Flow

```
QueueHistoryTab (fetches history)
  ├─ Filters by date/status/model/material
  ├─ Calculates statistics
  ├─ Passes to HistoryFiltersBar (filters)
  ├─ Passes to HistoryStatisticsPanel (stats)
  └─ Passes to HistoryJobsGrid
      └─ HistoryJobCard (per job)
          ├─ Shows job details
          ├─ Rerun callback
          └─ View details callback
```

---

## Styling Guide

### PrintFarmer Design Tokens (Required)
- **Backgrounds**: `bg-pf-bg-0`, `bg-pf-bg-1`
- **Borders**: `border-pf-border`
- **Text Primary**: `text-pf-text-primary`
- **Text Secondary**: `text-pf-text-secondary`
- **Status Colors**:
  - Completed: `text-pf-success` (green)
  - Failed: `text-pf-error` (red)
  - Cancelled: `text-pf-text-secondary` (gray)
- **Info/Highlights**: `text-pf-info` (blue)

### Card Design
- Standard padding and spacing matching Phase 2B
- Status badges with background: `bg-green-100 text-pf-success` etc.
- Progress bars with gradient: `bg-gradient-to-r from-pf-success to-pf-info`

---

## Accessibility Requirements

- [ ] All interactive elements keyboard accessible
- [ ] Proper ARIA labels on filters and buttons
- [ ] Date picker accessible
- [ ] Status badges have text labels (not just color)
- [ ] Focus indicators visible
- [ ] Tab order logical
- [ ] Error messages associated with controls

---

## Performance Considerations

1. **Pagination**: Load 50 items per page (lazy load on scroll or pagination buttons)
2. **Sorting**: Client-side initially, implement server-side if >1000 jobs
3. **Date Filtering**: Use efficient date comparison
4. **Statistics**: Memoize calculations if dataset is large
5. **Rendering**: React.memo for individual job cards if paginated

---

## Testing Strategy

### Unit Tests (Phase 2E)
- Component rendering
- Date filtering and range validation
- Status filtering
- Statistics calculation
- Pagination logic
- Rerun functionality

### Manual Testing
- Filter combinations work correctly
- Date range picker functional
- Rerun button adds job to queue
- Statistics display correctly
- Responsive on mobile/tablet/desktop
- Pagination works smoothly

---

## Definition of Done

- [ ] All components created and integrated
- [ ] Filtering works across all dimensions
- [ ] Statistics calculated and displayed correctly
- [ ] Rerun functionality working
- [ ] Pagination implemented
- [ ] 100% PrintFarmer styling
- [ ] WCAG 2.2 Level AA compliance
- [ ] Responsive design verified
- [ ] All 292 existing tests still passing
- [ ] No TypeScript/ESLint errors
- [ ] Code reviewed and committed

---

## Rollout Plan

1. **Implement Phase 2C.1** (2 hours) - Container component & API integration
2. **Implement Phase 2C.2** (3 hours) - Job cards and layout
3. **Implement Phase 2C.3** (2 hours) - Filter controls
4. **Implement Phase 2C.4** (2 hours) - Statistics panel
5. **Implement Phase 2C.5** (1.5 hours) - Rerun functionality
6. **Integration & Polish** (2 hours) - Combine, test, style
7. **Accessibility Review** (1 hour) - WCAG compliance
8. **Final Testing** (1.5 hours) - Edge cases, responsive

**Total Estimated Time**: 15 hours

---

## Notes

- Reuse existing UI components (Button, Select, Alert, Modal, etc.) from PrintFarmer
- Follow patterns from Phase 2A and 2B
- Keep components focused and testable
- Consider if history API endpoint exists - may need backend work
- Material color swatches could be simplified to text labels initially
