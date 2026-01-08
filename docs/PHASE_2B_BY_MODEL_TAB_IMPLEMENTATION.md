# Phase 2B: "By Model" Tab Implementation Guide

**Start Date**: January 9, 2026  
**Estimated Duration**: 12 hours (2-3 days)  
**Status**: 🔄 IN PROGRESS

---

## Overview

The "By Model" tab groups all queued and printing jobs by printer model, displaying statistics and actionable insights for each model. This provides a quick view of which printers have the most work and helps identify bottlenecks.

### Key User Stories
1. As a farm manager, I want to see jobs organized by printer model so I can quickly identify which printers have the most queue
2. As a farm manager, I want statistics for each model (queued count, printing count, average wait time)
3. As a farm manager, I want to expand a model card to see the first few jobs and take actions on them
4. As a farm manager, I want to filter by model or status within the "By Model" view

---

## Component Architecture

```
PrintQueueDashboardPage
└── Tabs
    └── Tabs.Panel (id="by-model")
        └── ModelFilteredJobsTab (Container Component)
            ├── ModelFiltersBar (Filter controls)
            │   ├── ModelSelect
            │   ├── StatusSelect
            │   └── RefreshButton
            ├── ModelStatisticsPanel (Summary stats)
            │   ├── Total Jobs Badge
            │   ├── Total Models Badge
            │   └── Average Wait Time
            └── ModelJobsGrid (Responsive grid layout)
                └── ModelJobsCard (for each model)
                    ├── ModelHeader (name, expand button)
                    ├── ModelStats (queued, printing counts)
                    ├── ModelJobsList (first 3 jobs)
                    ├── ModelActions (View All, Filter)
                    └── JobStatusIndicators
```

---

## Implementation Tasks

### Task 2B.1: Create ModelFilteredJobsTab Component

**File**: `src/features/queue/components/ModelFilteredJobsTab.tsx`

**Responsibilities**:
- Fetch jobs from API
- Group jobs by model
- Calculate statistics per model
- Handle loading/error states
- Manage filter state (model, status)
- Pass data to child components

**Key Functions**:
```typescript
interface ModelStats {
  name: string;
  queuedCount: number;
  printingCount: number;
  totalCount: number;
  averageWaitTimeMinutes: number;
  jobs: QueueJob[];
}

function groupJobsByModel(jobs: QueueJob[]): ModelStats[]
function filterByModel(stats: ModelStats[], modelName: string): ModelStats[]
function filterByStatus(stats: ModelStats[], status: JobStatus[]): ModelStats[]
```

**Tests to Write**:
- Grouping jobs by model correctly
- Filtering by model name
- Filtering by status
- Calculating statistics accurately
- Handling empty data
- Error state rendering

**Estimated Time**: 2 hours

---

### Task 2B.2: Create ModelJobsCard Component

**File**: `src/features/queue/components/ModelJobsCard.tsx`

**Structure**:
```typescript
interface ModelJobsCardProps {
  model: ModelStats;
  isExpanded: boolean;
  onToggleExpand: () => void;
  onJobAction: (jobId: string, action: 'pause' | 'resume' | 'cancel' | 'priority') => void;
}
```

**Features**:
- Display model name as title
- Show stat badges: `Queued: 5`, `Printing: 2`
- Mini job list (show first 3 jobs)
- Collapsible behavior (click to expand/collapse)
- Inline job actions (pause, resume, cancel)
- "View All Jobs" button to filter jobs by model in "All Jobs" tab
- Average wait time indicator
- Model color indicator (optional badge)

**Layout** (Tailwind):
```
┌─────────────────────────────────────┐
│ Prusa CORE One [Queued: 5] [Print:2]│ (Header, clickable)
├─────────────────────────────────────┤
│ Avg Wait: 12 minutes                │
│ ─────────────────────────────────── │
│ Job 1: model.stl (00:15)  [⏸][⊗]    │
│ Job 2: part.stl (01:30)   [⏸][⊗]    │
│ Job 3: base.stl (02:45)   [⏸][⊗]    │
│                                     │
│ [View All Jobs for This Model]      │
└─────────────────────────────────────┘
```

**Styling Requirements**:
- Card layout with PrintFarmer tokens (`bg-pf-bg-1`, `border-pf-border`)
- Header clickable (cursor pointer, hover effect)
- Status badges use color tokens (`text-pf-success`, `text-pf-warning`)
- Responsive: Full width on mobile, grid on desktop

**Tests to Write**:
- Card renders model name and stats
- Click expands/collapses jobs list
- Job actions trigger callbacks
- "View All" button present
- Responsive layout on mobile

**Estimated Time**: 3 hours

---

### Task 2B.3: Create ModelStatisticsPanel Component

**File**: `src/features/queue/components/ModelStatisticsPanel.tsx`

**Displays**:
- Total jobs across all models (badge)
- Total models being used (badge)
- Average wait time overall (with trend indicator)
- Highest queue model (with count)
- Busiest model by printing jobs

**Layout** (Tailwind Grid):
```
┌──────────────────────────────────────────────────────┐
│ 📊 Queue Overview                                    │
├──────────────────────────────────────────────────────┤
│ [Total Jobs: 23] [Models: 5] [Avg Wait: 12m] [📈3min]│
│ 🔴 Busiest Queue: Prusa CORE One (8 jobs)            │
│ 🟢 Most Printing: Bambu P1S (3 printers)             │
└──────────────────────────────────────────────────────┘
```

**Styling**:
- Light background with PrintFarmer tokens
- Badge style for stats (rounded, colored)
- Icons for visual indicators
- Responsive to screen size

**Tests to Write**:
- Statistics calculated correctly
- Trend arrows display (up/down/stable)
- Correct model highlighted
- Responsive layout

**Estimated Time**: 2 hours

---

### Task 2B.4: Create ModelFiltersBar Component

**File**: `src/features/queue/components/ModelFiltersBar.tsx`

**Controls**:
1. **Model Select Dropdown**
   - Options: "All Models", then list of all unique models
   - Default: "All Models"
   - Updates parent filter state on change

2. **Status Filter Multi-Select or Toggle Buttons**
   - Options: "Queued", "Printing", "Paused", "All"
   - Default: "All"
   - Can select multiple

3. **Sort Dropdown**
   - Options: 
     - By Model Name (A→Z)
     - By Queue Size (Largest First)
     - By Avg Wait Time (Longest First)
     - By Currently Printing (Most First)
   - Default: By Model Name

4. **Refresh Button**
   - Triggers reload of jobs
   - Shows loading state

**Layout**:
```
[Model: All ▼] [Status: All ▼] [Sort: Name ▼] [🔄 Refresh]
```

**Tests to Write**:
- Filter changes trigger callbacks
- Dropdown options correct
- Refresh button triggers action
- Disabled state when loading

**Estimated Time**: 2 hours

---

### Task 2B.5: Create Unit Tests

**Test Files**:

1. **ModelFilteredJobsTab.test.tsx** (30+ tests)
   - Component rendering
   - Data fetching and grouping
   - Filtering logic
   - Error handling
   - Loading states

2. **ModelJobsCard.test.tsx** (20+ tests)
   - Card rendering
   - Expand/collapse functionality
   - Job action callbacks
   - Responsive behavior

3. **ModelStatisticsPanel.test.tsx** (15+ tests)
   - Statistics calculation
   - Correct model highlighted
   - Responsive layout

4. **ModelFiltersBar.test.tsx** (12+ tests)
   - Filter changes
   - Dropdown interactions
   - Button clicks

**Target**: 80%+ code coverage

**Estimated Time**: 3 hours

---

## API Integration

### Endpoint: Get Queue Jobs (Existing)
```
GET /api/printQueue?limit=100&offset=0
```

**Response Used**:
```typescript
interface QueueJob {
  id: string;
  name: string;
  printerModel: string;  // Key field for grouping
  material: string;
  estimatedTime: number;
  progress: number;
  status: 'queued' | 'printing' | 'paused' | 'failed';
  createdAt: string;
  startedAt?: string;
}
```

**No new API endpoints needed** - will use existing queue API with client-side grouping

---

## Data Flow

```
PrintQueueDashboardPage
  ↓
ModelFilteredJobsTab (fetches jobs)
  ├─ Groups by model
  ├─ Applies filters
  ├─ Calculates stats
  ├─ Passes to ModelFiltersBar (filters)
  ├─ Passes to ModelStatisticsPanel (stats)
  └─ Passes to ModelJobsGrid
      └─ ModelJobsCard (per model)
          ├─ Shows model stats
          ├─ Shows job list
          └─ Job actions
```

---

## Styling Guide

### PrintFarmer Design Tokens (Required)
- **Backgrounds**: `bg-pf-bg-0`, `bg-pf-bg-1`
- **Borders**: `border-pf-border`
- **Text Primary**: `text-pf-text-primary`
- **Text Secondary**: `text-pf-text-secondary`
- **Status Colors**:
  - Success: `text-pf-success` (green)
  - Warning: `text-pf-warning` (yellow)
  - Info: `text-pf-info` (blue)
  - Error: `text-pf-error` (red)

### Spacing & Layout
- Use Tailwind grid for responsive layouts
- Mobile first approach: full width on mobile, grid columns on desktop
- Standard padding: `p-4` for cards, `gap-4` for grids
- Border radius: `rounded-lg` for cards

### Card Design
```html
<div class="bg-pf-bg-1 border border-pf-border rounded-lg p-4">
  <!-- Card content -->
</div>
```

---

## Accessibility Requirements

- [ ] All interactive elements keyboard accessible
- [ ] Proper ARIA labels on buttons and badges
- [ ] Color not sole indicator (use text/icons)
- [ ] Focus indicators visible
- [ ] Tab order logical
- [ ] Form controls have labels
- [ ] Error messages associated with controls

---

## Performance Considerations

1. **Grouping Algorithm**: O(n) single pass to group jobs by model
2. **Memoization**: Use `useMemo` for grouping results
3. **Lazy Loading**: Consider virtualization if 50+ cards
4. **Sorting**: Client-side sorting (small dataset)
5. **Filtering**: Instant with useMemo
6. **Rendering**: React.memo for individual cards if many

---

## Testing Strategy

### Unit Tests (using Vitest + React Testing Library)
- Component rendering and interactions
- Data grouping and filtering logic
- State management
- Callback functions
- Error handling

### Manual Testing
- Filter functionality works correctly
- Expand/collapse cards smoothly
- Job actions trigger correctly
- Responsive on mobile/tablet/desktop
- PrintFarmer styling consistent

### Accessibility Testing
- Tab through all controls
- Screen reader announces counts and statuses
- All colors have text labels
- Focus indicators visible

---

## Definition of Done

- [ ] All components created and integrated
- [ ] 80%+ test coverage
- [ ] 0 TypeScript errors
- [ ] 0 ESLint errors
- [ ] 100% PrintFarmer styling
- [ ] WCAG 2.2 Level AA compliance
- [ ] Responsive design verified (mobile/tablet/desktop)
- [ ] All manual tests passed
- [ ] Code reviewed
- [ ] Git commits made with detailed messages
- [ ] Documentation updated

---

## Rollout Plan

1. **Implement Phase 2B.1** (2 hours) - Container component
2. **Implement Phase 2B.2** (3 hours) - Cards and layout
3. **Implement Phase 2B.3** (2 hours) - Statistics panel
4. **Implement Phase 2B.4** (2 hours) - Filters
5. **Create Tests** (3 hours) - Full test coverage
6. **Integration Testing** (1 hour) - Verify all together
7. **Accessibility Review** (1 hour) - WCAG compliance
8. **Polish & Optimization** (1 hour) - Performance, styling

**Total Estimated Time**: 12-15 hours

---

## Notes

- Reuse existing components where possible (buttons, selects from PrintFarmer)
- Follow patterns established in Phase 1 and Phase 2A
- Keep components focused and testable
- Document complex logic with comments
- Maintain 100% test coverage for new components
