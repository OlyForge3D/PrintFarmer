# New File Browser Implementation Plan

**Start Date:** January 13, 2026  
**Target Completion:** January 20-27, 2026 (estimated 12-16 hours)  
**Status:** 🟡 IN PROGRESS

---

## Overview

This document tracks the implementation of new ModelsPage and GcodeLibraryPage wrapper components that integrate the recently completed infrastructure foundation (GenericFileBrowser, ModelsFileBrowser, GcodeFileBrowser).

**Infrastructure Status:** ✅ COMPLETE
- GenericFileBrowser abstraction
- Domain-specific wrappers (GcodeFileBrowser, ModelsFileBrowser)
- Horizontal master-detail layout for FilesPage
- Collapsible tree views with drag-drop support
- Upload modal with progress tracking
- View mode persistence and backwards compatibility

**Current Build Status:**
- ✅ 393+ tests passing
- ✅ 0 TypeScript errors
- ✅ 0 ESLint violations
- ✅ Build time: ~10 seconds

---

## Implementation Phases

### Phase 1: ModelsPage.tsx (3-4 hours) - 🟡 IN PROGRESS

**Objective:** Create ModelsPage wrapper component with full selection, tagging, and deletion support.

**File:** `src/Web/ReactApp/src/features/models3d/pages/ModelsPage.tsx`

**Tasks:**
- [ ] 1.1: Basic structure & ModelsFileBrowser integration (1h)
  - Create component with state hooks
  - Wire ModelsFileBrowser with initial props
  - Test basic rendering
  
- [ ] 1.2: Selection management & toolbar integration (0.5h)
  - Implement selectedModelIds state
  - Wire to ModelsFileBrowser selectedModelIds prop
  - Verify selection persists across view mode changes
  
- [ ] 1.3: Delete flow (1h)
  - Implement handleDeleteModels mutation
  - Create/wire ConfirmationModal
  - Add confirmation before delete
  - Handle API errors
  
- [ ] 1.4: Tag modal integration (0.5h)
  - Verify BulkTagModal component exists
  - Implement handleTagModels mutation
  - Wire tag button with selected count
  - Test bulk tagging
  
- [ ] 1.5: Testing & accessibility (0.5h)
  - Unit tests for page logic
  - Accessibility audit (WCAG 2.2 AA)
  - Verify 393+ tests still pass
  - ESLint: 0 violations

**Component Structure:**
```typescript
interface ModelsPageState {
  selectedModelIds: string[];
  showTagModal: boolean;
  deleteConfirmation: { show: boolean; modelIds: string[] };
}

interface ModelsPageHandlers {
  handleSelectModels(modelIds: string[]): void;
  handleDeleteModels(modelIds: string[]): Promise<void>;
  handleTagModels(tags: string[]): Promise<void>;
  handleNavigateToViewer(modelId: string): void;
}
```

**Acceptance Criteria:**
- ✅ Selection works in both grid and explorer views
- ✅ Tag button shows correct count and is disabled when nothing selected
- ✅ Delete confirmation shows model count
- ✅ Bulk delete operation completes successfully
- ✅ Bulk tag operation applies to all selected models
- ✅ Upload modal integrates without conflicts
- ✅ All 393+ tests pass
- ✅ 0 TypeScript errors
- ✅ WCAG 2.2 Level AA compliant

**API Endpoints Required:**
- `DELETE /3d-models` - Delete multiple models (via deleteModels(modelIds))
- `POST /3d-models/{id}/tags` - Update model tags (via updateModelTags(modelIds, tags))

**Component Dependencies:**
- ✅ ModelsFileBrowser (ready)
- ⚠️ BulkTagModal (verify exists)
- ✅ ConfirmationModal (exists)
- ✅ useAuth hook (exists)

---

### Phase 2: GcodeLibraryPage.tsx (2-3 hours) - 📋 TODO

**Objective:** Create GcodeLibraryPage wrapper component with deletion support and upload integration.

**File:** `src/Web/ReactApp/src/features/gcode/pages/GcodeLibraryPage.tsx`

**Tasks:**
- [ ] 2.1: Basic structure & GcodeFileBrowser integration (1h)
  - Create component
  - Wire GcodeFileBrowser with initial props
  - Test rendering
  
- [ ] 2.2: Delete flow (0.5h)
  - Implement handleDeleteGcodeFiles mutation
  - Wire delete confirmation modal
  - Test delete operation
  
- [ ] 2.3: Optional printer filter (0.5h)
  - Add printer dropdown filter
  - Wire to GcodeFileBrowser printerId prop
  - Test filtering
  
- [ ] 2.4: Testing & accessibility (0.5h)
  - Unit tests
  - Accessibility audit
  - Verify tests pass

**Component Structure:**
```typescript
interface GcodeLibraryPageState {
  printerId?: string;
  deleteConfirmation: { show: boolean; filePaths: string[] };
}

interface GcodeLibraryPageHandlers {
  handleDeleteGcodeFiles(paths: string[]): Promise<void>;
  handleFilterByPrinter(printerId?: string): void;
}
```

**Acceptance Criteria:**
- ✅ Delete confirmation shows correct file count
- ✅ Bulk delete completes successfully
- ✅ Upload modal works seamlessly
- ✅ Upload progress displays correctly
- ✅ Printer filter (if added) filters correctly
- ✅ Pagination works for large file counts
- ✅ Search functionality works
- ✅ All tests pass

**API Endpoints Required:**
- `DELETE /gcode-files` - Delete G-code files (via deleteGcodeFiles(paths))
- ✅ `POST /gcode-files/upload-multiple` - Upload files (via uploadMultipleGcodeLibraryFiles)

**Component Dependencies:**
- ✅ GcodeFileBrowser (ready)
- ✅ ConfirmationModal (exists)
- ✅ useAuth hook (exists)

---

### Phase 3: HarvestPage.tsx (1-2 hours) - 📋 TODO

**Objective:** Verify or update HarvestPage to work with new file browser architecture.

**File:** `src/Web/ReactApp/src/features/gcode/pages/HarvestPage.tsx`

**Tasks:**
- [ ] 3.1: Review original HarvestPage implementation (0.5h)
  - Understand original purpose and structure
  - Identify if it needs new browser integration
  
- [ ] 3.2: Determine integration approach (0.5h)
  - Separate harvest operation tracker vs file browser
  - Decide on scope
  
- [ ] 3.3: Implement or verify (0.5h)
  - Update if needed
  - Test integration
  
- [ ] 3.4: Testing (0.25h)
  - Verify tests pass

**Note:** This page was reverted in cleanup; need to understand its original purpose before integration.

---

### Phase 4: Grid View Components (1-2 hours) - 📋 TODO

**Objective:** Verify ModelGridView and GcodeGridView exist and work with new browser components.

**Tasks:**
- [ ] 4.1: Verify ModelGridView exists (0.25h)
  - Check `src/Web/ReactApp/src/features/models3d/components/ModelGridView.tsx`
  - Verify prop interface matches ModelsFileBrowser expectations
  
- [ ] 4.2: Verify GcodeGridView exists (0.25h)
  - Check `src/Web/ReactApp/src/features/gcode/components/GcodeGridView.tsx`
  - Verify prop interface matches GcodeFileBrowser expectations
  
- [ ] 4.3: Create missing components if needed (1-1.5h)
  - ModelGridView: Grid of model cards with thumbnail, name, size, tags, buttons
  - GcodeGridView: Grid of G-code cards with preview, name, size, printer, buttons
  
- [ ] 4.4: Test integration (0.5h)
  - Verify adapters work with grid views
  - Test view mode switching

---

### Phase 5: Accessibility & Full Testing (3-4 hours) - 📋 TODO

**Objective:** Complete accessibility audit and comprehensive testing.

**Tasks:**
- [ ] 5.1: WCAG 2.2 Level AA compliance audit (1.5h)
  - Verify ARIA labels on all interactive elements
  - Test keyboard navigation (Tab, Enter, Escape)
  - Check color contrast (≥ 4.5:1)
  - Screen reader testing (NVDA/JAWS/VoiceOver if available)
  - Focus management in modals
  
- [ ] 5.2: Unit & integration testing (1h)
  - Write tests for page selection logic
  - Write tests for bulk operations (delete, tag)
  - Write tests for delete confirmation flows
  - Write tests for upload modal integration
  
- [ ] 5.3: E2E testing (0.5h)
  - Test complete user journeys
  - Test cross-view-mode workflows
  - Test error handling
  
- [ ] 5.4: Build validation (0.5h)
  - Run `npm run build` - must complete in < 11 seconds
  - Run `npm run lint` - must show 0 errors
  - Run `npm run test:run` - must pass all 393+ tests
  - Verify no TypeScript compilation errors

**Accessibility Checklist:**
- [ ] ARIA labels on all buttons and form inputs
- [ ] Keyboard shortcuts documented (e.g., 't' cycles tabs)
- [ ] Focus visible on all interactive elements
- [ ] Modal focus trap working correctly
- [ ] Error messages associated with fields via aria-describedby
- [ ] Color contrast verified with Chrome DevTools
- [ ] Screen reader announcements tested
- [ ] Page structure semantic (landmarks, headings)

---

## Progress Tracking

| Phase | Task | Status | Hours | Notes |
|-------|------|--------|-------|-------|
| 1 | ModelsPage basic structure | 🟡 IN PROGRESS | 1h | Starting now |
| 1 | Selection management | 📋 TODO | 0.5h | Dependent on 1.1 |
| 1 | Delete flow | 📋 TODO | 1h | Dependent on 1.1 |
| 1 | Tag modal | 📋 TODO | 0.5h | Dependent on 1.1 |
| 1 | Testing & A11y | 📋 TODO | 0.5h | Dependent on all above |
| 2 | GcodeLibraryPage | 📋 TODO | 2-3h | After Phase 1 |
| 3 | HarvestPage | 📋 TODO | 1-2h | After Phase 2 |
| 4 | Grid view components | 📋 TODO | 1-2h | Parallel to Phase 2-3 |
| 5 | Accessibility & testing | 📋 TODO | 3-4h | After all phases |
| **TOTAL** | | | **12-16h** | |

---

## Key Dependencies & Validation

### API Endpoints
- ✅ `GET /3d-models/search` - List models (ModelsFileBrowser)
- ✅ `GET /gcode-files/hierarchy` - List G-code files (GcodeFileBrowser)
- ⚠️ `DELETE /3d-models` - Delete models (needed for Phase 1)
- ⚠️ `POST /3d-models/{id}/tags` - Update tags (needed for Phase 1)
- ✅ `DELETE /gcode-files` - Delete G-code files (GcodeFileBrowser)
- ✅ `POST /gcode-files/upload-multiple` - Upload G-code (GcodeFileBrowser)

### Components Status
- ✅ GenericFileBrowser
- ✅ ModelsFileBrowser
- ✅ GcodeFileBrowser
- ✅ TreeView
- ✅ ExplorerFileBrowser
- ✅ ExplorerModelListView
- ✅ GcodeUploadModal
- ⚠️ BulkTagModal (verify exists)
- ⚠️ ModelGridView (verify exists)
- ⚠️ GcodeGridView (verify exists)
- ✅ ConfirmationModal
- ✅ FileBrowserViewModeToggle

### Test Status
- ✅ Current: 393+ tests passing, 0 errors
- ✅ Build: < 11 seconds
- ✅ ESLint: 0 violations
- ✅ TypeScript: 0 errors

---

## Success Criteria - Final

Upon completion of all phases, the following must be true:

**Functionality:**
- ✅ ModelsPage with full selection, tagging, and deletion
- ✅ GcodeLibraryPage with deletion and upload
- ✅ HarvestPage integrated or verified as separate
- ✅ Grid view components verified or created
- ✅ Feature parity between grid and explorer views

**Quality:**
- ✅ 393+ tests passing (or more if new tests added)
- ✅ 0 TypeScript errors
- ✅ 0 ESLint violations
- ✅ Build completes in < 11 seconds
- ✅ WCAG 2.2 Level AA compliance

**User Experience:**
- ✅ Keyboard shortcuts working ('t' cycles tabs)
- ✅ Bulk operations (tag, delete) work correctly
- ✅ Upload progress displays and completes successfully
- ✅ Search, sort, filter, and pagination all functional
- ✅ Error handling with user-friendly messages
- ✅ No TypeScript errors in console

---

## Rollback Plan

If issues arise during implementation:
1. Revert specific phase: `git revert <commit-hash>`
2. Fix in isolation, test, and re-commit
3. Document issue in this file under "Known Issues"

---

## Known Issues & Resolutions

*None yet - will be updated as implementation progresses.*

