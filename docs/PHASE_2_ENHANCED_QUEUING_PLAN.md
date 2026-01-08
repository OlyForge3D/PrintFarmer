# Phase 2: Enhanced Queuing - Implementation Plan

**Start Date**: January 8, 2026  
**Estimated Duration**: 1-2 weeks  
**Status**: 🔄 IN PROGRESS

---

## Overview

Phase 2 transforms the Print Queue dashboard from a simple list view into a comprehensive job management system with multiple views, advanced filtering, and statistics.

### Current State (End of Phase 1b)
- ✅ Single "All Jobs" view with basic filtering
- ✅ Stats cards showing counts (Queued, Printing, Paused)
- ✅ Job list with actions (Pause, Resume, Cancel)
- ✅ Authentication and styling working
- ✅ 1926 tests passing

### Desired End State (Phase 2 Complete)
- ✅ Three tabs: "All Jobs", "By Model", "History"
- ✅ Group jobs by printer model with statistics
- ✅ View job history with completion rates
- ✅ Advanced filtering across all views
- ✅ Model-based analytics
- ✅ Responsive and accessible design
- ✅ 2000+ tests passing (maintaining coverage)

---

## Architecture

### Tab Structure

```
PrintQueueDashboardPage
├── Tabs Component
│   ├── Tab 1: "All Jobs"
│   │   ├── QueueFiltersBar (existing, enhanced)
│   │   ├── QueueJobsTable (existing)
│   │   └── Pagination controls
│   │
│   ├── Tab 2: "By Model" (NEW)
│   │   ├── ModelFilters
│   │   ├── ModelJobsGrid
│   │   │   ├── ModelJobsCard (per model)
│   │   │   │   ├── Model name + stats
│   │   │   │   ├── Queued count
│   │   │   │   ├── Currently printing
│   │   │   │   ├── Mini job list (collapsible)
│   │   │   │   └── View all button
│   │   │   └── (repeat per model)
│   │   └── ModelStatisticsPanel
│   │
│   └── Tab 3: "History" (NEW)
│       ├── HistoryFilters (date range, status)
│       ├── HistoryJobsGrid
│       │   ├── HistoryJobCard (per job)
│       │   │   ├── Job name + file
│       │   │   ├── Printer name
│       │   │   ├── Success/Failure status
│       │   │   ├── Duration
│       │   │   ├── Completion % or error
│       │   │   └── Rerun button
│       │   └── (repeat per job)
│       └── HistoryStatisticsPanel
```

---

## Component Breakdown

### 1. Enhanced PrintQueueDashboardPage

**Changes**:
- Add Tabs component (using PrintFarmer UI or custom)
- Tab 1: "All Jobs" (move existing content here)
- Tab 2: "By Model" (new tab content)
- Tab 3: "History" (new tab content)
- Update state to track active tab
- Add shared filter state across tabs (if applicable)

**File**: `src/Web/ReactApp/src/features/queue/pages/PrintQueueDashboardPage.tsx`

**Implementation Steps**:
1. Add Tabs UI component
2. Create three tab contents as separate components
3. Move existing filters/table into "All Jobs" tab
4. Add logic to load model and history data
5. Add state for active tab

---

### 2. ModelFilteredJobsTab (NEW)

**Purpose**: Show all jobs grouped by printer model with statistics

**Props**:
```typescript
interface ModelFilteredJobsTabProps {
  onJobAction?: (jobId: string, action: string) => void;
  loading?: boolean;
}
```

**Features**:
- Display all printer models
- For each model:
  - Model name
  - Queued count (badge)
  - Currently printing (badge)
  - Mini job list (max 3, with "View all" link)
  - Click to expand/collapse
- Overall statistics
- Filter by model (click model card)

**File**: `src/Web/ReactApp/src/features/queue/components/ModelFilteredJobsTab.tsx`

**Data Flow**:
```
ModelFilteredJobsTab
├── Load models from stats API: GET /api/printQueue/stats/models
├── For each model:
│   ├── Get jobs for that model: GET /api/printQueue?filterModel=modelName
│   └── Display ModelJobsCard
└── Display ModelStatisticsPanel
```

**Implementation Steps**:
1. Create component skeleton
2. Add API calls to load model data
3. Create ModelJobsCard subcomponent
4. Create ModelStatisticsPanel subcomponent
5. Add filtering and sorting logic
6. Add expand/collapse animation
7. Add tests (minimum 10 test cases)

---

### 3. ModelJobsCard (NEW)

**Purpose**: Display single printer model with its queued jobs

**Props**:
```typescript
interface ModelJobsCardProps {
  model: string;
  queuedCount: number;
  printingCount: number;
  jobs: QueuedPrintJobWithFileMetaDto[];
  isExpanded?: boolean;
  onToggleExpand?: (model: string) => void;
  onJobAction?: (jobId: string, action: string) => void;
}
```

**Display**:
- Model name as heading
- Stats row: "Queued: X | Printing: Y | Paused: Z"
- Mini job list (collapsed: shows first 3, expanded: shows all)
- "View all jobs for {model}" button
- Action buttons (if expanded)

**Styling**: PrintFarmer design tokens throughout

**File**: `src/Web/ReactApp/src/features/queue/components/ModelJobsCard.tsx`

**Implementation Steps**:
1. Create component skeleton
2. Add PrintFarmer styling
3. Add expand/collapse logic
4. Create mini job list display
5. Add action button handlers
6. Add tests (minimum 8 test cases)

---

### 4. QueueHistoryTab (NEW)

**Purpose**: Show completed, failed, and cancelled jobs with statistics

**Props**:
```typescript
interface QueueHistoryTabProps {
  loading?: boolean;
}
```

**Features**:
- Filter by status: All / Completed / Failed / Cancelled
- Date range filter (last 7 days, 30 days, custom)
- Sort by: Date (newest first), Duration, Success rate
- Display history jobs with:
  - Job name
  - File name
  - Printer name
  - Status with color
  - Duration
  - Success/failure reason
  - Completion date
  - Rerun button (for completed jobs)

**File**: `src/Web/ReactApp/src/features/queue/components/QueueHistoryTab.tsx`

**Data Flow**:
```
QueueHistoryTab
├── Load history: GET /api/printQueue/history?filterStatus=...&dateFrom=...&dateTo=...
├── For each history entry:
│   └── Display HistoryJobCard
└── Display HistoryStatisticsPanel
```

**Implementation Steps**:
1. Create component skeleton
2. Add API call to load history
3. Create HistoryJobCard subcomponent
4. Create HistoryStatisticsPanel subcomponent
5. Add filtering UI (status, date range)
6. Add sorting logic
7. Add tests (minimum 10 test cases)

---

### 5. HistoryJobCard (NEW)

**Purpose**: Display single completed/failed/cancelled job

**Props**:
```typescript
interface HistoryJobCardProps {
  entry: QueueHistoryEntryDto;
  onRerun?: (jobId: string) => void;
}
```

**Display**:
- Job name and file name
- Printer name
- Status badge (color-coded: green=completed, red=failed, gray=cancelled)
- Duration (hh:mm:ss)
- Completion percentage or error message
- Completed date (relative time: "3 days ago")
- Rerun button (only if status=completed)
- Click to expand for more details

**Styling**: PrintFarmer design tokens throughout

**File**: `src/Web/ReactApp/src/features/queue/components/HistoryJobCard.tsx`

**Implementation Steps**:
1. Create component skeleton
2. Add PrintFarmer styling
3. Add status color mapping
4. Add relative time formatting
5. Add expand/collapse for details
6. Add rerun handler
7. Add tests (minimum 8 test cases)

---

### 6. ModelStatisticsPanel (NEW)

**Purpose**: Show aggregated statistics by model

**Props**:
```typescript
interface ModelStatisticsPanelProps {
  models: Array<{
    modelName: string;
    totalQueued: number;
    currentlyPrinting: number;
    oldestQueuedAtUtc?: string;
    averageQueueWaitMinutes: number;
  }>;
}
```

**Display**:
- Grid of model cards showing:
  - Model name
  - Queued count
  - Printing count
  - Average wait time
  - Oldest job in queue (relative time)

**Styling**: PrintFarmer design tokens (bg-pf-bg-1, etc.)

**File**: `src/Web/ReactApp/src/features/queue/components/ModelStatisticsPanel.tsx`

**Implementation Steps**:
1. Create component skeleton
2. Add PrintFarmer styling
3. Add data aggregation logic
4. Create stat cards
5. Add relative time formatting
6. Add tests (minimum 5 test cases)

---

### 7. HistoryStatisticsPanel (NEW)

**Purpose**: Show aggregated history statistics

**Props**:
```typescript
interface HistoryStatisticsPanelProps {
  entries: QueueHistoryEntryDto[];
  dateRange?: {
    from: Date;
    to: Date;
  };
}
```

**Display**:
- Total jobs completed (in period)
- Success rate (%)
- Average print duration
- Total filament used
- Most used printer (by volume)
- Common failure reasons (if any)

**Styling**: PrintFarmer design tokens

**File**: `src/Web/ReactApp/src/features/queue/components/HistoryStatisticsPanel.tsx`

**Implementation Steps**:
1. Create component skeleton
2. Add statistics calculation functions
3. Add PrintFarmer styling
4. Create stat cards/display
5. Add data aggregation
6. Add tests (minimum 5 test cases)

---

## API Requirements

### Verify Existing Endpoints

The backend should already have these endpoints from Phase 1:

1. **GET /api/printQueue** ✅
   - Returns all current jobs
   - Filters: status, model, material
   - Pagination: limit, offset

2. **GET /api/printQueue/stats** ✅
   - Returns overall statistics
   - { totalQueued, totalPrinting, totalPaused, averageWaitTimeMinutes }

3. **GET /api/printQueue/stats/models** (verify)
   - Should return per-model statistics
   - Expected response:
     ```json
     {
       "byModel": {
         "Prusa CORE One": {
           "modelName": "Prusa CORE One",
           "totalQueued": 5,
           "currentlyPrinting": 2,
           "oldestQueuedAtUtc": "2026-01-08T10:30:00Z",
           "averageQueueWaitMinutes": 45
         }
       }
     }
     ```

4. **GET /api/printQueue/history** (verify or create)
   - Should return completed/failed/cancelled jobs
   - Filters: status, dateFrom, dateTo
   - Response: array of QueueHistoryEntryDto

### Interface Types (TypeScript)

Make sure these exist in `printQueueService.ts`:

```typescript
// Existing - verify
export interface QueueStatsDto {
  totalQueued: number;
  totalPrinting: number;
  totalPaused: number;
  averageWaitTimeMinutes: number;
  byModel: Record<string, QueuePrinterModelStatsDto>;
}

export interface QueuePrinterModelStatsDto {
  modelName: string;
  totalQueued: number;
  currentlyPrinting: number;
  oldestQueuedAtUtc?: string;
  averageQueueWaitMinutes: number;
}

export interface QueueHistoryPageDto {
  entries: QueueHistoryEntryDto[];
  totalCount: number;
  currentPage: number;
  pageSize: number;
}

export interface QueueHistoryEntryDto {
  id: string;
  jobName: string;
  printerName: string;
  status: string; // 'Completed', 'Failed', 'Cancelled'
  completionPercentage: number;
  startedAtUtc: string;
  completedAtUtc?: string;
  actualPrintTimeSeconds: number;
  failureReason?: string;
}
```

---

## Development Workflow

### Step 1: Setup & Planning (30 min)
- [ ] Review Phase 1b completion checklist
- [ ] Verify all API endpoints exist
- [ ] Create TypeScript types if missing
- [ ] Set up git branch: `feature/phase-2-enhanced-queuing`

### Step 2: Create Tab Infrastructure (1 hour)
- [ ] Update PrintQueueDashboardPage with Tabs
- [ ] Create Tab 1 container for "All Jobs"
- [ ] Create Tab 2 container for "By Model"
- [ ] Create Tab 3 container for "History"
- [ ] Test basic tab switching

### Step 3: Implement "By Model" Tab (3-4 hours)
- [ ] Create ModelFilteredJobsTab component
- [ ] Create ModelJobsCard component
- [ ] Create ModelStatisticsPanel component
- [ ] Add API calls and data loading
- [ ] Add filtering and expansion logic
- [ ] Style with PrintFarmer design tokens
- [ ] Write tests (10+ test cases)
- [ ] Verify all tests pass

### Step 4: Implement "History" Tab (3-4 hours)
- [ ] Create QueueHistoryTab component
- [ ] Create HistoryJobCard component
- [ ] Create HistoryStatisticsPanel component
- [ ] Add API calls and data loading
- [ ] Add filtering UI (status, date range)
- [ ] Add sorting logic
- [ ] Style with PrintFarmer design tokens
- [ ] Write tests (10+ test cases)
- [ ] Verify all tests pass

### Step 5: Integration & Polish (2-3 hours)
- [ ] Move existing "All Jobs" content into Tab 1
- [ ] Ensure filters work across all tabs (if needed)
- [ ] Verify responsive design (mobile, tablet, desktop)
- [ ] Verify accessibility (keyboard, screen reader)
- [ ] Run all tests: `npm run test:run`
- [ ] Run linting: `npm run lint`
- [ ] Performance check with browser DevTools

### Step 6: Testing & Validation (2-3 hours)
- [ ] Manual browser testing in all 3 tabs
- [ ] Test all filters and sorting
- [ ] Test responsive design on different devices
- [ ] Load test with 100+ jobs
- [ ] Test error scenarios
- [ ] Document results

### Step 7: Documentation & Commits (1 hour)
- [ ] Create Phase 2 completion summary
- [ ] Document new components and features
- [ ] Create commit messages for each major section
- [ ] Update PRINT_QUEUE_REDESIGN_PLAN.md
- [ ] Create PR/merge request if using Git flow

---

## Testing Strategy

### Unit Tests (Minimum Coverage)
- PrintQueueDashboardPage: 5 tests (tab switching)
- ModelFilteredJobsTab: 8 tests (filtering, sorting, expand/collapse)
- ModelJobsCard: 6 tests (rendering, actions, expansion)
- ModelStatisticsPanel: 4 tests (data aggregation)
- QueueHistoryTab: 10 tests (filtering, sorting, date range)
- HistoryJobCard: 6 tests (rendering, status colors, rerun)
- HistoryStatisticsPanel: 4 tests (statistics calculation)

**Total**: 43+ new tests

### Integration Tests
- Test filter persistence across tabs
- Test job action handling from multiple tabs
- Test pagination and large datasets
- Test API error handling
- Test authentication in tab switching

### Manual Testing
- Desktop browser (Chrome, Firefox, Safari)
- Tablet (responsive design)
- Mobile (responsive design)
- Different network speeds (DevTools throttling)
- Accessibility: keyboard navigation, screen reader

---

## Risk Mitigation

### Potential Issues & Solutions

| Risk | Mitigation |
|------|-----------|
| API endpoints not fully implemented | Verify endpoints early, create mocks if needed |
| Complex data aggregation | Build helper functions, test separately |
| Performance with 100+ jobs | Implement pagination, virtual scrolling if needed |
| Complex component interactions | Keep components focused, use simple props |
| Breaking changes to Phase 1 | Ensure "All Jobs" tab is exact replica of current view |
| Accessibility issues | Test with screen reader, keyboard nav from start |
| Unequal test coverage | Set minimum coverage targets upfront |

---

## Definition of Done

Phase 2 is complete when:
- ✅ All 3 tabs fully functional and tested
- ✅ 43+ new unit tests created and passing
- ✅ Total test count 2000+ (1926 from Phase 1b + new)
- ✅ 100% PrintFarmer design system compliance
- ✅ 0 build errors, 0 linting warnings
- ✅ Responsive on mobile, tablet, desktop
- ✅ Keyboard navigation working
- ✅ Manual testing completed and documented
- ✅ All commits made with clear messages
- ✅ Plan updated with completion status

---

## Rollback Plan

If Phase 2 encounters critical issues:
1. All changes exist on feature branch only
2. Can revert to `main` (Phase 1b) at any point
3. Keep Phase 1b fully functional and tested
4. No changes to working "All Jobs" view

---

## Success Metrics

After Phase 2 completion:
- ✅ Users can view jobs grouped by printer model
- ✅ Users can see job history with success/failure rates
- ✅ Users can filter jobs across all views
- ✅ Dashboard provides actionable statistics
- ✅ All features work on mobile and desktop
- ✅ Accessibility standards met (WCAG 2.2 AA)
- ✅ Performance remains fast (< 2s load time)
- ✅ 2000+ tests passing (100% pass rate)

---

**Next Review**: After Step 3 (By Model tab complete)  
**Target Completion**: January 22, 2026 (2 weeks from start)

---

*This plan is living documentation. Update as implementation progresses.*
