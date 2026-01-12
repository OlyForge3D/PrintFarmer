# 🚀 PrintFarmer Professional UI Enhancement Roadmap

A feature-driven plan to implement world-class UX enhancements across all pages, prioritizing non-admin user features first.

---

## Overview

This roadmap organizes enhancements by **feature** rather than phase, identifying all pages that benefit from each feature, then implementing based on **non-admin user priority**.

**Key Principle:** Non-admin users (Printers, Print Queue, Files, Spools, Jobs) are prioritized over admin pages (Settings, Logs, Observability, etc.)

**Total Estimated Effort:** 40-60 hours
**Recommended Pace:** 4-6 weeks (5-6 hour sprints)

---

## Application Pages Inventory

### Non-Admin Pages (User-Facing)
- **Dashboard** - Home/overview
- **Printers** - Printer status & management  
- **Files** - Models, G-Code, Harvest tabs
- **Slice** - Job creation/slicing
- **Print Queue** - Active/queued jobs
- **Spools** - Filament management
- **Spoolman Integration** - External spool sync

### Admin Pages (Less Frequent)
- **Locations** - Site management
- **Catalog** - Manufacturers/models
- **User Accounts** - Team management
- **Tags** - Tag management
- **Workers** - OrcaSlicer/background workers
- **Slicer Profiles** - Profile management
- **Logs** - System logs
- **Observability** - Metrics/monitoring
- **File Health** - Database health
- **Settings** - System configuration

---

## Feature 1: Infinite Scroll & Smart Pagination

### Pages That Benefit
1. **Files - Models tab** (HIGH) - Users have 100+ 3D models
2. **Files - G-Code tab** (HIGH) - Users accumulate 100+ sliced files
3. **Files - Harvest tab** (HIGH) - Print history accumulates
4. **Print Queue** (MEDIUM) - Past/completed jobs queue
5. **Spools** (MEDIUM) - Users manage multiple filament reels
6. **Catalog - Models** (MEDIUM-LOW) - Manufacturers have 50+ models
7. **Logs** (LOW) - Admin viewing application logs
8. **Slicer Profiles** (MEDIUM-LOW) - Hundreds of profiles per manufacturer
9. **User Accounts** (LOW) - Only relevant for large teams

### Implementation Strategy

```typescript
// Reusable infinite scroll hook
export function useInfiniteList<T>(
  queryKey: string[],
  fetchFn: (pageParam?: string) => Promise<PaginatedResponse<T>>,
) {
  return useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam }) => fetchFn(pageParam),
    getNextPageParam: (lastPage) => lastPage.nextCursor,
    initialPageParam: undefined,
  });
}
```

### Per-Page Implementation

#### Files - Models Tab
**Current State:** Grid loads all models at once  
**Enhancement:** Scroll to load next batch
```typescript
const { data, hasNextPage, fetchNextPage } = useInfiniteList(
  ['files', 'models'],
  (pageParam) => fileService.getModels({ limit: 20, cursor: pageParam })
);

// Render with InfiniteScroll component
<InfiniteScroll
  dataLength={items.length}
  next={fetchNextPage}
  hasMore={hasNextPage}
  loader={<ModelSkeleton />}
>
  <ModelGrid items={items} />
</InfiniteScroll>
```
**Effort:** 3-4 hours  
**UI Impact:** ⭐⭐⭐⭐⭐ (Most used page)

#### Files - G-Code Tab
Similar to Models tab, but with G-code-specific metadata  
**Effort:** 3-4 hours  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Files - Harvest Tab
Print history with pagination  
**Effort:** 2-3 hours (builds on Models/GCode pattern)  
**UI Impact:** ⭐⭐⭐⭐

#### Print Queue
Display past/completed jobs with infinite scroll  
**Effort:** 3-4 hours  
**UI Impact:** ⭐⭐⭐⭐

#### Spools
Filament inventory with infinite scroll  
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐

#### Catalog - Models
Manufacturer model listings  
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐

#### Slicer Profiles
Profile listings by manufacturer  
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐

---

**Feature 1 Total Effort:** 17-26 hours  
**Implementation Order:** Models → GCode → Harvest → Print Queue → Spools → (Catalog/Profiles) → Logs

---

## Feature 2: Upload Progress & Form Status

### Pages That Benefit
1. **Files - Models tab** (HIGH) - Upload 3D models (can be 50MB+)
2. **Files - G-Code tab** (HIGH) - Upload G-code files
3. **Files - Harvest tab** (MEDIUM) - Upload print photos
4. **Spools** (MEDIUM) - Update spool properties
5. **Catalog** (LOW) - Upload manufacturer data
6. **Settings** (LOW) - Configuration uploads

### Implementation Pattern

```typescript
// Server Action for file uploads
async function uploadModel(formData: FormData) {
  'use server';
  const file = formData.get('file') as File;
  return await fileService.uploadModel(file);
}

function ModelUploadForm() {
  const [state, formAction] = useActionState(uploadModel, {});
  const { pending } = useFormStatus();
  
  return (
    <form action={formAction}>
      <input name="file" type="file" accept=".stl,.obj,.3mf" required />
      <Button disabled={pending}>
        {pending ? (
          <>
            <Spinner className="w-4 h-4 animate-spin" />
            Uploading ({state.progress}%)...
          </>
        ) : (
          'Upload Model'
        )}
      </Button>
    </form>
  );
}
```

### Per-Page Implementation

#### Files - Models Upload
**Effort:** 4-5 hours  
**UI Impact:** ⭐⭐⭐⭐⭐ (Users upload large files)

#### Files - G-Code Upload
**Effort:** 3-4 hours (reuse Model pattern)  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Spools Management
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐

#### Harvest Photo Upload
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐

---

**Feature 2 Total Effort:** 11-15 hours  
**Implementation Order:** Models → G-Code → Spools → Harvest → Settings

---

## Feature 3: Optimistic Updates

### Pages That Benefit
1. **Files - Models tab** (HIGH) - Tag, favorite, delete operations
2. **Files - G-Code tab** (HIGH) - Tag, link to printer operations
3. **Print Queue** (HIGH) - Cancel, retry operations
4. **Spools** (HIGH) - Update spool data
5. **Files - Harvest tab** (MEDIUM) - Tag photos
6. **Printer Details** (MEDIUM) - Update printer properties
7. **User Accounts** (LOW) - Update user info
8. **Tags** (LOW) - Create/delete tags

### Implementation Pattern

```typescript
function FileTagging({ file }: { file: GcodeFile }) {
  const [tags, setTags] = useState(file.tags);
  const [optimisticTags, addOptimisticTag] = useOptimistic(tags, 
    (state, newTag: Tag) => [...state, newTag]
  );
  
  const handleAddTag = async (tag: Tag) => {
    addOptimisticTag(tag);
    try {
      await fileService.addTag(file.id, tag.id);
      setTags(prev => [...prev, tag]);
    } catch (error) {
      // Reverts automatically on error
      console.error('Failed to add tag');
    }
  };
  
  return (
    <TagContainer>
      {optimisticTags.map(tag => (
        <Badge key={tag.id} className="animate-pulse">
          {tag.name}
        </Badge>
      ))}
      <Button onClick={() => handleAddTag(newTag)}>Add Tag</Button>
    </TagContainer>
  );
}
```

### Per-Page Implementation

#### Files - Models (Tag, Favorite, Delete)
**Effort:** 3-4 hours  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Files - G-Code (Tag, Link Printer, Delete)
**Effort:** 3-4 hours  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Print Queue (Cancel, Retry, Update Status)
**Effort:** 3-4 hours  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Spools (Update Quantity, Properties)
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐⭐

#### Files - Harvest (Tag Photos)
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐

#### Printer Details (Update Settings)
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐

---

**Feature 3 Total Effort:** 15-21 hours  
**Implementation Order:** Models → G-Code → Print Queue → Spools → Harvest → Printers

---

## Feature 4: Floating Action Buttons (FABs)

### Pages That Benefit
1. **Files - Models tab** (HIGH) - Upload new model
2. **Files - G-Code tab** (HIGH) - Upload new G-code
3. **Print Queue** (MEDIUM) - Create new job (FAB → Slice page)
4. **Spools** (MEDIUM) - Add new spool
5. **Files - Harvest tab** (MEDIUM) - Add photo
6. **User Accounts** (MEDIUM) - Add new user (admin)
7. **Locations** (MEDIUM) - Add location (admin)
8. **Tags** (LOW) - Create tag (admin)

### Reusable FAB Component

```typescript
interface FloatingActionButtonProps {
  icon: React.ComponentType<{ className?: string }>;
  onClick: () => void;
  label: string;
  position?: 'bottom-right' | 'bottom-center' | 'bottom-left';
  variant?: 'primary' | 'secondary';
}

export function FloatingActionButton({
  icon: Icon,
  onClick,
  label,
  position = 'bottom-right',
  variant = 'primary',
}: FloatingActionButtonProps) {
  return (
    <button
      onClick={onClick}
      aria-label={label}
      className={`
        fixed rounded-full p-4 shadow-lg hover:shadow-xl transition-all
        focus:outline-none focus:ring-2
        ${variant === 'primary' ? 'bg-pf-accent text-white' : 'bg-pf-bg-2'}
        ${position === 'bottom-right' && 'bottom-6 right-6'}
        ${position === 'bottom-center' && 'bottom-6 left-1/2 -translate-x-1/2'}
        ${position === 'bottom-left' && 'bottom-6 left-6'}
      `}
    >
      <Icon className="w-6 h-6" />
    </button>
  );
}
```

### Per-Page Implementation

#### Files - Models Tab
**Effort:** 1-2 hours (create FAB component + integrate)  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Files - G-Code Tab
**Effort:** 1 hour (reuse component)  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Print Queue (Link to Slice)
**Effort:** 1-2 hours  
**UI Impact:** ⭐⭐⭐⭐

#### Spools
**Effort:** 1 hour  
**UI Impact:** ⭐⭐⭐

#### Other Pages
**Effort:** 3-4 hours total for remaining pages  
**UI Impact:** ⭐⭐-⭐⭐⭐

---

**Feature 4 Total Effort:** 8-11 hours  
**Implementation Order:** Create FAB component → Files pages → Print Queue → Spools → Admin pages

---

## Feature 5: Context Menus (Right-Click Operations)

### Pages That Benefit
1. **Files - Models tab** (HIGH) - Edit, tag, delete, download
2. **Files - G-Code tab** (HIGH) - Edit, link printer, delete, download
3. **Print Queue** (MEDIUM) - Cancel, pause, view details, restart
4. **Spools** (MEDIUM) - View details, edit, delete
5. **Files - Harvest tab** (MEDIUM) - View full image, tag, delete
6. **Printers** (MEDIUM) - SSH, restart, unlock
7. **Catalog** (LOW) - View details, edit (admin)
8. **User Accounts** (LOW) - Edit, deactivate, delete (admin)

### Reusable Context Menu Component

```typescript
interface ContextMenuProps {
  x: number;
  y: number;
  items: ContextMenuItem[];
  onClose: () => void;
}

interface ContextMenuItem {
  label: string;
  icon?: React.ComponentType<{ className?: string }>;
  onClick: () => void;
  variant?: 'default' | 'danger';
  divider?: boolean;
}

export function ContextMenu({ x, y, items, onClose }: ContextMenuProps) {
  const menuRef = useRef<HTMLDivElement>(null);
  
  useEffect(() => {
    const handleClickOutside = (e: MouseEvent) => {
      if (!menuRef.current?.contains(e.target as Node)) onClose();
    };
    
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [onClose]);
  
  return (
    <div
      ref={menuRef}
      className="fixed bg-pf-bg-2 border border-pf-border rounded-lg shadow-lg z-50"
      style={{ top: y, left: x }}
    >
      {items.map((item, i) => (
        <div key={i}>
          {item.divider && <Divider />}
          <button
            onClick={() => { item.onClick(); onClose(); }}
            className={`w-full text-left px-4 py-2 hover:bg-pf-bg-3 ${
              item.variant === 'danger' ? 'text-red-500' : ''
            }`}
          >
            {item.icon && <item.icon className="w-4 h-4 mr-2 inline" />}
            {item.label}
          </button>
        </div>
      ))}
    </div>
  );
}
```

### Per-Page Implementation

#### Files - Models Tab
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Files - G-Code Tab
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Print Queue
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐⭐

#### Spools
**Effort:** 1-2 hours  
**UI Impact:** ⭐⭐⭐

#### Remaining Pages
**Effort:** 4-5 hours  
**UI Impact:** ⭐⭐-⭐⭐⭐

---

**Feature 5 Total Effort:** 11-16 hours  
**Implementation Order:** Create component → Files pages → Print Queue → Spools → Printers → Admin pages

---

## Feature 6: Keyboard Navigation & Accessibility

### Pages That Benefit (ALL non-admin pages)
1. **Files** (HIGH) - Grid navigation, arrow keys, Enter to open
2. **Print Queue** (HIGH) - List navigation, keyboard shortcuts
3. **Spools** (HIGH) - Inventory management
4. **Printers** (HIGH) - Status overview
5. **Dashboard** (MEDIUM) - Overview scrolling
6. **Slice Page** (MEDIUM) - Form field navigation
7. **All Admin Pages** (MEDIUM) - Consistent accessibility

### Implementation Pattern

```typescript
// Generic keyboard navigation hook
export function useKeyboardNavigation<T>(
  items: T[],
  onSelect: (item: T, index: number) => void,
  options?: {
    columns?: number;
    onEscapeKey?: () => void;
  }
) {
  const [selectedIndex, setSelectedIndex] = useState(0);
  
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      switch (e.key) {
        case 'ArrowDown':
          setSelectedIndex(i => Math.min(i + 1, items.length - 1));
          break;
        case 'ArrowUp':
          setSelectedIndex(i => Math.max(i - 1, 0));
          break;
        case 'ArrowRight':
          if (options?.columns)
            setSelectedIndex(i => Math.min(i + options.columns, items.length - 1));
          break;
        case 'ArrowLeft':
          if (options?.columns)
            setSelectedIndex(i => Math.max(i - options.columns, 0));
          break;
        case 'Enter':
          onSelect(items[selectedIndex], selectedIndex);
          break;
        case 'Escape':
          options?.onEscapeKey?.();
          break;
      }
    };
    
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [items, selectedIndex, onSelect, options]);
  
  return selectedIndex;
}

// Keyboard shortcuts
const shortcuts = {
  models: {
    'ctrl+u': 'Upload model',
    'ctrl+d': 'Delete selected',
    'ctrl+t': 'Tag selected',
  },
  queue: {
    'ctrl+n': 'New print job',
    'ctrl+c': 'Cancel job',
    'ctrl+p': 'Pause job',
  },
};
```

### Enhancement Details

**Files - All tabs:**
- Arrow keys navigate grid (left/right/up/down)
- Enter opens file details
- Ctrl+U: Upload modal
- Ctrl+D: Delete
- Ctrl+T: Tag modal
- Ctrl+F: Search/filter

**Print Queue:**
- Arrow keys navigate list
- Enter views job details
- Ctrl+N: New job
- Ctrl+C: Cancel
- Ctrl+P: Pause/Resume

**Spools:**
- Arrow keys navigate inventory
- Enter edits spool
- Ctrl+A: Add spool
- Ctrl+D: Delete

**Focus Management:**
- Tab order follows visual order
- Focus indicators visible (ring)
- Modal focus trap (Escape closes)
- Return focus after modal closes

---

**Feature 6 Total Effort:** 12-16 hours  
**Implementation Order:** Create keyboard hook → Files → Print Queue → Spools → Printers → Remaining pages

---

## Feature 7: Master-Detail Layout (Sidebar Toggle)

### Pages That Benefit
1. **Files - Models tab** (HIGH) - Select model → view details
2. **Files - G-Code tab** (HIGH) - Select code → view metadata
3. **Print Queue** (MEDIUM) - Select job → view details
4. **Printers** (MEDIUM) - Select printer → view full details
5. **Catalog** (MEDIUM) - Select model → view specs
6. **Spools** (MEDIUM) - Select spool → view usage

### Pattern: Responsive Master-Detail

```typescript
function ModelsWithDetails() {
  const [selected, setSelected] = useState<Model | null>(null);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const isDesktop = useMediaQuery('(min-width: 1024px)');
  
  return (
    <div className="flex h-full">
      {/* List - Always visible on desktop, hidden on mobile unless open */}
      {(isDesktop || sidebarOpen) && (
        <ModelsList
          selected={selected}
          onSelect={setSelected}
          className="w-80 border-r border-pf-border overflow-y-auto"
        />
      )}
      
      {/* Details - Full width on mobile, flex-1 on desktop */}
      <div className="flex-1 overflow-hidden flex flex-col">
        {!isDesktop && selected && (
          <div className="flex items-center gap-2 p-4 border-b border-pf-border">
            <Button
              variant="subtle"
              size="sm"
              onClick={() => setSidebarOpen(true)}
            >
              <ChevronLeftIcon className="w-4 h-4" />
            </Button>
            <span className="text-sm font-medium flex-1 truncate">
              {selected.name}
            </span>
          </div>
        )}
        
        {selected ? (
          <ModelDetails model={selected} />
        ) : (
          <EmptyState message="Select a model to view details" />
        )}
      </div>
    </div>
  );
}
```

### Per-Page Implementation

#### Files - Models Tab
**Effort:** 3-4 hours  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Files - G-Code Tab
**Effort:** 3-4 hours (reuse pattern)  
**UI Impact:** ⭐⭐⭐⭐⭐

#### Print Queue
**Effort:** 3-4 hours  
**UI Impact:** ⭐⭐⭐⭐

#### Printers Page
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐⭐

#### Catalog
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐

#### Spools
**Effort:** 2-3 hours  
**UI Impact:** ⭐⭐⭐

---

**Feature 7 Total Effort:** 15-21 hours  
**Implementation Order:** Files (Models/GCode) → Print Queue → Printers → Catalog → Spools

---

## Feature 8: Breadcrumbs & Navigation

### Pages That Benefit
1. **Files** (HIGH) - Show folder hierarchy
2. **Catalog** (MEDIUM) - Manufacturer → Model hierarchy
3. **Admin sections** (LOW) - Section navigation

### Pattern

```typescript
interface BreadcrumbItem {
  label: string;
  href?: string;
  current?: boolean;
}

function Breadcrumbs({ items }: { items: BreadcrumbItem[] }) {
  return (
    <nav aria-label="Breadcrumb" className="flex items-center gap-2 text-sm">
      {items.map((item, i) => (
        <div key={i} className="flex items-center gap-2">
          {i > 0 && <ChevronRightIcon className="w-4 h-4 text-pf-text-secondary" />}
          {item.href ? (
            <Link href={item.href} className="hover:text-pf-accent">
              {item.label}
            </Link>
          ) : (
            <span className={item.current ? 'font-medium' : 'text-pf-text-secondary'}>
              {item.label}
            </span>
          )}
        </div>
      ))}
    </nav>
  );
}
```

---

**Feature 8 Total Effort:** 4-6 hours  
**Implementation Order:** Files → Catalog → Admin sections

---

## Feature 9: React 19 Modern Patterns

### Patterns to Implement
1. **use() hook** - Replace prop drilling with promise handling
2. **useOptimistic** - Already covered in Feature 3
3. **useFormStatus** - Already covered in Feature 2
4. **useActionState** - Form state management
5. **Ref as prop** - Remove forwardRef usage
6. **Context without Provider** - Simplify context patterns
7. **Activity component** - Preserve tab state (already done!)

### Scope

**use() Hook Migration:**
- File detail loading
- Printer status fetching
- Job details

**useActionState Migration:**
- File uploads
- Form submissions
- Printer configuration

---

**Feature 9 Total Effort:** 8-12 hours  
**Priority:** 🟢 LOW (Technical, not user-facing)

---

## Implementation Priority Matrix

```
╔════════════════════════════════════════════════════════════════╗
║ NON-ADMIN PAGES (Priority 1)                                  ║
║ ├─ Printers                                                    ║
║ ├─ Files (Models, G-Code, Harvest)                            ║
║ ├─ Print Queue                                                 ║
║ └─ Spools                                                      ║
║                                                                ║
║ USER-FACING PAGES (Priority 2)                                ║
║ ├─ Dashboard                                                   ║
║ ├─ Slice Page                                                  ║
║ ├─ Spoolman Integration                                        ║
║ └─ Printer Details                                             ║
║                                                                ║
║ ADMIN PAGES (Priority 3)                                      ║
║ ├─ Catalog                                                     ║
║ ├─ Locations                                                   ║
║ ├─ User Accounts                                               ║
║ ├─ Tags                                                        ║
║ ├─ Workers                                                     ║
║ ├─ Slicer Profiles                                             ║
║ ├─ Logs                                                        ║
║ ├─ Observability                                               ║
║ ├─ File Health                                                 ║
║ └─ Settings                                                    ║
╚════════════════════════════════════════════════════════════════╝
```

---

## Recommended Implementation Order

### Sprint 1: Foundation (Week 1 - 6 hours)
1. **Feature 2** - Upload Progress (useFormStatus) - **4 hours**
   - Implement across Models, G-Code tabs
   - Reusable upload component
2. **Feature 4** - Create FAB Component - **2 hours**
   - Generic FloatingActionButton
   - Integrate into Files pages

### Sprint 2: Data & Navigation (Week 2 - 6 hours)
1. **Feature 1** - Infinite Scroll for Files - **4-5 hours**
   - Models tab (implement full pattern)
   - G-Code tab (reuse pattern)
   - Harvest tab (reuse pattern)
2. **Feature 8** - Breadcrumbs - **1-2 hours**
   - Files breadcrumb component

### Sprint 3: Interactivity (Week 3 - 6 hours)
1. **Feature 3** - Optimistic Updates - **3-4 hours**
   - Files operations (tag, delete, favorite)
   - Print Queue operations (cancel, retry)
2. **Feature 5** - Context Menus - **2-3 hours**
   - Implement ContextMenu component
   - Files pages
   - Print Queue

### Sprint 4: Navigation Polish (Week 4 - 6 hours)
1. **Feature 6** - Keyboard Navigation - **3-4 hours**
   - Files grid navigation
   - Print Queue list navigation
   - Keyboard shortcuts
2. **Feature 7** - Master-Detail - **2-3 hours**
   - Files - Models responsive layout
   - Verify mobile responsiveness

### Sprint 5: Extend Features (Week 5 - 6 hours)
1. **Feature 1** - Infinite Scroll for remaining pages - **2-3 hours**
   - Print Queue
   - Spools
   - Catalog
2. **Feature 3** - Optimistic Updates for remaining - **2 hours**
   - Spools
   - Harvest
3. **Feature 7** - Master-Detail for remaining - **1-2 hours**
   - Print Queue
   - Printers

### Sprint 6: Admin Features (Week 6 - 6 hours)
1. **Feature 6** - Complete keyboard nav (all pages) - **2 hours**
2. **Feature 5** - Context menus (admin pages) - **2 hours**
3. **Feature 7** - Master-detail (admin pages) - **2 hours**

### Sprint 7: Polish & React 19 (Week 7 - Optional, 6 hours)
1. **Feature 9** - React 19 Patterns - **4-5 hours**
   - Migrate to use() hook
   - useActionState for forms
2. **Testing & Accessibility** - **1-2 hours**

---

## Estimated Total Effort by Feature

| Feature | Effort | Priority | Impact |
|---------|--------|----------|--------|
| 1: Infinite Scroll | 17-26h | 🔴 HIGH | ⭐⭐⭐⭐⭐ |
| 2: Upload Progress | 11-15h | 🔴 HIGH | ⭐⭐⭐⭐⭐ |
| 3: Optimistic Updates | 15-21h | 🔴 HIGH | ⭐⭐⭐⭐⭐ |
| 4: FABs | 8-11h | 🟡 MEDIUM | ⭐⭐⭐⭐ |
| 5: Context Menus | 11-16h | 🟡 MEDIUM | ⭐⭐⭐⭐ |
| 6: Keyboard Nav | 12-16h | 🔴 HIGH | ⭐⭐⭐⭐⭐ |
| 7: Master-Detail | 15-21h | 🟡 MEDIUM | ⭐⭐⭐⭐ |
| 8: Breadcrumbs | 4-6h | 🟡 MEDIUM | ⭐⭐⭐ |
| 9: React 19 Patterns | 8-12h | 🟢 LOW | ⭐⭐ |
| | **Total: 101-144h** | | |

### Prioritized Effort (Non-Admin First)

**Phase A: Non-Admin Critical Features (40-50 hours)**
- Feature 2: Upload (Files)
- Feature 1: Infinite scroll (Files, Queue, Spools)
- Feature 3: Optimistic (Files, Queue)
- Feature 6: Keyboard nav (Files, Queue)
- Feature 4: FABs (Files, Queue)

**Phase B: Navigation & Polish (35-45 hours)**
- Feature 5: Context menus
- Feature 7: Master-detail
- Feature 8: Breadcrumbs
- Admin feature coverage

**Phase C: Technical Debt (8-12 hours)**
- Feature 9: React 19 patterns

---

## Getting Started Checklist

### Before Sprint 1
- [ ] Read this entire document
- [ ] Review code examples
- [ ] Identify all reusable components to create
- [ ] Check current Tailwind config for responsive utilities

### Sprint 1 Kickoff
- [ ] Create useInfiniteList hook (reusable)
- [ ] Create FloatingActionButton component
- [ ] Implement useFormStatus in Models upload
- [ ] Implement useFormStatus in G-Code upload
- [ ] Test upload flow on slow network

### Sprint 2 Kickoff
- [ ] Implement infinite scroll for Models tab
- [ ] Implement infinite scroll for G-Code tab
- [ ] Implement infinite scroll for Harvest tab
- [ ] Create Breadcrumb component
- [ ] Add breadcrumbs to FilesPage

---

## Success Metrics

After completing all sprints:
- ✅ Files page handles 100+ items smoothly
- ✅ All uploads show progress feedback
- ✅ Tag/delete operations feel instant (optimistic)
- ✅ Keyboard power users can navigate entirely with arrow keys
- ✅ Mobile users have responsive master-detail layouts
- ✅ Context menus provide fast access to operations
- ✅ Application feels professional and responsive

---

## Resources

- [React 19 Documentation](https://react.dev)
- [React Query Infinite Queries](https://tanstack.com/query/latest/docs/react/guides/infinite-queries)
- [Web Accessibility (WCAG 2.2)](https://www.w3.org/WAI/WCAG22/quickref/)
- [MDN: Keyboard Accessible Components](https://developer.mozilla.org/en-US/docs/Web/Accessibility/Keyboard-navigable_custom_components)
- [Tailwind Responsive Design](https://tailwindcss.com/docs/responsive-design)

