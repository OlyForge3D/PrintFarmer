# Phase 3: Job Management - Detailed Plan

**Status**: ✅ COMPLETE - Phase 3D.5 Tag Analytics & Polish (Completed January 10, 2026)  
**Timeline**: Completed in 2 weeks (January 8-10, 2026)  
**Priority**: High - Core job manipulation features - ✅ DELIVERED  

---

## Overview

Phase 3 extends the print queue dashboard with advanced job manipulation capabilities. Users can now reorder jobs, add notes, tag jobs, pause/resume printing, and gain better visibility into job details and estimated print times.

**Phase 3D.5 (Tag Analytics & Polish)**: ✅ COMPLETE
- Tag analytics dashboard fully implemented
- GcodeFile tagging integrated with polymorphic design
- Comprehensive tagging documentation (TAGGING_SYSTEM.md)
- All 1672 .NET tests passing
- All 449 React tests passing
- Build: 0 errors, 0 warnings

This phase builds on Phase 2's foundation (tabs, filtering, history, rerun) by adding deeper job management features.

---

## Objectives & Success Criteria

### Primary Objectives

1. **Job Details & Editing**
   - View complete job information in modal/sidebar
   - Edit job properties before printing
   - Update notes, tags, and priority inline

2. **Job Reordering**
   - Drag-and-drop queue reordering (priority modification)
   - Keyboard accessibility for reordering (arrow keys)
   - Batch reorder with modal confirmation

3. **Job Control**
   - Pause printing job (suspend without losing progress)
   - Resume paused job
   - Status-aware action buttons

4. **Job Metadata**
   - Add custom notes to jobs
   - Tag jobs (project, material type, printer group)
   - Search/filter by tags

5. **Time Estimates**
   - Display estimated print time per job
   - Show calculated completion time
   - Queue position indicator

### Success Criteria (Definition of Done)

- ✅ Job details modal with all editable properties
- ✅ Drag-and-drop reordering with visual feedback
- ✅ Pause/resume working with status updates
- ✅ Notes and tags editable inline or in modal
- ✅ All 292+ existing tests still passing
- ✅ 30+ new tests for Phase 3 functionality
- ✅ Keyboard accessibility (ARIA labels, focus management)
- ✅ Mobile-responsive design
- ✅ Build succeeds with 0 errors (Backend + Frontend)
- ✅ Production-ready code with error handling

---

## Detailed Feature Breakdown

### Feature 3.1: Job Details Modal

**Purpose**: Display full job information and enable inline editing

**Backend Requirements**:
- New endpoint: `GET /api/printQueue/jobs/{jobId}` - Full job details with all fields
- Existing endpoints for updates:
  - `PUT /api/printQueue/jobs/{jobId}` - Update job properties
  - `PUT /api/printQueue/jobs/{jobId}/priority` - Update priority specifically

**New DTO** (if needed):
```csharp
public class JobDetailsDto
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Status { get; set; }
    public int Priority { get; set; }
    public int QueuePosition { get; set; }
    public string GcodeFileId { get; set; }
    public string FileName { get; set; }
    public string PrinterId { get; set; }
    public string PrinterName { get; set; }
    public string PrinterModel { get; set; }
    
    // Job metadata
    public string Notes { get; set; }
    public List<string> Tags { get; set; }
    public string MaterialType { get; set; }
    public double NozzleDiameter { get; set; }
    
    // Timing
    public int EstimatedPrintTimeSeconds { get; set; }
    public DateTime EstimatedCompletionTime { get; set; }
    public string EstimatedFilamentUsage { get; set; }
    
    // Status info
    public DateTime CreatedAt { get; set; }
    public DateTime QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
```

**Frontend Components**:

1. **JobDetailsModal.tsx** (350 lines)
   - Modal dialog with job information
   - Tabbed sections: Overview, Details, Timing, History
   - Edit buttons for each section
   - Save/Cancel actions with API integration
   - Loading and error states
   - Confirmation on unsaved changes

2. **JobDetailsSection.tsx** (180 lines)
   - Read-only display of job details
   - Edit mode toggle
   - Form fields for each property
   - Validation feedback

3. **JobNotesEditor.tsx** (150 lines)
   - Rich text editor for notes (or simple textarea)
   - Auto-save functionality (debounced)
   - Word count and character limit

4. **JobTagsEditor.tsx** (140 lines)
   - Tag input with autocomplete
   - Tag suggestions from existing tags
   - Tag color coding (by project/material)
   - Remove tag button

**API Integration**:
```typescript
// In printQueueService.ts
async getJobDetailsAsync(jobId: string): Promise<JobDetailsDto> { ... }
async updateJobDetailsAsync(jobId: string, updates: Partial<JobDetailsDto>): Promise<JobDetailsDto> { ... }
async updateJobNotesAsync(jobId: string, notes: string): Promise<void> { ... }
async updateJobTagsAsync(jobId: string, tags: string[]): Promise<void> { ... }
```

**Accessibility Requirements**:
- Modal focus management (trap focus inside modal, restore on close)
- Keyboard shortcut to close (Escape)
- ARIA labels for all form fields
- Semantic HTML (fieldset for grouped inputs)
- Tab navigation within modal

---

### Feature 3.2: Drag-and-Drop Reordering

**Purpose**: Allow users to reorder jobs in queue by dragging

**Backend Requirements**:
- Existing endpoint: `POST /api/printQueue/bulk/reorder` - Already supports reordering
- API takes array of job IDs in new order
- Recalculates queue positions atomically

**Frontend Components**:

1. **DraggableJobRow.tsx** (200 lines)
   - Wraps existing job row with drag capabilities
   - Drag handle icon (≡) on left side
   - Visual feedback during drag (opacity, shadow, highlight)
   - Drop zone indicators

2. **ReorderableQueueList.tsx** (280 lines)
   - Container using react-beautiful-dnd or similar
   - Manages drag state
   - Calls API on drop
   - Optimistic UI updates with rollback on error
   - Loading/disabled state during API call

3. **ReorderConfirmModal.tsx** (140 lines)
   - Shows reorder preview
   - Confirms before applying
   - Cancel button to abandon changes
   - Displays affected jobs and new positions

**Features**:
- Drag single job to new position
- Visual feedback during drag
- Auto-scroll when dragging near list edges
- Keyboard support (arrow keys to move, Space to confirm)
- Touch support for mobile (long-press to initiate)

**Accessibility**:
- ARIA-label for drag handle: "Drag handle for [Job Name]"
- aria-roledescription: "draggable"
- Screen reader announcement of new position after drop
- Keyboard-only drag capability (arrow keys)

**Libraries**:
- `react-beautiful-dnd` - Accessible drag-and-drop
- Alternative: `dnd-kit` if lighter weight preferred

---

### Feature 3.3: Pause/Resume Job

**Purpose**: Suspend printing without cancelling (preserve progress)

**Backend Requirements**:
- Existing endpoints:
  - `POST /api/printQueue/jobs/{jobId}/pause` - Already implemented
  - `POST /api/printQueue/jobs/{jobId}/resume` - Already implemented
- Update to track pause reason (optional notes)

**Frontend Components**:

1. **PauseResumeButton.tsx** (80 lines)
   - Conditional rendering based on job status
   - Shows "Pause", "Resume", or "Paused" depending on state
   - Click handler calls service
   - Loading state during API call
   - Error toast notification

2. **PauseJobModal.tsx** (120 lines)
   - Asks user for pause reason (optional)
   - Confirms pause action
   - Shows impact (job stays in queue at current position)
   - Suggests resume time estimate

3. **ResumeJobButton.tsx** (60 lines)
   - Simple button (no confirmation needed)
   - Updates job status to Printing
   - Shows updated status immediately

**API Integration**:
```typescript
async pauseJobAsync(jobId: string, reason?: string): Promise<void> { ... }
async resumeJobAsync(jobId: string): Promise<void> { ... }
```

**Accessibility**:
- aria-label: "Pause job [Name]" / "Resume job [Name]"
- Disabled state clearly indicated
- Focus management for modals

---

### Feature 3.4: Notes and Tags

**Purpose**: Add metadata for organization and searchability

**Backend Requirements**:
- Update PrintJob entity with:
  ```csharp
  public string Notes { get; set; } // Max 500 chars
  public List<JobTag> Tags { get; set; } // Navigation property
  ```
- Create JobTag entity:
  ```csharp
  public class JobTag
  {
      public string Id { get; set; }
      public string PrintJobId { get; set; }
      public string TagName { get; set; }
      public DateTime CreatedAt { get; set; }
  }
  ```
- New endpoints:
  - `PUT /api/printQueue/jobs/{jobId}/notes` - Update notes
  - `POST /api/printQueue/jobs/{jobId}/tags` - Add tag
  - `DELETE /api/printQueue/jobs/{jobId}/tags/{tagName}` - Remove tag

**Frontend Components**:

1. **NotesDisplay.tsx** (100 lines)
   - Shows notes with markdown support (optional)
   - Click to edit (inline or modal)
   - Auto-save with spinner
   - Show last-edited timestamp

2. **TagsDisplay.tsx** (130 lines)
   - Pill-style tags with colors
   - Tag search/filter integration
   - Quick remove (X button)
   - Hover tooltip with creation date

3. **TagAutocomplete.tsx** (160 lines)
   - Input field with autocomplete suggestions
   - Shows recently used tags
   - Shows common tags (Material, Project, Printer)
   - Category-based suggestions

**Features**:
- Tags searchable from FilterBar
- Notes visible in job card
- Tag colors by category (Material=blue, Project=green, Printer=orange)
- Recent tags for quick access
- Tag usage analytics (show most-used tags)

**Accessibility**:
- aria-label for tag pills
- Semantic form structure
- Clear focus indicators on input

---

### Feature 3.5: Estimated Print Time & Queue Position

**Purpose**: Help users predict job completion

**Backend Requirements**:
- Existing field: `EstimatedPrintTime` (TimeSpan)
- New calculation: Estimated completion time based on:
  - Sum of print times for jobs before this one
  - Current time + sum
  - Account for paused jobs (skip them in calculation)

**New Endpoint**:
- `GET /api/printQueue/jobs/{jobId}/timing` - Full timing breakdown
  ```json
  {
    "jobId": "...",
    "estimatedPrintTimeSeconds": 3600,
    "jobsAheadCount": 3,
    "estimatedWaitTimeSeconds": 5400,
    "estimatedCompletionTime": "2026-01-08T18:45:00Z",
    "estimatedCompletionDateDisplay": "Today at 6:45 PM",
    "isPausedAtTop": false
  }
  ```

**Frontend Components**:

1. **TimingEstimate.tsx** (120 lines)
   - Shows formatted timing information
   - Job position indicator (e.g., "4 of 12 in queue")
   - Time display (total, position, wait, completion)
   - Visual progress (bar showing position)

2. **CompletionTimeDisplay.tsx** (80 lines)
   - Friendly time format ("Today at 6:45 PM")
   - Human-readable countdown (e.g., "in 2 hours")
   - Updates every minute
   - Shows if estimate is stale (>5 min old)

3. **JobPositionBadge.tsx** (60 lines)
   - Chip/badge showing current queue position
   - Updates in real-time via SignalR
   - Color changes based on position (red=top, yellow=middle, green=far back)

**Calculation Logic**:
```typescript
function calculateEstimatedCompletion(
  currentJob: QueuedPrintJobDto,
  allQueuedJobs: QueuedPrintJobDto[],
  currentTime: Date
): Date {
  // Find all jobs with lower QueuePosition
  const jobsAhead = allQueuedJobs
    .filter(j => j.queuePosition < currentJob.queuePosition)
    .filter(j => j.status !== 'Paused'); // Exclude paused jobs
  
  // Sum their estimated times
  const waitTimeSeconds = jobsAhead.reduce(
    (sum, job) => sum + job.estimatedPrintTimeSeconds,
    0
  );
  
  // Add current job's time
  const totalSeconds = waitTimeSeconds + currentJob.estimatedPrintTimeSeconds;
  
  return new Date(currentTime.getTime() + totalSeconds * 1000);
}
```

**Accessibility**:
- aria-label for position badge
- Title attribute with full timing info
- Semantic time element: `<time datetime="...">`

---

## Implementation Phases

### Phase 3A: Foundation & Details Modal (Days 1-4)

**Deliverables**:
- Job details modal with view/edit capability
- New `JobDetailsModal` component (680 lines)
- Backend endpoint for full job details
- Tests for modal interactions

**Files to Create**:
- `src/Web/ReactApp/src/features/queue/components/JobDetailsModal.tsx`
- `src/Web/ReactApp/src/features/queue/components/JobDetailsSection.tsx`
- `src/Web/ReactApp/src/features/queue/components/JobNotesEditor.tsx`
- `src/Web/ReactApp/src/features/queue/components/JobTagsEditor.tsx`

**Files to Modify**:
- `src/api/Controllers/PrintQueueController.cs` - Add details endpoint (if not exists)
- `src/Web/ReactApp/src/services/printQueueService.ts` - Add client methods
- `src/Web/ReactApp/src/features/queue/PrintQueueDashboardPage.tsx` - Integrate modal

**Test Files**:
- `src/Web/ReactApp/src/features/queue/components/__tests__/JobDetailsModal.test.tsx`
- `src/Web/ReactApp/src/features/queue/components/__tests__/JobDetailsSection.test.tsx`

**Success Criteria**:
- Modal opens with full job details
- All fields display correctly
- Edit mode works (form validation, save/cancel)
- Tests cover 85%+ of modal logic
- 0 build errors

---

### Phase 3B: Drag-and-Drop Reordering (Days 5-8)

**Deliverables**:
- Draggable job rows with reordering
- Visual feedback and accessibility
- Confirm modal with preview
- Keyboard support

**Files to Create**:
- `src/Web/ReactApp/src/features/queue/components/DraggableJobRow.tsx`
- `src/Web/ReactApp/src/features/queue/components/ReorderableQueueList.tsx`
- `src/Web/ReactApp/src/features/queue/components/ReorderConfirmModal.tsx`

**Files to Modify**:
- `src/Web/ReactApp/src/features/queue/PrintQueueDashboardPage.tsx` - Replace with reorderable version
- `src/Web/ReactApp/src/services/printQueueService.ts` - Ensure reorder API call exists
- `package.json` - Add react-beautiful-dnd dependency

**Test Files**:
- `src/Web/ReactApp/src/features/queue/components/__tests__/DraggableJobRow.test.tsx`
- `src/Web/ReactApp/src/features/queue/components/__tests__/ReorderableQueueList.test.tsx`

**Success Criteria**:
- Drag handle visible and functional
- Smooth drag-and-drop with visual feedback
- Confirm modal shows before applying changes
- Keyboard reordering works (arrow keys)
- Mobile touch support
- API call succeeds and UI updates
- Tests cover drag scenarios

---

### Phase 3C: Pause/Resume & Controls (Days 9-11)

**Deliverables**:
- Pause and resume buttons integrated in queue
- Optional pause reason modal
- Status-aware action buttons
- Real-time status updates via SignalR

**Files to Create**:
- `src/Web/ReactApp/src/features/queue/components/PauseResumeButton.tsx`
- `src/Web/ReactApp/src/features/queue/components/PauseJobModal.tsx`

**Files to Modify**:
- `src/Web/ReactApp/src/features/queue/components/QueueJobsTable.tsx` - Add pause/resume buttons
- `src/Web/ReactApp/src/services/printQueueService.ts` - Add pause/resume methods
- `src/Web/ReactApp/src/features/queue/PrintQueueDashboardPage.tsx` - Add pause state handling

**Test Files**:
- `src/Web/ReactApp/src/features/queue/components/__tests__/PauseResumeButton.test.tsx`
- `src/Web/ReactApp/src/features/queue/components/__tests__/PauseJobModal.test.tsx`

**Success Criteria**:
- Pause button shows when job is printing
- Resume button shows when job is paused
- Modal asks for optional pause reason
- API calls work correctly
- Status updates in real-time
- Tests cover pause/resume scenarios

---

### Phase 3D: Notes, Tags & Timing (Days 12-15)

**Deliverables**:
- Notes editor with auto-save
- Tag management with autocomplete
- Estimated timing calculations
- Queue position indicators

**Files to Create**:
- `src/Web/ReactApp/src/features/queue/components/NotesDisplay.tsx`
- `src/Web/ReactApp/src/features/queue/components/TagsDisplay.tsx`
- `src/Web/ReactApp/src/features/queue/components/TagAutocomplete.tsx`
- `src/Web/ReactApp/src/features/queue/components/TimingEstimate.tsx`
- `src/Web/ReactApp/src/features/queue/components/CompletionTimeDisplay.tsx`
- `src/Web/ReactApp/src/features/queue/components/JobPositionBadge.tsx`

**Files to Modify**:
- `src/api/Entities/PrintJob.cs` - Add Notes property
- `src/api/Services/PrintQueue/PrintQueueService.cs` - Add timing calculation logic
- `src/Web/ReactApp/src/services/printQueueService.ts` - Add notes/tags/timing methods
- `src/Web/ReactApp/src/features/queue/PrintQueueDashboardPage.tsx` - Integrate new components
- `src/Web/ReactApp/src/features/queue/components/QueueJobsTable.tsx` - Add tags column

**Database Changes**:
- Add migration for Notes column (if not exists)
- Create JobTag table (if not exists)

**Test Files**:
- `src/Web/ReactApp/src/features/queue/components/__tests__/NotesDisplay.test.tsx`
- `src/Web/ReactApp/src/features/queue/components/__tests__/TagsDisplay.test.tsx`
- `src/Web/ReactApp/src/features/queue/components/__tests__/TimingEstimate.test.tsx`

**Success Criteria**:
- Notes visible and editable
- Tags with autocomplete working
- Timing calculations accurate
- Position badges update in real-time
- All timing displays show correct format
- Tests cover all new logic

---

## Testing Strategy

### Unit Tests (Vitest)

**Target**: 30+ new test files
- Component rendering and interactions
- Modal open/close/save/cancel flows
- Drag-and-drop interactions
- Form validation
- API call mocking

**Example Tests**:
```typescript
// JobDetailsModal.test.tsx
describe('JobDetailsModal', () => {
  it('should open modal with job details', () => { ... });
  it('should enable edit mode on edit button click', () => { ... });
  it('should save changes and close modal on save', () => { ... });
  it('should discard changes on cancel', () => { ... });
  it('should show error toast on save failure', () => { ... });
  it('should display notes with auto-save', () => { ... });
  it('should handle tag add/remove', () => { ... });
});

// ReorderableQueueList.test.tsx
describe('ReorderableQueueList', () => {
  it('should render draggable jobs', () => { ... });
  it('should show drop zones', () => { ... });
  it('should call API on successful drop', () => { ... });
  it('should revert UI on API failure', () => { ... });
  it('should keyboard navigate (arrow keys)', () => { ... });
});
```

### Integration Tests

**Target**: 10+ integration test scenarios
- Full job edit workflow (modal → save → verify)
- Reorder workflow (drag → confirm → apply)
- Pause/resume workflow (pause → resume → verify status)
- Notes and tags persistence
- Real-time updates via SignalR

### Manual Testing Checklist

**Functionality**:
- [ ] Open job details modal for each job status
- [ ] Edit each field in job details
- [ ] Save and discard changes
- [ ] Drag job to new position
- [ ] Confirm and apply reorder
- [ ] Pause printing job (pause reason optional)
- [ ] Resume paused job
- [ ] Add/edit/remove notes
- [ ] Add/remove tags (autocomplete works)
- [ ] Verify timing calculations accurate

**Accessibility**:
- [ ] Tab navigation through all modal fields
- [ ] Escape closes modal
- [ ] Screen reader announces job details
- [ ] Keyboard drag-and-drop (arrow keys)
- [ ] Focus management (trap in modal)
- [ ] ARIA labels present and correct

**Mobile**:
- [ ] Modal responsive on small screens
- [ ] Touch drag-and-drop works
- [ ] Buttons accessible with touch
- [ ] Tags autocomplete on mobile

**Performance**:
- [ ] Modal opens instantly
- [ ] Drag is smooth (60fps)
- [ ] No janky animations
- [ ] Large job lists (100+ jobs) load quickly

**Error Handling**:
- [ ] Network error shows toast
- [ ] Retry button available
- [ ] Rollback on API failure
- [ ] No stuck loading states

---

## Backend Enhancements

### Entity Updates

**PrintJob.cs** (Add if missing):
```csharp
public string Notes { get; set; } // Max 500 chars
public List<JobTag> Tags { get; set; } = new();
```

**JobTag.cs** (Create):
```csharp
public class JobTag
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PrintJobId { get; set; }
    public string TagName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public PrintJob PrintJob { get; set; }
}
```

### New API Endpoints

```csharp
// In PrintQueueController.cs

[HttpGet("jobs/{jobId}")]
public async Task<ActionResult<JobDetailsDto>> GetJobDetailsAsync(string jobId)
{
    // Return full job details with notes, tags, timing estimates
}

[HttpPut("jobs/{jobId}/notes")]
public async Task<IActionResult> UpdateJobNotesAsync(string jobId, [FromBody] string notes)
{
    // Update job notes
}

[HttpPost("jobs/{jobId}/tags")]
public async Task<IActionResult> AddJobTagAsync(string jobId, [FromBody] string tagName)
{
    // Add tag to job (check for duplicates)
}

[HttpDelete("jobs/{jobId}/tags/{tagName}")]
public async Task<IActionResult> RemoveJobTagAsync(string jobId, string tagName)
{
    // Remove tag from job
}

[HttpGet("jobs/{jobId}/timing")]
public async Task<ActionResult<TimingEstimateDto>> GetJobTimingAsync(string jobId)
{
    // Return timing estimates and queue position
}
```

### Service Method Enhancements

```csharp
// In PrintQueueService.cs

public async Task<JobDetailsDto> GetJobDetailsAsync(string jobId, CancellationToken cancellationToken)
{
    // Fetch job with all related data (notes, tags, timing)
}

public async Task UpdateJobNotesAsync(string jobId, string notes, CancellationToken cancellationToken)
{
    // Update job notes (validate length)
}

public async Task AddJobTagAsync(string jobId, string tagName, CancellationToken cancellationToken)
{
    // Add tag with duplicate prevention
}

public async Task RemoveJobTagAsync(string jobId, string tagName, CancellationToken cancellationToken)
{
    // Remove tag
}

public async Task<TimingEstimateDto> GetJobTimingAsync(string jobId, CancellationToken cancellationToken)
{
    // Calculate estimated completion time based on queue
}
```

### Database Migration

```csharp
// Create migration: AddNotesAndTagsToJobs
// Changes:
// 1. Add Notes column to PrintJobs table (nvarchar(500), nullable)
// 2. Create JobTags table with FK to PrintJobs
// 3. Add index on JobTags.PrintJobId for performance
```

---

## Deployment & Verification

### Pre-Deployment Checklist

- [ ] All 292+ existing tests passing
- [ ] 30+ new Phase 3 tests added and passing
- [ ] Backend Release build: 0 errors
- [ ] Frontend TypeScript: 0 errors
- [ ] Code review approved
- [ ] Database migration tested on clean DB
- [ ] Performance tested with 100+ jobs
- [ ] Accessibility audit passed
- [ ] Error scenarios tested manually

### Post-Deployment Validation

```bash
# API Health Check
curl http://localhost:5245/healthz

# Test Job Details Endpoint
curl http://localhost:5245/api/printQueue/jobs/{jobId}

# Test Pause Endpoint
curl -X POST http://localhost:5245/api/printQueue/jobs/{jobId}/pause

# Test Timing Endpoint
curl http://localhost:5245/api/printQueue/jobs/{jobId}/timing

# React Build Check
cd src/Web/ReactApp
npm run build  # Should complete successfully

# Test Runner
npm run test:run  # All 322+ tests passing
```

---

## Risks & Mitigation

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|-----------|
| Database migration fails | Medium | High | Test migration on staging DB first, have rollback plan |
| Drag-and-drop jank on large lists | Medium | Medium | Virtual scrolling, pagination limits, performance testing |
| Modal focus trapping breaks accessibility | Low | Medium | Test with screen reader, ARIA audit before deploy |
| Race condition on concurrent reorders | Low | High | Implement optimistic locking or version field |
| Real-time updates out of sync | Medium | Medium | SignalR health checks, manual refresh button |
| Form validation on tags fails | Low | Low | Input sanitization, max length validation |

---

## Definition of Done (Phase 3)

- ✅ All 5 features implemented (details, reorder, pause/resume, notes/tags, timing)
- ✅ 30+ new unit tests with 85%+ coverage
- ✅ 10+ integration test scenarios
- ✅ All 322+ tests passing (292 existing + 30 new)
- ✅ Backend Release build: 0 errors
- ✅ Frontend TypeScript: 0 errors
- ✅ Code formatted and linted
- ✅ Accessibility audit passed
- ✅ Performance tested with 100+ jobs
- ✅ Database migrations tested
- ✅ Documentation updated (this plan + README)
- ✅ Git commits with clear messages

---

## Timeline & Milestones

| Phase | Duration | Milestone | Target Date |
|-------|----------|-----------|------------|
| 3A | Days 1-4 | Job Details Modal | Jan 11, 2026 |
| 3B | Days 5-8 | Drag-and-Drop Reordering | Jan 15, 2026 |
| 3C | Days 9-11 | Pause/Resume Controls | Jan 18, 2026 |
| 3D | Days 12-15 | Notes, Tags, Timing | Jan 22, 2026 |
| Testing & Polish | Days 16-17 | Final QA, Accessibility Audit | Jan 24, 2026 |
| **Complete** | **17 days** | **Phase 3 Ready for Production** | **Jan 24, 2026** |

---

## Next Actions

**Immediate** (Today - Jan 8):
1. Create Phase 3A task list in issue tracker
2. Set up branch: `feat/phase-3-job-management`
3. Review existing reorder endpoint code

**Phase 3A Kickoff** (Jan 9):
1. Create JobDetailsModal component structure
2. Design modal layout (tabs/sections)
3. Implement service methods for job details
4. Start unit tests

**Phase 3B Prep** (After 3A):
1. Select drag-and-drop library (react-beautiful-dnd)
2. Install and configure
3. Design reorderable list component

**Phase 3C-D Prep**:
1. Database schema review for notes/tags
2. Migration planning
3. Backend endpoint design review

---

## Success Metrics

After Phase 3 completion:
- **Code Quality**: 0 build errors, 100% test pass rate, 85%+ coverage
- **Performance**: Drag operations smooth (60fps), modal opens <500ms
- **User Experience**: All 5 features intuitive and keyboard-accessible
- **Reliability**: Real-time updates consistent, no race conditions
- **Maintainability**: Clear component structure, well-documented code

---

## Reference: Related Phase 1 & 2 Artifacts

- **Main Plan**: `/docs/PRINT_QUEUE_REDESIGN_PLAN.md`
- **Implementation Guide**: `/docs/PRINT_QUEUE_REDESIGN_IMPLEMENTATION.md`
- **Phase 2 Documentation**: `/docs/PHASE_2_ENHANCED_QUEUING_PLAN.md`
- **Test Coverage**: All tests in `src/Web/ReactApp/src/features/queue/components/__tests__/`
- **Git History**: See commits `cc89f1c3` (Phase 2C.5) and `b218f1fd` (Phase 2 consolidation)

---

**Last Updated**: January 8, 2026  
**Status**: 🔄 Ready for Phase 3A Kickoff  
**Owner**: PrintFarmer Development Team
