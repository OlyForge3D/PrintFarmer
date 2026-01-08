# Phase 3D: Advanced Tag Management - Implementation Plan

**Status**: 🔄 IN PROGRESS (Kickoff January 8, 2026)  
**Timeline**: 5 days (Target completion: January 13, 2026)  
**Priority**: High - Foundational tag management infrastructure

---

## Overview

Phase 3D extends the 3D model tagging system with comprehensive backend support, user-facing tagging features, filtering, autocomplete suggestions, and tag analytics. This builds on the basic tag infrastructure established in earlier phases to provide a complete tag management solution for organizing and discovering 3D models.

**Phase Dependencies**: Phase 3C (✅ Complete) → Phase 3D (🔄 IN PROGRESS)

---

## Objectives & Success Criteria

### Primary Objectives

1. **Full Backend Tag Support**
   - Extend TagService with comprehensive operations
   - Implement tag-based searching and filtering
   - Database schema optimizations (indexing, constraints)
   - Multi-provider database support validation

2. **Tag-Based Filtering**
   - Filter 3D models by single and multiple tags
   - Exclude models with specific tags
   - Combine tag filters with other criteria
   - Save filter preferences (optional)

3. **Tag Suggestions & Autocomplete**
   - Real-time tag suggestions as user types
   - Popular tags display
   - Recently used tags
   - Fuzzy matching for typo tolerance

4. **Tag Analytics & Management**
   - Tag usage statistics (count of models per tag)
   - Most used tags dashboard
   - Tag cleanup (merge, deprecate, delete)
   - Tag creation validation and standards

5. **User-Facing Tag UI**
   - Tag editing in model details modal
   - Tag display with visual design system
   - Tag input with autocomplete
   - Tag removal with confirmation
   - Tag search in model browser

### Success Criteria (Definition of Done)

- ✅ `TagService` with 8+ operations (list, add, remove, get by ID, search, get popular, analytics, cleanup)
- ✅ Database indexing on `Model3DTag` table for performance
- ✅ Tag filtering endpoints returning filtered model lists
- ✅ Autocomplete endpoint with suggestions and popular tags
- ✅ React `TagInput` component with autocomplete and validation
- ✅ React `TagDisplay` component with visual styling
- ✅ `ModelDetailsModal` integration with tag editing
- ✅ Tag analytics dashboard component
- ✅ 40+ new unit tests (backend + frontend)
- ✅ All existing tests still passing (365 React + 1634 .NET)
- ✅ Keyboard accessibility (ARIA labels, focus management)
- ✅ Mobile-responsive tag UI
- ✅ Build succeeds with 0 errors
- ✅ Production-ready code

---

## Architecture Overview

### Backend Changes

**Services Layer** (`src/api/Services/`)
- `TagService.cs` - Enhanced with 8+ operations
  - `ListAsync()` - Get all tags with usage counts
  - `AddAsync(tag)` - Create new tag with normalization
  - `RemoveAsync(tag)` - Delete tag
  - `GetByIdAsync(id)` - Retrieve specific tag
  - `SearchAsync(query)` - Search tags by name
  - `GetPopularAsync(count)` - Get most used tags
  - `GetAnalyticsAsync()` - Tag usage statistics
  - `MergeAsync(sourceTag, targetTag)` - Merge duplicate tags

**Controllers** (`src/api/Controllers/`)
- `CatalogController` (new routes)
  - `GET /api/catalog/tags` - List all tags with counts
  - `GET /api/catalog/tags/search?q=...` - Search tags
  - `GET /api/catalog/tags/popular?count=10` - Popular tags
  - `GET /api/catalog/tags/analytics` - Tag statistics
  - `GET /api/catalog/models/filter?tags=...` - Filter models by tags
  - `POST /api/catalog/tags` - Create tag
  - `DELETE /api/catalog/tags/{id}` - Delete tag
  - `POST /api/catalog/tags/merge` - Merge tags

**DTOs** (`src/shared/Models.cs`)
- `TagDto` - Basic tag information
- `TagAnalyticsDto` - Usage statistics
- `TagSuggestionDto` - Suggestion with usage count
- `ModelFilterOptionsDto` - Filter criteria

**Database** (`src/api/Data/`)
- EF Core migration for tag indexing
- Index on `Model3DTag.Name` for performance
- Index on `Model3DTag.CreatedAt` for analytics

### Frontend Changes

**React Components** (`src/Web/ReactApp/src/components/`)
- `TagInput.tsx` - Input with autocomplete
  - Autocomplete suggestions
  - Popular tags dropdown
  - Validation (reserved names, max length)
  - Keyboard navigation (arrow keys, Enter, Escape)
  - ARIA labels and announcements

- `TagDisplay.tsx` - Visual tag renderer
  - Design system styling
  - Inline remove option
  - Optional click handler
  - Color coding based on usage

- `TagAnalyticsDashboard.tsx` - Tag statistics
  - Tag usage chart
  - Most used tags list
  - Tag creation trends
  - Tag cleanup suggestions

**Modals** (`src/Web/ReactApp/src/components/modals/`)
- `ModelDetailsModal.tsx` - Enhanced with tags
  - Tag editing section
  - Tag input with autocomplete
  - Tag display with removal
  - Save changes with validation

**Services** (`src/Web/ReactApp/src/services/`)
- `tagService.ts` - API client
  - `listTags()` - Get all tags
  - `searchTags(query)` - Search suggestions
  - `getPopularTags(count)` - Popular tags
  - `getAnalytics()` - Usage statistics
  - `createTag(name)` - Create tag
  - `deleteTag(id)` - Delete tag
  - `filterModelsByTags(tags, excludeTags)` - Filter models

---

## Implementation Phases

### Phase 3D.1: Backend Infrastructure & Enhanced TagService

**Duration**: 1.5 days  
**Priority**: P0 - Blocking all other features

#### Subtasks

- [ ] 3D.1.1: Extend `TagService` with 8 operations
  - [ ] `ListAsync()` with usage counts
  - [ ] `SearchAsync(query)` with fuzzy matching
  - [ ] `GetPopularAsync(count)` sorted by usage
  - [ ] `GetAnalyticsAsync()` with statistics
  - [ ] `MergeAsync()` for tag consolidation
  - [ ] Input validation and error handling

- [ ] 3D.1.2: Database optimization
  - [ ] Create EF Core migration for tag indexing
  - [ ] Add index on `Model3DTag.Name`
  - [ ] Add index on `Model3DTag.CreatedAt`
  - [ ] Test with multiple providers (SQLite, PostgreSQL, SQL Server, MySQL)

- [ ] 3D.1.3: Create backend DTOs
  - [ ] `TagDto` structure
  - [ ] `TagAnalyticsDto` with counts and trends
  - [ ] `TagSuggestionDto` with usage info
  - [ ] JSON serialization with camelCase

- [ ] 3D.1.4: Implement `CatalogController` routes
  - [ ] `GET /api/catalog/tags`
  - [ ] `GET /api/catalog/tags/search?q=...`
  - [ ] `GET /api/catalog/tags/popular`
  - [ ] `GET /api/catalog/tags/analytics`
  - [ ] Error handling and validation

- [ ] 3D.1.5: Write backend unit tests (15+ tests)
  - [ ] TagService operations
  - [ ] Database indexing verification
  - [ ] Controller endpoint behavior
  - [ ] Error scenarios

- [ ] 3D.1.6: Verify API endpoints
  - [ ] Health check passes
  - [ ] Database migrations apply successfully
  - [ ] All endpoints return expected data

**Exit Criteria**:
- ✅ `TagService` has 8+ operations with full test coverage
- ✅ Database indexes created and verified
- ✅ 4 new API endpoints working (list, search, popular, analytics)
- ✅ 15+ backend tests passing
- ✅ All existing .NET tests still passing
- ✅ Build succeeds with 0 errors

**Validation Checklist**:
- [ ] Run `dotnet test ./farm-web.sln -c Release` - All 1634+ tests passing
- [ ] Run `curl http://localhost:5245/api/catalog/tags` - Returns tag list
- [ ] Run `curl http://localhost:5245/api/catalog/tags/popular` - Returns popular tags
- [ ] Database migration applied successfully

---

### Phase 3D.2: Tag Filtering & Query Optimization

**Duration**: 1 day  
**Priority**: P0 - Enables filtering features

#### Subtasks

- [ ] 3D.2.1: Implement tag filtering logic
  - [ ] `FilterByTagsAsync(tags)` - Include models with ANY tag
  - [ ] `FilterByAllTagsAsync(tags)` - Include models with ALL tags
  - [ ] `ExcludeByTagsAsync(tags)` - Exclude models with ANY tag
  - [ ] Combined filtering (include + exclude)

- [ ] 3D.2.2: Add filtering endpoints
  - [ ] `GET /api/catalog/models/filter?tags=tag1,tag2&excludeTags=tag3`
  - [ ] Support pagination with filters
  - [ ] Return count of filtered results

- [ ] 3D.2.3: Optimize query performance
  - [ ] Use LINQ `.Include()` to avoid N+1 queries
  - [ ] Profile query performance
  - [ ] Test with 1000+ models and tags

- [ ] 3D.2.4: Write filtering tests (10+ tests)
  - [ ] Single tag filtering
  - [ ] Multiple tag combinations
  - [ ] Exclude tag logic
  - [ ] Performance benchmarks

- [ ] 3D.2.5: Verify filtering works
  - [ ] Add test models with various tag combinations
  - [ ] Test each filter scenario
  - [ ] Verify pagination works

**Exit Criteria**:
- ✅ Filtering endpoints implemented and tested
- ✅ N+1 query issues resolved
- ✅ 10+ tests passing for filtering logic
- ✅ All existing tests still passing

**Validation Checklist**:
- [ ] Run `curl http://localhost:5245/api/catalog/models/filter?tags=test` - Returns filtered list
- [ ] Query performance: <100ms for 1000+ models
- [ ] All filtering tests pass

---

### Phase 3D.3: Frontend Tag Components & Input

**Duration**: 1.5 days  
**Priority**: P1 - UI implementation

#### Subtasks

- [ ] 3D.3.1: Create `TagInput.tsx` component
  - [ ] Input field with autocomplete suggestions
  - [ ] Popular tags dropdown
  - [ ] Recently used tags section
  - [ ] Keyboard navigation (arrow keys, Enter)
  - [ ] Escape to close suggestions
  - [ ] Tag validation (no duplicates, max length)
  - [ ] ARIA labels and announcements
  - [ ] Design token styling

- [ ] 3D.3.2: Create `TagDisplay.tsx` component
  - [ ] Visual tag renderer with design tokens
  - [ ] Inline remove button (optional)
  - [ ] Hover effects and transitions
  - [ ] Optional click handler for filtering
  - [ ] Color coding based on usage (optional)

- [ ] 3D.3.3: Create `tagService.ts` client
  - [ ] `listTags()` API call
  - [ ] `searchTags(query)` with debouncing
  - [ ] `getPopularTags(count)` call
  - [ ] `getAnalytics()` call
  - [ ] Error handling and retry logic

- [ ] 3D.3.4: Write component tests (15+ tests)
  - [ ] TagInput rendering and interaction
  - [ ] TagDisplay rendering with/without remove
  - [ ] Autocomplete functionality
  - [ ] Keyboard accessibility
  - [ ] Service integration

- [ ] 3D.3.5: Verify components
  - [ ] React dev server running
  - [ ] Components render without errors
  - [ ] All tests passing

**Exit Criteria**:
- ✅ `TagInput` component with autocomplete and keyboard nav
- ✅ `TagDisplay` component with design tokens
- ✅ `tagService.ts` with API integration
- ✅ 15+ component tests passing
- ✅ Accessibility WCAG 2.2 AA compliant

**Validation Checklist**:
- [ ] Run `npm run test:run` in ReactApp - All 365+ tests passing
- [ ] Dev server running: `npm run dev`
- [ ] Components render without console errors
- [ ] Keyboard navigation works (Tab, Arrow keys, Enter)

---

### Phase 3D.4: Model Details Modal & Tag Integration

**Duration**: 1 day  
**Priority**: P1 - Feature completion

#### Subtasks

- [ ] 3D.4.1: Enhance `ModelDetailsModal.tsx`
  - [ ] Add tags section
  - [ ] Integrate `TagInput` component
  - [ ] Display current tags with `TagDisplay`
  - [ ] Add/remove tags with visual feedback
  - [ ] Save tag changes to database
  - [ ] Show loading state while saving

- [ ] 3D.4.2: Enhance `ModelBrowser.tsx`
  - [ ] Add tag filter section
  - [ ] Integrate `TagInput` for filtering
  - [ ] Display filtered results count
  - [ ] Save filter preferences (optional)
  - [ ] Show popular tags

- [ ] 3D.4.3: Write integration tests (10+ tests)
  - [ ] Modal tag editing flow
  - [ ] Filter application and results
  - [ ] Tag persistence
  - [ ] Keyboard navigation through tags

- [ ] 3D.4.4: Verify integration
  - [ ] Modal opens/closes correctly
  - [ ] Tags save and persist
  - [ ] Filtering updates model list
  - [ ] All interactions accessible via keyboard

**Exit Criteria**:
- ✅ `ModelDetailsModal` fully integrated with tags
- ✅ `ModelBrowser` has tag filtering
- ✅ All tag operations persist to database
- ✅ 10+ integration tests passing

**Validation Checklist**:
- [ ] Open model details modal
- [ ] Add/remove tags in modal
- [ ] Changes persist after reload
- [ ] Tag filtering in model browser works
- [ ] All tests passing (365+ React, 1634+ .NET)

---

### Phase 3D.5: Tag Analytics Dashboard & Polish

**Duration**: 0.5 days  
**Priority**: P2 - Analytics and refinement

#### Subtasks

- [ ] 3D.5.1: Create `TagAnalyticsDashboard.tsx`
  - [ ] Display tag usage statistics
  - [ ] Chart showing most used tags
  - [ ] Tag creation trends (optional)
  - [ ] Tag cleanup recommendations
  - [ ] Design system styling

- [ ] 3D.5.2: Add analytics to catalog page
  - [ ] Display tag analytics section
  - [ ] Link to tag management (optional)
  - [ ] Show tag health metrics

- [ ] 3D.5.3: Polish and refinement
  - [ ] Responsive design (mobile-first)
  - [ ] Design token consistency
  - [ ] Error state handling
  - [ ] Loading states for all async operations
  - [ ] Empty state messaging

- [ ] 3D.5.4: Write final tests (5+ tests)
  - [ ] Dashboard rendering
  - [ ] Statistics display
  - [ ] Responsive breakpoints

**Exit Criteria**:
- ✅ Tag analytics dashboard implemented
- ✅ All UI polished and responsive
- ✅ 5+ dashboard tests passing
- ✅ All Phase 3D features complete and tested

**Validation Checklist**:
- [ ] Dashboard displays tag statistics correctly
- [ ] Responsive at 320px, 640px, 1024px breakpoints
- [ ] Design tokens used consistently
- [ ] All tests passing (370+ React, 1634+ .NET)

---

## Testing & Validation

### Test Coverage

**Target: 40+ new unit tests**
- Backend: 15+ tests (TagService, filtering, analytics)
- Frontend: 25+ tests (components, integration, accessibility)

**Test Categories**:
1. **Unit Tests**: Individual function behavior
2. **Integration Tests**: Component-service interactions
3. **Accessibility Tests**: WCAG 2.2 AA compliance (keyboard nav, ARIA labels)
4. **Responsive Design Tests**: 3 breakpoints (320px, 640px, 1024px)
5. **Performance Tests**: Query performance, component render time

### API Endpoint Validation

```bash
# List all tags
curl http://localhost:5245/api/catalog/tags

# Search tags
curl http://localhost:5245/api/catalog/tags/search?q=material

# Get popular tags
curl http://localhost:5245/api/catalog/tags/popular?count=10

# Get tag analytics
curl http://localhost:5245/api/catalog/tags/analytics

# Filter models by tags
curl http://localhost:5245/api/catalog/models/filter?tags=plastic,abs
```

### UI Validation

- [ ] Tag input shows suggestions as user types
- [ ] Popular tags display on focus
- [ ] Keyboard: Tab navigates, Arrow keys select, Enter confirms
- [ ] Tag display shows visual badge with name
- [ ] Remove button removes tag with confirmation
- [ ] Model details modal saves tag changes
- [ ] Model browser filters by tags correctly
- [ ] Analytics dashboard shows statistics
- [ ] All components responsive at 320px, 640px, 1024px
- [ ] All interactive elements keyboard accessible
- [ ] Screen reader announces tag count and status
- [ ] Form errors display inline with clear messaging

### Accessibility Checklist (WCAG 2.2 Level AA)

- [ ] All interactive elements focusable with Tab key
- [ ] Focus visible and clear
- [ ] ARIA labels on all inputs
- [ ] ARIA announcements for dynamic updates
- [ ] Color not sole means of conveying information
- [ ] Contrast ratios ≥ 4.5:1 for normal text, ≥ 3:1 for large text
- [ ] Links and buttons distinguishable by more than color
- [ ] No keyboard traps
- [ ] Form validation messages clear and associated with fields
- [ ] Screen reader testing (NVDA, JAWS, VoiceOver)

---

## Exit Criteria & Sign-Off

### Build & Deployment

- ✅ .NET build succeeds: `dotnet build ./farm-web.sln -c Release` (0 errors)
- ✅ React build succeeds: `npm run build` (0 errors)
- ✅ All tests pass: 365+ React tests + 1634+ .NET tests (100%)
- ✅ Code formatted: `dotnet format` and `npm run lint` pass
- ✅ App deployed: Verify at http://10.0.0.20:8080
- ✅ API endpoints verified: All 8+ routes responding correctly

### Feature Completeness

- ✅ Backend: TagService with 8+ operations
- ✅ Backend: Database indexing and optimization
- ✅ Backend: 4 new API endpoints (list, search, popular, analytics)
- ✅ Frontend: TagInput component with autocomplete
- ✅ Frontend: TagDisplay component with styling
- ✅ Frontend: ModelDetailsModal with tag editing
- ✅ Frontend: ModelBrowser with tag filtering
- ✅ Frontend: TagAnalyticsDashboard (optional)

### Quality Metrics

- ✅ Test Coverage: ≥40 new tests (backend + frontend)
- ✅ Code Quality: No ESLint errors (React) or warnings (build)
- ✅ Accessibility: WCAG 2.2 Level AA compliant
- ✅ Performance: Query execution <100ms for 1000+ models
- ✅ Responsive: Mobile-first design (320px+)

### Documentation

- ✅ Phase 3D Implementation Plan updated with completion status
- ✅ Exit criteria documented
- ✅ Test results logged
- ✅ Deployment status recorded

---

## Deployment Notes

### Build & Run

```bash
# Build
cd /home/pi/pfarm/src
dotnet build ./farm-web.sln -c Release

# Test
dotnet test ./farm-web.sln -c Release
cd ../Web/ReactApp && npm run test:run

# Deploy
cd /home/pi/pfarm
./scripts/deploy-docker.sh --non-interactive --tear-down
```

### Verification

```bash
# Health check
curl http://localhost:5245/healthz

# API endpoints
curl http://localhost:5245/api/catalog/tags
curl http://localhost:5245/api/catalog/tags/popular

# Frontend
curl http://localhost:8080/ | head -5
```

### Rollback Plan

- Previous deployment: Docker containers saved
- Database: Automatic migrations with safety checks
- Frontend: CDN cache invalidation (if applicable)

---

## Sign-Off (To be completed)

**Phase 3D.1 - Backend Infrastructure**
- Status: 🔄 IN PROGRESS
- Completed: [pending]
- Test Results: [pending]
- Deployment: [pending]

**Phase 3D.2 - Tag Filtering**
- Status: 🔜 READY TO START
- Completed: [pending]
- Test Results: [pending]

**Phase 3D.3 - Frontend Components**
- Status: 🔜 READY TO START
- Completed: [pending]
- Test Results: [pending]

**Phase 3D.4 - Modal Integration**
- Status: 🔜 READY TO START
- Completed: [pending]
- Test Results: [pending]

**Phase 3D.5 - Analytics & Polish**
- Status: 🔜 READY TO START
- Completed: [pending]
- Test Results: [pending]

**Phase 3D Overall Completion**

- Status: 🔄 IN PROGRESS (Kickoff January 8, 2026)
- Estimated Completion: January 13, 2026
- All exit criteria met: [pending]
- All tests passing: [pending]
- Production deployment: [pending]

**Sign-Off**: [pending - awaiting Phase 3D.1 completion]

All planning complete. Phase 3D implementation ready to begin with Phase 3D.1 (Backend Infrastructure).

---

**Next Phase**: Phase 4 (Planned)
