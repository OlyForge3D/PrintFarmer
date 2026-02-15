# Harvest Table Column Customization - Implementation Plan

**Feature**: User-configurable column visibility and ordering for the Indexed Files table in Harvest Operation Details.

**Priority**: Medium  
**Estimated Effort**: 4-18 hours (depending on approach)  
**Status**: Planning

---

## Overview

Currently, the Indexed Files table in the Harvest Operation Details page displays a fixed set of columns in a predefined order. This enhancement will allow users to:

1. Show/hide specific columns
2. Reorder columns to match their workflow preferences
3. Save preferences for future sessions

---

## Current Table Structure

### Existing Columns
| Column | Type | Required | Current Visibility |
|--------|------|----------|-------------------|
| Checkbox | Input | Yes | Always visible |
| File | Text + Thumbnail | Yes | Always visible |
| Size | Number | No | Always visible |
| Slicer | Text | No | Always visible |
| Material | Text | No | Always visible |
| Nozzle | Number | No | Always visible |
| Print Time | Number | No | Always visible |
| Filament | Number | No | Always visible |
| Status | Badge | No | Always visible |
| Error | Text + Actions | No | Always visible |
| Modified | DateTime | No | Always visible |

**Location**: `/src/Web/ReactApp/src/components/harvest/IndexedFilesList.tsx`

---

## Proposed Implementation Approaches

### Approach 1: Preset Layouts (Quick Win)
**Effort**: 4-6 hours  
**Complexity**: Low

#### Features
- 3 predefined column layouts:
  - **Compact**: Checkbox, File, Size, Status
  - **Standard** (default): Checkbox, File, Size, Slicer, Material, Status, Error
  - **Detailed**: All columns
- Dropdown selector in table header
- LocalStorage persistence

#### Benefits
- Fast to implement
- Easy to use
- No complex UI needed
- Covers 80% of use cases

#### Implementation Steps
1. Define preset configurations (1 hour)
2. Add layout selector dropdown (1 hour)
3. Implement dynamic rendering (2 hours)
4. Add LocalStorage persistence (1 hour)
5. Testing and polish (1 hour)

---

### Approach 2: Full Column Customization (Complete Solution)
**Effort**: 12-18 hours  
**Complexity**: Medium-High

#### Features
- Individual column show/hide toggles
- Drag-and-drop column reordering
- Custom presets (save user layouts)
- LocalStorage or backend persistence
- Column width adjustment (optional)

#### Benefits
- Maximum flexibility
- Professional UX
- Scalable to future columns
- Exportable/shareable configurations

#### Implementation Steps

##### 1. Data Structure Design (2 hours)
```typescript
interface ColumnConfig {
  id: string;
  label: string;
  required: boolean;
  defaultVisible: boolean;
  defaultWidth?: number;
  render: (file: DiscoveredGcodeFileDto) => React.ReactNode;
  headerAlign?: 'left' | 'center' | 'right';
  cellAlign?: 'left' | 'center' | 'right';
}

interface UserColumnPreferences {
  visibleColumns: string[];
  columnOrder: string[];
  columnWidths?: Record<string, number>;
  selectedPreset?: string;
}
```

##### 2. Column Configuration Array (2 hours)
Create comprehensive column definitions with render functions for each column type.

##### 3. Settings UI Component (3-4 hours)
- Modal or sidebar for column settings
- Checkboxes for visibility
- Drag-and-drop list for ordering
- Preview of table layout
- Reset to defaults button

##### 4. Drag-and-Drop Implementation (3-4 hours)
**Recommended Library**: `@dnd-kit/core` or `react-beautiful-dnd`

```bash
npm install @dnd-kit/core @dnd-kit/sortable
```

Features:
- Drag handles for each column in settings
- Visual feedback during drag
- Snap-to-position ordering

##### 5. Dynamic Table Rendering (2-3 hours)
Refactor table to render based on configuration:

```typescript
// Header rendering
{columnOrder
  .filter(colId => visibleColumns.includes(colId))
  .map(colId => {
    const col = COLUMN_DEFINITIONS.find(c => c.id === colId);
    return <th key={colId} className={col.headerAlign}>{col.label}</th>;
  })}

// Body rendering
{columnOrder
  .filter(colId => visibleColumns.includes(colId))
  .map(colId => {
    const col = COLUMN_DEFINITIONS.find(c => c.id === colId);
    return <td key={colId} className={col.cellAlign}>{col.render(file)}</td>;
  })}
```

##### 6. Persistence Layer (2 hours)

**Option A: LocalStorage** (Simple)
```typescript
const STORAGE_KEY = 'harvest-table-preferences';

const savePreferences = (prefs: UserColumnPreferences) => {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(prefs));
};

const loadPreferences = (): UserColumnPreferences | null => {
  const saved = localStorage.getItem(STORAGE_KEY);
  return saved ? JSON.parse(saved) : null;
};
```

**Option B: Backend API** (Robust)
- Create user settings table
- API endpoints: `GET/PUT /api/users/me/settings/harvest-columns`
- Sync across devices
- Per-tenant/per-user settings

##### 7. Testing & Polish (2-3 hours)
- Test all column combinations
- Verify responsive behavior
- Test persistence
- Edge cases (all columns hidden, etc.)

---

## Technical Implementation Details

### State Management

```typescript
// Component state
const [visibleColumns, setVisibleColumns] = useState<string[]>([]);
const [columnOrder, setColumnOrder] = useState<string[]>([]);
const [showSettings, setShowSettings] = useState(false);

// Load preferences on mount
useEffect(() => {
  const saved = loadPreferences();
  if (saved) {
    setVisibleColumns(saved.visibleColumns);
    setColumnOrder(saved.columnOrder);
  } else {
    // Use defaults
    setVisibleColumns(DEFAULT_VISIBLE_COLUMNS);
    setColumnOrder(DEFAULT_COLUMN_ORDER);
  }
}, []);

// Save on change
useEffect(() => {
  savePreferences({ visibleColumns, columnOrder });
}, [visibleColumns, columnOrder]);
```

### Column Definitions Structure

```typescript
const COLUMN_DEFINITIONS: ColumnConfig[] = [
  {
    id: 'checkbox',
    label: '',
    required: true,
    defaultVisible: true,
    render: (file) => (
      <input 
        type="checkbox" 
        checked={selected.has(file.id)} 
        onChange={() => toggleSelect(file.id)} 
      />
    ),
    cellAlign: 'center'
  },
  {
    id: 'file',
    label: 'File',
    required: true,
    defaultVisible: true,
    render: (file) => (
      <div className="flex items-center gap-2">
        {file.thumbnailUrl && <img src={file.thumbnailUrl} ... />}
        <span>{file.fileName}</span>
      </div>
    ),
    headerAlign: 'left'
  },
  {
    id: 'size',
    label: 'Size',
    required: false,
    defaultVisible: true,
    render: (file) => (
      <span>{(file.fileSizeBytes / 1024).toFixed(1)} KB</span>
    ),
    headerAlign: 'right',
    cellAlign: 'right'
  },
  // ... additional columns
];
```

### Settings UI Component

```typescript
interface ColumnSettingsProps {
  columns: ColumnConfig[];
  visibleColumns: string[];
  columnOrder: string[];
  onVisibilityChange: (columns: string[]) => void;
  onOrderChange: (order: string[]) => void;
  onClose: () => void;
}

const ColumnSettings: React.FC<ColumnSettingsProps> = ({
  columns,
  visibleColumns,
  columnOrder,
  onVisibilityChange,
  onOrderChange,
  onClose
}) => {
  return (
    <div className="modal">
      <div className="modal-header">
        <h3>Customize Columns</h3>
        <button onClick={onClose}>×</button>
      </div>
      
      <div className="modal-body">
        {/* Visibility Toggles */}
        <section>
          <h4>Visible Columns</h4>
          {columns.map(col => (
            <label key={col.id}>
              <input
                type="checkbox"
                disabled={col.required}
                checked={visibleColumns.includes(col.id)}
                onChange={(e) => {
                  if (e.target.checked) {
                    onVisibilityChange([...visibleColumns, col.id]);
                  } else {
                    onVisibilityChange(visibleColumns.filter(id => id !== col.id));
                  }
                }}
              />
              {col.label || col.id}
            </label>
          ))}
        </section>

        {/* Drag-and-Drop Ordering */}
        <section>
          <h4>Column Order</h4>
          <DragDropContext onDragEnd={handleDragEnd}>
            <Droppable droppableId="columns">
              {(provided) => (
                <div {...provided.droppableProps} ref={provided.innerRef}>
                  {columnOrder.map((colId, index) => (
                    <Draggable key={colId} draggableId={colId} index={index}>
                      {(provided) => (
                        <div
                          ref={provided.innerRef}
                          {...provided.draggableProps}
                          {...provided.dragHandleProps}
                        >
                          ☰ {columns.find(c => c.id === colId)?.label}
                        </div>
                      )}
                    </Draggable>
                  ))}
                  {provided.placeholder}
                </div>
              )}
            </Droppable>
          </DragDropContext>
        </section>
      </div>
      
      <div className="modal-footer">
        <button onClick={resetToDefaults}>Reset to Defaults</button>
        <button onClick={onClose}>Close</button>
      </div>
    </div>
  );
};
```

---

## UI/UX Considerations

### Settings Access
- **Option 1**: Gear icon in table header
- **Option 2**: Right-click context menu on header
- **Option 3**: "Customize" button above table

### Visual Feedback
- Show column count: "Showing 8 of 11 columns"
- Highlight customized state: "Custom Layout (8 columns)"
- Visual indicator for drag-and-drop

### Responsive Design
- Mobile: Use preset layouts only (customization too complex)
- Tablet: Show simplified settings (checkboxes only)
- Desktop: Full drag-and-drop interface

### Accessibility
- Keyboard navigation for settings
- ARIA labels for drag handles
- Screen reader announcements for changes

---

## Dependencies

### Approach 1 (Preset Layouts)
- None (uses existing libraries)

### Approach 2 (Full Customization)
```json
{
  "dependencies": {
    "@dnd-kit/core": "^6.1.0",
    "@dnd-kit/sortable": "^8.0.0",
    "@dnd-kit/utilities": "^3.2.2"
  }
}
```

---

## Testing Plan

### Unit Tests
- Column configuration validation
- Visibility toggle logic
- Order manipulation functions
- Persistence save/load

### Integration Tests
- Settings modal interactions
- Drag-and-drop functionality
- Table re-rendering with custom config
- LocalStorage persistence

### E2E Tests
- Complete customization workflow
- Reset to defaults
- Multi-session persistence
- Mobile responsive behavior

---

## Migration & Backwards Compatibility

### Initial Release
1. All columns visible by default
2. No saved preferences = default layout
3. Settings UI available but optional

### Version Updates
- V1: Current fixed layout
- V2: Add preset layouts
- V3: Add full customization
- V4: Add backend persistence (optional)

### Data Migration
```typescript
// Handle old localStorage format
const migrateOldPreferences = (old: any): UserColumnPreferences => {
  // Migration logic here
  return {
    visibleColumns: old?.columns || DEFAULT_VISIBLE_COLUMNS,
    columnOrder: old?.order || DEFAULT_COLUMN_ORDER
  };
};
```

---

## Future Enhancements

### Phase 3 (Post-Launch)
1. **Column Width Adjustment**
   - Resizable columns (drag dividers)
   - Save width preferences

2. **Saved Presets**
   - User-named custom layouts
   - Share presets between users
   - Import/export JSON configs

3. **Column Grouping**
   - Group related columns (e.g., "Metadata")
   - Collapse/expand groups

4. **Advanced Filters per Column**
   - Filter rows by column values
   - Multi-column sorting

5. **Export Configurations**
   - Export visible data to CSV
   - Column order applies to export

---

## Recommendation

### Suggested Approach: **Two-Phase Implementation**

#### Phase 1: Preset Layouts (Sprint 1)
- **Effort**: 4-6 hours
- **Risk**: Low
- **Value**: High (quick wins for users)
- Implement 3 preset layouts
- LocalStorage persistence
- Simple dropdown UI

#### Phase 2: Full Customization (Sprint 2-3)
- **Effort**: 8-12 hours
- **Risk**: Medium
- **Value**: Very High (professional feature)
- Individual column toggles
- Drag-and-drop reordering
- Enhanced settings modal
- Optional: Backend persistence

### Success Metrics
- User adoption rate (% using custom layouts)
- Most popular column combinations
- Time saved in harvest operations
- User feedback scores

---

## Questions & Decisions Needed

1. **Persistence Strategy**: LocalStorage or backend API?
2. **Drag Library**: @dnd-kit or react-beautiful-dnd?
3. **Mobile Strategy**: Presets only or simplified customization?
4. **Default Layout**: Keep current "all columns" or create new default?
5. **Settings Access**: Gear icon, button, or context menu?

---

## References

### Related Files
- `/src/Web/ReactApp/src/components/harvest/IndexedFilesList.tsx`
- `/src/Web/ReactApp/src/types/api.ts`

### Similar Features in Codebase
- Check if printer table has column customization
- Review existing user preference patterns

### External Examples
- GitHub Issues table (column picker)
- Jira board customization
- DataTables.net column visibility

---

## Author & History

**Created**: 2025-10-06  
**Author**: Development Team  
**Status**: Planning / Proposal  
**Next Steps**: Review with team, prioritize phase, assign to sprint
