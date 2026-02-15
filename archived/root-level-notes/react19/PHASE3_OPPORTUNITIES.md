# Phase 3: Advanced React 19 Patterns - Implementation Roadmap

**Current Status**: Phase 1-2 complete, ready to implement advanced patterns  
**Target**: Improve UX with optimistic updates, advanced hooks, and state preservation  
**Estimated Effort**: 4-6 components, 2-3 sprints

## Identified Phase 3 Opportunities

### 1. useOptimistic - Highest Priority

**Pattern**: Optimistic UI updates for delete/toggle operations  
**Impact**: Improved perceived performance, better UX  
**Difficulty**: Medium (requires async + rollback handling)

#### Candidate Components:

**A. TagAdminPage.tsx** (Admin → Tag Management)
- **Operation**: Delete tags from the tag list
- **Current**: Waits for server response before removing from UI
- **Improvement**: Remove immediately, rollback on error
- **Lines affected**: ~50-100 (delete button handlers)
- **Benefit**: Tag management feels instant

**B. CatalogPage.tsx** (Catalog page)
- **Operation**: Delete manufacturers/models from catalog
- **Current**: Server confirmation required for visual update
- **Improvement**: Remove from UI optimistically
- **Benefit**: Smoother admin workflows

**C. Model3DFileBrowser.tsx** (Model browser)
- **Operation**: Delete 3D model files
- **Current**: Shows loading state, then removes
- **Improvement**: Remove immediately with undo option
- **Benefit**: File management feels responsive

**D. GcodeFileBrowser.tsx** (G-code browser)
- **Operation**: Delete G-code files
- **Current**: Shows loading state, then removes
- **Improvement**: Remove immediately with rollback
- **Benefit**: Consistent with model deletion UX

**Implementation Pattern**:
```typescript
const [optimisticItems, addOptimisticUpdate] = useOptimistic(items, 
  (state, deletedId) => state.filter(i => i.id !== deletedId)
);

const handleDelete = async (id: string) => {
  addOptimisticUpdate(id); // Remove from UI immediately
  
  startTransition(async () => {
    try {
      await api.deleteItem(id);
      setItems(items.filter(i => i.id !== id)); // Confirm state
    } catch {
      // Automatic rollback via useOptimistic
    }
  });
};
```

### 2. useEffectEvent - Medium Priority

**Pattern**: Extract non-reactive event handlers from effects  
**Impact**: Cleaner dependency management, fewer accidental recomputes  
**Difficulty**: Medium (requires understanding effect dependencies)

#### Candidate Components:

**A. PrinterStatusSubscriber or WebSocket handlers**
- **Current**: useEffect depends on many props, retriggers unnecessarily
- **Improvement**: Extract event handler with useEffectEvent
- **Benefit**: Connection stays alive across prop changes

**B. SignalR connection setup** (if present in hooks)
- **Issue**: Effect retriggers when unrelated props change
- **Fix**: Use useEffectEvent for onMessage handler
- **Benefit**: Stable connection, fewer reconnects

**C. Event listeners** (keyboard, window resize, etc.)
- **Current**: Recreate listeners when dependencies change
- **Improvement**: Extract with useEffectEvent
- **Benefit**: Fewer listener registrations

### 3. Activity Component - Lower Priority

**Pattern**: Preserve component state when hidden (tabs, wizards)  
**Impact**: Better UX for multi-step flows  
**Difficulty**: Low-Medium (straightforward replacement)

#### Candidate Components:

**A. JobDetailsModal.tsx** (if it has tabs)
- **Current**: Tab content might re-initialize on switch
- **Improvement**: Wrap each tab in Activity component
- **Benefit**: User input preserved when switching tabs

**B. SetupWizard.tsx** (Setup wizard)
- **Current**: Multi-step form, user data could be lost on step switch
- **Improvement**: Wrap each step in Activity
- **Benefit**: Smoother wizard experience

**C. Multi-tab Admin Pages**
- **Identify**: Any page with tabs (settings, configuration, etc.)
- **Improvement**: Use Activity for each tab
- **Benefit**: Consistent UX, preserved state

**Implementation Pattern**:
```typescript
const [activeTab, setActiveTab] = useState('overview');

<Activity mode={activeTab === 'overview' ? 'visible' : 'hidden'}>
  <OverviewTab />
</Activity>

<Activity mode={activeTab === 'settings' ? 'visible' : 'hidden'}>
  <SettingsTab />
</Activity>
```

## Implementation Roadmap

### Sprint 1: useOptimistic in Tag Management & File Browsers
1. **TagAdminPage.tsx** - Implement optimistic tag deletion
2. **Model3DFileBrowser.tsx** - Implement optimistic file deletion
3. **GcodeFileBrowser.tsx** - Implement optimistic file deletion
4. Tests: Verify optimistic updates and rollback behavior
5. Verification: Build, tests, lint, manual testing

### Sprint 2: useOptimistic in Catalog & useEffectEvent
1. **CatalogPage.tsx** - Implement optimistic deletions
2. **WebSocket/SignalR handlers** - Apply useEffectEvent pattern
3. Tests: Verify connection stability and handler cleanup
4. Verification: Build, tests, lint

### Sprint 3: Activity Component & Polish
1. **JobDetailsModal.tsx** - Add Activity to tab panels
2. **SetupWizard.tsx** - Add Activity to wizard steps
3. Polish: Review all optimistic updates for edge cases
4. Documentation: Update CONTRIBUTING.md with real examples
5. Verification: Full test suite, manual testing

## Success Criteria

**For useOptimistic implementations**:
- ✅ Items removed immediately from UI
- ✅ Proper rollback on error (item reappears)
- ✅ Loading indicator shown during async operation
- ✅ No duplicate items after optimistic update
- ✅ All tests pass

**For useEffectEvent implementations**:
- ✅ Effect dependencies cleaned up
- ✅ No unnecessary recomputes/reconnects
- ✅ Event handlers properly bound
- ✅ Cleanup functions still execute

**For Activity implementations**:
- ✅ Component state preserved when hidden
- ✅ No re-initialization on tab switch
- ✅ User input preserved across tabs
- ✅ Smooth transitions between states

## Notes

- **useOptimistic** has the highest impact - prioritize this first
- **useEffectEvent** requires careful dependency analysis - audit effects first
- **Activity** is straightforward once patterns are identified
- **cacheSignal** deferred - primarily for Server Components (future consideration)
- Keep documentation updated with real code examples from actual implementations
- Consider adding integration tests for optimistic update scenarios

