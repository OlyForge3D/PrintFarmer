# Phase 3C - Timeline & History Visualization
## Implementation Plan & Roadmap

**Start Date**: January 8, 2026  
**Estimated Duration**: 4 days (January 9-12, 2026)  
**Status**: 🔜 READY TO START

---

## Overview

Phase 3C enhances the print queue dashboard with timeline visualization, job state tracking, and duration analytics. Users will see:

- **Timing Tab**: Visual timeline of job state changes with timestamps
- **Job State History**: When jobs transitioned between states (Queued → Printing → Completed)
- **Duration Analytics**: Estimated vs actual duration comparison
- **Timeline Visualization**: Gantt-like chart showing job progression
- **Completion Predictions**: AI-powered duration estimates

---

## Phase Objectives

✅ **Primary Goal**: Provide deep visibility into job execution timeline

**Key Requirements**:
1. Display when jobs entered each state (timestamps)
2. Show estimated vs actual duration
3. Visual timeline of job progression
4. Compare multiple jobs' timelines
5. Predict future job completion times

---

## Components to Implement

### New Components (5 total)

1. **TimingTab.tsx** (Main container)
   - Tab component for Timing tab in PrintQueueDashboardPage
   - Filters: Date range, printer, status
   - Statistics cards: Total jobs, avg duration, accuracy
   - Timeline visualization area

2. **JobTimeline.tsx** (Visual timeline)
   - Gantt-style chart showing job progression
   - Horizontal bars for each job
   - Color-coded states (Queued, Printing, Paused, Completed)
   - Hover tooltips with details

3. **JobStateHistory.tsx** (State transition list)
   - Chronological list of state changes
   - Columns: Timestamp, From State, To State, Duration, Notes
   - Sortable and filterable
   - Relative time formatting ("2 hours ago")

4. **DurationComparison.tsx** (Est vs Actual)
   - Side-by-side bars: Estimated vs Actual
   - Variance percentage
   - Color coding (green=accurate, orange=variance, red=major difference)
   - Detailed breakdown by component

5. **CompletionPrediction.tsx** (Future estimate)
   - Predicted completion time for current jobs
   - Based on historical average duration
   - Confidence level (high/medium/low)
   - Breakdown by remaining jobs

### Updated Components (2 total)

1. **PrintQueueDashboardPage.tsx**
   - Add "Timing" tab to tab list
   - Pass filters to TimingTab

2. **Tabs.tsx** (if needed)
   - Ensure support for 4 tabs

---

## API Endpoints Needed

### New Endpoints (3 total)

1. **GET /api/printQueue/timeline**
   - Get timeline data for visualization
   - Query params: dateFrom, dateTo, printerId, status
   - Returns: Array of timeline events with timestamps
   - Response: `TimelineEventDto[]`

2. **GET /api/printQueue/jobs/{jobId}/state-history**
   - Get complete state history for single job
   - Returns: State transitions with timestamps
   - Response: `JobStateHistoryDto`

3. **GET /api/printQueue/duration-analytics**
   - Get duration comparison data
   - Query params: printerId, dateFrom, dateTo
   - Returns: Estimated vs actual duration stats
   - Response: `DurationAnalyticsDto`

### Existing Endpoints (enhanced)

- `GET /api/printQueue` - Already returns jobs
- `GET /api/printQueue/history` - Already returns history
- `GET /api/printQueue/stats` - Already returns stats

---

## Service Layer

### New Methods (3 total)

**PrintQueueService.cs**:

```csharp
/// <summary>
/// Get timeline events for visualization
/// </summary>
public async Task<IEnumerable<TimelineEventDto>> GetTimelineAsync(
    DateTime? dateFrom,
    DateTime? dateTo,
    string? printerId,
    string? filterStatus,
    CancellationToken cancellationToken)
{
    // Query PrintJobs with state transitions
    // Filter by date range, printer, status
    // Return events with timestamps
}

/// <summary>
/// Get state history for a specific job
/// </summary>
public async Task<JobStateHistoryDto> GetJobStateHistoryAsync(
    string jobId,
    CancellationToken cancellationToken)
{
    // Get all state changes for job
    // Include timestamps and durations
}

/// <summary>
/// Get duration analytics (est vs actual)
/// </summary>
public async Task<DurationAnalyticsDto> GetDurationAnalyticsAsync(
    string? printerId,
    DateTime? dateFrom,
    DateTime? dateTo,
    CancellationToken cancellationToken)
{
    // Compare estimated vs actual durations
    // Calculate variance and accuracy
}
```

---

## Data Models

### DTOs to Create

**TimelineEventDto**:
```typescript
{
  jobId: string;
  jobName: string;
  printer: string;
  state: JobState;
  enteredAt: Date;
  exitedAt?: Date;
  durationSeconds?: number;
  estimatedDurationSeconds?: number;
  variancePercent?: number;
}
```

**JobStateHistoryDto**:
```typescript
{
  jobId: string;
  jobName: string;
  states: StateTransitionDto[];
  totalDuration: number;
  estimatedDuration: number;
}

StateTransitionDto {
  fromState: JobState;
  toState: JobState;
  timestamp: Date;
  durationInState: number;
  notes?: string;
}
```

**DurationAnalyticsDto**:
```typescript
{
  totalJobs: number;
  averageEstimated: number;
  averageActual: number;
  accuracy: number; // 0-100%
  variance: number;
  byPrinter: {
    [printerId]: {
      estimatedAvg: number;
      actualAvg: number;
      accuracy: number;
    }
  }
}
```

---

## React Frontend Implementation

### TimingTab Component Structure

```typescript
function TimingTab() {
  // State
  const [dateFrom, setDateFrom] = useState(new Date(Date.now() - 7*24*60*60*1000));
  const [dateTo, setDateTo] = useState(new Date());
  const [printerId, setPrinterId] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  
  // Data fetching
  const { data: timelineEvents, loading: timelineLoading } = useApi(
    () => printQueueService.getTimelineAsync(dateFrom, dateTo, printerId, status),
    [dateFrom, dateTo, printerId, status]
  );
  
  const { data: analytics, loading: analyticsLoading } = useApi(
    () => printQueueService.getDurationAnalyticsAsync(printerId, dateFrom, dateTo),
    [dateFrom, dateTo, printerId]
  );

  return (
    <div>
      {/* Filters */}
      <TimingFiltersBar
        onDateFromChange={setDateFrom}
        onDateToChange={setDateTo}
        onPrinterChange={setPrinterId}
        onStatusChange={setStatus}
      />
      
      {/* Statistics */}
      <div className="grid grid-cols-4 gap-4">
        <StatCard label="Total Jobs" value={analytics?.totalJobs} />
        <StatCard label="Avg Estimated" value={formatDuration(analytics?.averageEstimated)} />
        <StatCard label="Avg Actual" value={formatDuration(analytics?.averageActual)} />
        <StatCard label="Accuracy" value={`${analytics?.accuracy.toFixed(1)}%`} />
      </div>
      
      {/* Timeline Visualization */}
      <JobTimeline events={timelineEvents} loading={timelineLoading} />
      
      {/* State History List */}
      <div className="mt-8">
        <h3 className="text-lg font-semibold mb-4">State Transitions</h3>
        <JobStateHistory events={timelineEvents} />
      </div>
      
      {/* Duration Comparison */}
      <div className="mt-8">
        <h3 className="text-lg font-semibold mb-4">Duration Analysis</h3>
        <DurationComparison analytics={analytics} />
      </div>
    </div>
  );
}
```

### JobTimeline Component (Gantt Chart)

```typescript
function JobTimeline({ events, loading }: Props) {
  if (loading) return <LoadingState />;
  
  return (
    <div className="overflow-x-auto">
      <div className="flex flex-col gap-2">
        {events.map(event => (
          <div key={event.jobId} className="flex items-center gap-4">
            {/* Job label */}
            <div className="w-32 truncate text-sm font-medium">
              {event.jobName}
            </div>
            
            {/* Timeline bar */}
            <div className="flex-1 h-8 bg-gray-200 rounded relative">
              {/* Color-coded segment for each state */}
              <div
                className={`h-full absolute rounded ${getStateColor(event.state)}`}
                style={{
                  left: `${getProgressPercent(event)}%`,
                  width: `${getDurationPercent(event)}%`
                }}
              >
                <div className="text-xs text-white flex items-center justify-center h-full">
                  {event.state}
                </div>
              </div>
            </div>
            
            {/* Duration info */}
            <div className="w-24 text-right text-sm">
              {formatDuration(event.durationSeconds)} / {formatDuration(event.estimatedDurationSeconds)}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
```

### DurationComparison Component

```typescript
function DurationComparison({ analytics }: Props) {
  return (
    <div className="grid grid-cols-2 gap-8">
      {/* Overall stats */}
      <div>
        <h4 className="font-semibold mb-4">Overall</h4>
        <div className="space-y-4">
          <DurationBar
            label="Estimated"
            value={analytics.averageEstimated}
            color="blue"
          />
          <DurationBar
            label="Actual"
            value={analytics.averageActual}
            color="green"
          />
          <div className="pt-4 border-t">
            <div className="text-sm">
              Variance: <span className={analytics.variance > 0 ? 'text-red-500' : 'text-green-500'}>
                {analytics.variance > 0 ? '+' : ''}{analytics.variance}%
              </span>
            </div>
            <div className="text-sm">
              Accuracy: <span className="text-blue-500">{analytics.accuracy.toFixed(1)}%</span>
            </div>
          </div>
        </div>
      </div>
      
      {/* By printer */}
      <div>
        <h4 className="font-semibold mb-4">By Printer</h4>
        <div className="space-y-4">
          {Object.entries(analytics.byPrinter).map(([printerId, stats]) => (
            <div key={printerId} className="border-l-4 border-blue-500 pl-3">
              <div className="text-sm font-medium">{printerId}</div>
              <div className="text-xs text-gray-600">
                Est: {formatDuration(stats.estimatedAvg)} | 
                Act: {formatDuration(stats.actualAvg)} | 
                Acc: {stats.accuracy.toFixed(1)}%
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
```

---

## Database Schema Changes

### New Table: JobStateHistory

```sql
CREATE TABLE JobStateHistory (
    Id NVARCHAR(36) PRIMARY KEY,
    
    JobId NVARCHAR(36) NOT NULL,
    FOREIGN KEY (JobId) REFERENCES PrintJobs(Id),
    
    FromState NVARCHAR(50),
    ToState NVARCHAR(50) NOT NULL,
    
    TransitionedAtUtc DATETIME2 NOT NULL,
    DurationInStateSeconds INT,
    
    Notes NVARCHAR(MAX),
    
    INDEX IX_JobId (JobId),
    INDEX IX_TransitionedAt (TransitionedAtUtc DESC)
);
```

### Updated Table: PrintJobs

```sql
ALTER TABLE PrintJobs ADD (
    ActualStartTimeUtc DATETIME2,
    ActualEndTimeUtc DATETIME2,
    ActualDurationSeconds INT,
    
    -- Computed columns
    EstimatedDurationSeconds AS DATEDIFF(SECOND, CreatedAtUtc, GETUTCDATE()),
    DurationVariancePercent AS CASE 
        WHEN EstimatedDurationSeconds > 0 
        THEN ((CAST(ActualDurationSeconds AS FLOAT) - EstimatedDurationSeconds) / EstimatedDurationSeconds) * 100
        ELSE NULL
    END,
    
    INDEX IX_ActualDates (ActualStartTimeUtc, ActualEndTimeUtc)
);
```

---

## Testing Strategy

### Unit Tests

**JobTimeline.test.tsx**:
- Render with empty data
- Render with multiple events
- State color coding
- Duration formatting
- Responsive layout

**DurationComparison.test.tsx**:
- Render statistics
- Show variance
- Display accuracy percentage
- Highlight deviations

**TimingTab.test.tsx**:
- Filter by date range
- Filter by printer
- Load data on mount
- Update on filter change

### Integration Tests

**Timeline API Integration**:
- GET /api/printQueue/timeline returns correct data
- Filters work (dateFrom, dateTo, printerId)
- State history populated correctly
- Duration calculations accurate

**Service Layer**:
- GetTimelineAsync returns sorted events
- GetJobStateHistoryAsync includes all transitions
- GetDurationAnalyticsAsync calculates variance correctly

### Manual Testing

**Timeline Visualization**:
- [ ] View jobs on timeline
- [ ] See state transitions with colors
- [ ] Hover for tooltips
- [ ] Responsive on mobile

**Duration Comparison**:
- [ ] Compare estimated vs actual
- [ ] See variance percentages
- [ ] View accuracy metrics
- [ ] Filter by printer

**State History**:
- [ ] View chronological state changes
- [ ] See timestamp for each transition
- [ ] View duration in each state
- [ ] Search/filter by state

---

## Implementation Phases

### Phase 3C.1: Data Models & API (Day 1)
- [ ] Create DTOs
- [ ] Implement service methods
- [ ] Create API endpoints
- [ ] Add database table/columns
- [ ] Unit test service layer

**Exit Criteria - Phase 3C.1**:

✅ **Backend Implementation Complete**:
  - [ ] All 5 DTOs created with correct property mapping
  - [ ] All 3 service methods implemented and functional
  - [ ] All 3 API endpoints exposed and documented
  - [ ] JobStateHistory entity created and configured in DbContext
  - [ ] All required indexes added to database schema

✅ **.NET Code Format**:
  - [ ] Run: `cd /home/pi/pfarm/src && dotnet format ./farm-web.sln`
  - [ ] All code formatted per team standards
  - [ ] Output: Code formatting applied successfully

✅ **.NET Build Validation**:
  - [ ] Run: `dotnet clean ./farm-web.sln`
  - [ ] Run: `dotnet build ./farm-web.sln -c Debug`
  - [ ] Expected: **0 Errors, 0 Warnings**
  - [ ] Fix any build errors immediately

✅ **.NET Test Validation**:
  - [ ] Run: `dotnet test ./farm-web.sln -c Release`
  - [ ] Expected: **All tests pass (100% pass rate)**
  - [ ] No regressions in existing tests
  - [ ] Fix any failing tests immediately

---

### Phase 3C.2: React Components (Day 2)
- [ ] Create TimingTab component
- [ ] Create JobTimeline component
- [ ] Create DurationComparison component
- [ ] Create JobStateHistory component
- [ ] Integrate into PrintQueueDashboardPage

**Exit Criteria - Phase 3C.2**:

✅ **React Implementation Complete**:
  - [ ] TimingTab component complete with date range filtering
  - [ ] JobTimeline component renders timeline events
  - [ ] DurationComparison component displays analytics
  - [ ] JobStateHistoryView component shows state transitions
  - [ ] CompletionPrediction component provides insights
  - [ ] All components integrated into PrintQueueDashboardPage
  - [ ] New "Timing & Analytics" tab accessible in dashboard

✅ **React Build & Type Checking**:
  - [ ] Run: `cd /home/pi/pfarm/src/Web/ReactApp && npm run build`
  - [ ] Expected: **0 TypeScript errors, build succeeds**
  - [ ] Bundle size acceptable
  - [ ] Build completes in reasonable time

✅ **React Linting**:
  - [ ] Run: `npm run lint`
  - [ ] Expected: **0 ESLint errors, 0 ESLint warnings**
  - [ ] Fix all accessibility and code style issues
  - [ ] Verify no critical violations

✅ **React Testing**:
  - [ ] Run: `npm run test:run`
  - [ ] Expected: **All tests pass (100% pass rate)**
  - [ ] No regressions in existing tests
  - [ ] Fix any failing tests immediately

---

### Phase 3C.3: Styling & Polish (Day 3)
- [ ] Apply PrintFarmer design tokens
- [ ] Responsive design
- [ ] Accessibility (ARIA labels, keyboard nav)
- [ ] Dark mode support
- [ ] Loading states

**Exit Criteria - Phase 3C.3**:

✅ **Styling & Design Complete**:
  - [ ] All PrintFarmer design tokens applied
  - [ ] Responsive design validated (320px, 768px, 1024px+)
  - [ ] Dark mode colors verified
  - [ ] Consistent with existing queue components
  - [ ] No hardcoded colors

✅ **Accessibility (WCAG 2.2 AA)**:
  - [ ] All interactive elements have ARIA labels
  - [ ] Keyboard navigation works (Tab, Enter, Escape)
  - [ ] Contrast ratios meet standards (4.5:1)
  - [ ] Form inputs properly labeled
  - [ ] Screen reader compatible

✅ **.NET Format & Build (Final Pass)**:
  - [ ] Run: `cd /home/pi/pfarm/src && dotnet format ./farm-web.sln`
  - [ ] Run: `dotnet clean ./farm-web.sln`
  - [ ] Run: `dotnet build ./farm-web.sln -c Debug`
  - [ ] Expected: **0 Errors, 0 Warnings**
  - [ ] Fix any warnings that appear

✅ **React Build & Lint (Final Pass)**:
  - [ ] Run: `cd /home/pi/pfarm/src/Web/ReactApp && npm run build`
  - [ ] Expected: **Build succeeds with 0 TypeScript errors**
  - [ ] Run: `npm run lint`
  - [ ] Expected: **0 ESLint errors, 0 ESLint warnings**

✅ **React Tests (Final Pass)**:
  - [ ] Run: `npm run test:run`
  - [ ] Expected: **100% test pass rate**

---

### Phase 3C.4: Testing & Validation (Day 4)
- [ ] Unit tests for all components
- [ ] Integration tests for API
- [ ] Manual testing checklist
- [ ] Performance optimization
- [ ] Documentation

**Exit Criteria - Phase 3C.4**:

✅ **Comprehensive Testing**:
  - [ ] Unit Tests: All new components have tests
    - [ ] TimingTab tests pass
    - [ ] JobTimeline tests pass
    - [ ] DurationComparison tests pass
    - [ ] JobStateHistoryView tests pass
    - [ ] CompletionPrediction tests pass
  - [ ] Integration Tests: API endpoints tested
    - [ ] Timeline endpoint returns correct data
    - [ ] State history endpoint works
    - [ ] Duration analytics endpoint works
  - [ ] All tests pass 100% (0 failures)
  - [ ] Test coverage >85% on new code

✅ **Performance Validation**:
  - [ ] Timeline rendering: <1s for 100+ events
  - [ ] Analytics calculation: <500ms
  - [ ] Page load: <2s total
  - [ ] No memory leaks detected
  - [ ] Bundle size acceptable

✅ **Manual Testing Complete**:
  - [ ] Load PrintQueueDashboardPage
  - [ ] Click "Timing & Analytics" tab
  - [ ] Timeline loads and displays correctly
  - [ ] Filter by date range works
  - [ ] Filter by printer works
  - [ ] Analytics update correctly
  - [ ] Tested on mobile, tablet, desktop
  - [ ] Keyboard navigation works
  - [ ] Screen reader compatible

✅ **.NET Final Validation**:
  - [ ] Run: `cd /home/pi/pfarm/src && dotnet format ./farm-web.sln`
  - [ ] Run: `dotnet clean ./farm-web.sln && dotnet build ./farm-web.sln -c Debug`
  - [ ] Expected: **0 Errors, 0 Warnings**
  - [ ] Run: `dotnet test ./farm-web.sln -c Release`
  - [ ] Expected: **All tests pass (100%)**

✅ **React Final Validation**:
  - [ ] Run: `cd /home/pi/pfarm/src/Web/ReactApp && npm run build`
  - [ ] Expected: **Build succeeds, 0 TypeScript errors**
  - [ ] Run: `npm run lint`
  - [ ] Expected: **0 ESLint errors, 0 warnings**
  - [ ] Run: `npm run test:run`
  - [ ] Expected: **100% test pass rate**

✅ **Phase 3C Complete**:
  - [ ] All exit criteria met
  - [ ] All components working
  - [ ] All tests passing
  - [ ] Build succeeds (both .NET and React)
  - [ ] Zero errors and warnings
  - [ ] Code quality verified
  - [ ] Documentation complete
  - [ ] Ready for deployment

---

## Success Criteria & Exit Gates

✅ **Phase Completion Definition**:

All 4 phases must achieve **100% exit criteria completion** before sign-off:

- ✅ Phase 3C.1: Backend complete with clean builds and passing tests
- ✅ Phase 3C.2: React complete with no TypeScript/ESLint errors
- ✅ Phase 3C.3: Polish complete with accessibility and responsiveness verified
- ✅ Phase 3C.4: Testing complete with 100% test pass rate

✅ **Code Quality Standards**:
- [ ] **Build Quality**
  - 0 .NET build errors (after `dotnet build`)
  - 0 .NET build warnings (after `dotnet build`)
  - 0 TypeScript compilation errors
  - 0 ESLint errors
  - 0 ESLint warnings
  
- [ ] **Test Quality**
  - 100% .NET test pass rate (all tests passing)
  - 100% React test pass rate (all tests passing)
  - >85% code coverage on new components
  - 0 test regressions
  
- [ ] **Code Standards**
  - Code formatted via `dotnet format`
  - TypeScript strict mode compliant
  - Accessibility WCAG 2.2 AA compliant
  - No console errors or warnings
  - No memory leaks detected

✅ **Deployment Readiness**:
- [ ] Production build succeeds (React)
- [ ] All dependencies updated and secure
- [ ] Database migrations safe and tested
- [ ] Documentation complete and accurate
- [ ] Team sign-off obtained

---

## Master Validation Checklist

Use this checklist at the end of each phase to verify completion:

**Phase 3C.1 - Exit Gate**:
```
dotnet format ./farm-web.sln
dotnet clean ./farm-web.sln && dotnet build ./farm-web.sln -c Debug
✓ Expected: 0 Errors, 0 Warnings

dotnet test ./farm-web.sln -c Release
✓ Expected: All tests pass (100%)
```

**Phase 3C.2 - Exit Gate**:
```
cd src/Web/ReactApp
npm run build
✓ Expected: 0 TypeScript errors

npm run lint
✓ Expected: 0 ESLint errors, 0 warnings

npm run test:run
✓ Expected: 100% pass rate
```

**Phase 3C.3 - Exit Gate**:
```
cd src
dotnet format ./farm-web.sln
dotnet clean ./farm-web.sln && dotnet build ./farm-web.sln -c Debug
✓ Expected: 0 Errors, 0 Warnings

cd Web/ReactApp
npm run build && npm run lint
✓ Expected: 0 TypeScript errors, 0 ESLint errors
```

**Phase 3C.4 - Exit Gate (Final)**:
```
# .NET validation
cd src
dotnet format ./farm-web.sln
dotnet clean ./farm-web.sln && dotnet build ./farm-web.sln -c Debug
✓ Expected: 0 Errors, 0 Warnings

dotnet test ./farm-web.sln -c Release
✓ Expected: 100% test pass rate

# React validation
cd Web/ReactApp
npm run build && npm run lint && npm run test:run
✓ Expected: All passing, 0 errors, 0 warnings

# Manual testing
- Verify Timing & Analytics tab loads
- Verify all components display correctly
- Verify responsive design works
- Verify accessibility with keyboard and screen reader
- Verify no console errors/warnings

# Final sign-off
✓ ALL CRITERIA MET - Ready for deployment
```

---

## Common Patterns & References

### Timeline Display Pattern

```typescript
// Group events by date
const eventsByDate = groupBy(events, e => formatDate(e.enteredAt));

// Render chronologically
Object.entries(eventsByDate).map(([date, dayEvents]) => (
  <section key={date}>
    <h3>{date}</h3>
    {dayEvents.map(event => (
      <TimelineEvent key={event.id} event={event} />
    ))}
  </section>
))
```

### Duration Formatting Pattern

```typescript
function formatDuration(seconds: number): string {
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}
```

### Color Coding Pattern

```typescript
function getStateColor(state: JobState): string {
  const colors: Record<JobState, string> = {
    'Queued': 'bg-blue-500',
    'Printing': 'bg-green-500',
    'Paused': 'bg-yellow-500',
    'Completed': 'bg-teal-500',
    'Failed': 'bg-red-500',
    'Cancelled': 'bg-gray-500'
  };
  return colors[state] || 'bg-gray-500';
}
```

---

## Known Limitations & Future Enhancements

**Current Scope**:
- ✅ Timeline visualization
- ✅ State history
- ✅ Duration comparison
- ✅ Basic analytics

**Not Included (Future Phases)**:
- [ ] Predictive completion times (ML)
- [ ] Custom date range picker UI
- [ ] Export timeline as image/PDF
- [ ] Calendar heatmap view
- [ ] Real-time timeline updates
- [ ] Comparison of multiple jobs side-by-side

---

## Next Phase (3D - Advanced Tag Management)

**Estimated Duration**: 5 days  
**Status**: Planned after Phase 3C completion

**Scope**:
- Full backend tag support
- Tag-based filtering
- Tag suggestions/autocomplete
- Tag analytics and usage

---

## Deployment Notes

### Build & Run

```bash
# Build
cd /home/pi/pfarm/src
dotnet build ./farm-web.sln -c Debug

# Test
dotnet test ./farm-web.sln -c Release
cd ../Web/ReactApp && npm run test:run

# Run Dev
# Terminal 1
cd /home/pi/pfarm/src
dotnet run --project ./api/Farm.Web.Api.csproj

# Terminal 2
cd /home/pi/pfarm/src/Web/ReactApp
npm run dev
```

### Docker Deployment

```bash
cd /home/pi/pfarm
./scripts/deploy-docker.sh --non-interactive --tear-down
```

---

## Files to Create/Modify

### New Files
- `src/Web/ReactApp/src/features/queue/components/TimingTab.tsx`
- `src/Web/ReactApp/src/features/queue/components/JobTimeline.tsx`
- `src/Web/ReactApp/src/features/queue/components/JobStateHistory.tsx`
- `src/Web/ReactApp/src/features/queue/components/DurationComparison.tsx`
- `src/Web/ReactApp/src/features/queue/components/CompletionPrediction.tsx`
- `src/api/Services/PrintQueue/TimelineService.cs` (if needed)
- `src/api/DTOs/TimelineDto.cs`

### Modified Files
- `src/Web/ReactApp/src/features/queue/pages/PrintQueueDashboardPage.tsx` (add Timing tab)
- `src/api/Controllers/PrintQueueController.cs` (add 3 endpoints)
- `src/api/Services/PrintQueue/PrintQueueService.cs` (add 3 methods)

---

## Sign-Off & Kickoff

**Phase 3C Status**: 🔜 READY TO START  
**Kickoff Date**: January 9, 2026  
**Estimated Completion**: January 12, 2026  

All planning complete. Ready to begin implementation.

---

*Phase 3C - Timeline & History Visualization*  
*Implementation Plan*  
*Status: Ready for Kickoff*  
*Date: January 8, 2026*
