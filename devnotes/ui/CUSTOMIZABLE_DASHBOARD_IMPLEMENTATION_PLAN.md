# Customizable Dashboard Implementation Plan (Option C)

## Overview

Implement a fully customizable dashboard with drag-and-drop widget positioning, resizing, and visibility controls. This provides users with a Grafana/Windows Widgets-style experience to personalize their PrintFarmer dashboard.

## Goals

1. **Drag-and-Drop**: Users can drag widgets to reposition them on a responsive grid
2. **Resizable Widgets**: Widgets can be resized (1x1, 2x1, 1x2, 2x2, etc.)
3. **Show/Hide Widgets**: Toggle visibility of individual widgets
4. **Persistence**: Layout saved to localStorage, survives page refresh/browser restart
5. **Reset to Default**: One-click restore to factory layout
6. **Responsive**: Adapts to mobile/tablet/desktop breakpoints
7. **Accessible**: Full keyboard navigation for drag operations

---

## Technical Stack

### New Dependency

```bash
npm install react-grid-layout @types/react-grid-layout
```

**Why `react-grid-layout`?**
- Battle-tested (used by Grafana, Kibana, many enterprise dashboards)
- Built-in responsive breakpoints
- Handles collision detection, drag boundaries
- Good accessibility support
- Active maintenance, React 18/19 compatible
- ~50KB gzipped (acceptable for the functionality)

---

## Architecture

### File Structure

```
src/
├── common/
│   ├── components/
│   │   └── DashboardWidget.tsx          # Existing - no changes needed
│   └── hooks/
│       └── useDashboardLayout.ts        # NEW: Layout state management
├── features/
│   └── printers/
│       └── components/
│           ├── PrinterDashboard.tsx     # MODIFY: Use CustomizableDashboard
│           ├── CustomizableDashboard.tsx # NEW: Grid layout container
│           ├── DashboardWidgetRegistry.ts # NEW: Widget definitions
│           ├── DashboardToolbar.tsx     # NEW: Customize/reset buttons
│           └── DashboardCustomizeModal.tsx # NEW: Add/remove widgets modal
```

---

## Implementation Phases

### Phase 1: Foundation (~150 lines)

#### 1.1 Widget Registry (`DashboardWidgetRegistry.ts`)

Defines all available widgets with metadata:

```typescript
import { ComponentType } from 'react';
import { AlertsWidget } from './AlertsWidget';
import { TasksWidget } from '@/features/tasks';
import { ActiveJobsWidget } from './ActiveJobsWidget';
import { RecentPrintsWidget } from './RecentPrintsWidget';
import { MaintenanceAlertsWidget, MaintenanceOverviewWidget } from '@/features/maintenance/components';
import { BackgroundServicesWidget } from '@/features/admin/components';
import { DetailedSystemHealth } from './SystemHealth';
import { 
  AlertTriangleIcon, ClipboardListIcon, PlayIcon, TrendingUpIcon,
  WrenchIcon, BarChartIcon, ServerIcon, ActivityIcon 
} from '@/common/components/icons/MdiIcons';

export interface WidgetDefinition {
  id: string;
  title: string;
  description: string;
  icon: ComponentType<{ className?: string }>;
  component: ComponentType<{ className?: string }>;
  /** Default grid dimensions: { w: columns, h: rows } */
  defaultSize: { w: number; h: number };
  /** Minimum grid dimensions */
  minSize: { w: number; h: number };
  /** Maximum grid dimensions (optional) */
  maxSize?: { w: number; h: number };
  /** Category for grouping in the add widget modal */
  category: 'monitoring' | 'jobs' | 'maintenance' | 'system';
}

export const WIDGET_REGISTRY: WidgetDefinition[] = [
  {
    id: 'alerts',
    title: 'Alerts',
    description: 'Active alerts requiring attention',
    icon: AlertTriangleIcon,
    component: AlertsWidget,
    defaultSize: { w: 1, h: 1 },
    minSize: { w: 1, h: 1 },
    category: 'monitoring',
  },
  {
    id: 'tasks',
    title: 'Pending Tasks',
    description: 'User tasks awaiting action',
    icon: ClipboardListIcon,
    component: TasksWidget,
    defaultSize: { w: 1, h: 1 },
    minSize: { w: 1, h: 1 },
    category: 'monitoring',
  },
  {
    id: 'active-jobs',
    title: 'Active Jobs',
    description: 'Currently printing and queued jobs',
    icon: PlayIcon,
    component: ActiveJobsWidget,
    defaultSize: { w: 1, h: 1 },
    minSize: { w: 1, h: 1 },
    category: 'jobs',
  },
  {
    id: 'recent-prints',
    title: 'Recent Prints',
    description: 'Print history and completed jobs',
    icon: TrendingUpIcon,
    component: RecentPrintsWidget,
    defaultSize: { w: 1, h: 1 },
    minSize: { w: 1, h: 1 },
    category: 'jobs',
  },
  {
    id: 'maintenance-alerts',
    title: 'Maintenance Alerts',
    description: 'Upcoming and overdue maintenance',
    icon: WrenchIcon,
    component: MaintenanceAlertsWidget,
    defaultSize: { w: 1, h: 1 },
    minSize: { w: 1, h: 1 },
    category: 'maintenance',
  },
  {
    id: 'maintenance-overview',
    title: 'Maintenance Overview',
    description: 'Maintenance statistics and schedule',
    icon: BarChartIcon,
    component: MaintenanceOverviewWidget,
    defaultSize: { w: 1, h: 1 },
    minSize: { w: 1, h: 1 },
    category: 'maintenance',
  },
  {
    id: 'background-services',
    title: 'Background Services',
    description: 'Service health and status',
    icon: ServerIcon,
    component: BackgroundServicesWidget,
    defaultSize: { w: 1, h: 1 },
    minSize: { w: 1, h: 1 },
    category: 'system',
  },
  {
    id: 'system-health',
    title: 'System Health',
    description: 'Overall system health checks',
    icon: ActivityIcon,
    component: DetailedSystemHealth,
    defaultSize: { w: 1, h: 1 },
    minSize: { w: 1, h: 1 },
    category: 'system',
  },
];

export const DEFAULT_LAYOUT: LayoutItem[] = [
  { i: 'alerts',              x: 0, y: 0, w: 1, h: 1 },
  { i: 'tasks',               x: 1, y: 0, w: 1, h: 1 },
  { i: 'active-jobs',         x: 0, y: 1, w: 1, h: 1 },
  { i: 'recent-prints',       x: 1, y: 1, w: 1, h: 1 },
  { i: 'maintenance-alerts',  x: 0, y: 2, w: 1, h: 1 },
  { i: 'maintenance-overview',x: 1, y: 2, w: 1, h: 1 },
  { i: 'background-services', x: 0, y: 3, w: 1, h: 1 },
  { i: 'system-health',       x: 1, y: 3, w: 1, h: 1 },
];

export function getWidgetById(id: string): WidgetDefinition | undefined {
  return WIDGET_REGISTRY.find(w => w.id === id);
}
```

#### 1.2 Layout Hook (`useDashboardLayout.ts`)

```typescript
import { useState, useCallback, useEffect } from 'react';
import { Layout } from 'react-grid-layout';
import { DEFAULT_LAYOUT } from '@/features/printers/components/DashboardWidgetRegistry';

const STORAGE_KEY = 'printfarmer-dashboard-layout';

export interface DashboardLayoutState {
  layout: Layout[];
  hiddenWidgets: string[];
}

export function useDashboardLayout() {
  const [state, setState] = useState<DashboardLayoutState>(() => {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved) {
        return JSON.parse(saved);
      }
    } catch (e) {
      console.warn('Failed to load dashboard layout:', e);
    }
    return { layout: DEFAULT_LAYOUT, hiddenWidgets: [] };
  });

  // Persist to localStorage on change
  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
    } catch (e) {
      console.warn('Failed to save dashboard layout:', e);
    }
  }, [state]);

  const updateLayout = useCallback((newLayout: Layout[]) => {
    setState(prev => ({ ...prev, layout: newLayout }));
  }, []);

  const toggleWidget = useCallback((widgetId: string) => {
    setState(prev => {
      const isHidden = prev.hiddenWidgets.includes(widgetId);
      if (isHidden) {
        // Show widget - add to layout at bottom
        const maxY = Math.max(...prev.layout.map(l => l.y + l.h), 0);
        const newItem: Layout = { i: widgetId, x: 0, y: maxY, w: 1, h: 1 };
        return {
          layout: [...prev.layout, newItem],
          hiddenWidgets: prev.hiddenWidgets.filter(id => id !== widgetId),
        };
      } else {
        // Hide widget - remove from layout
        return {
          layout: prev.layout.filter(l => l.i !== widgetId),
          hiddenWidgets: [...prev.hiddenWidgets, widgetId],
        };
      }
    });
  }, []);

  const resetLayout = useCallback(() => {
    setState({ layout: DEFAULT_LAYOUT, hiddenWidgets: [] });
  }, []);

  const addWidget = useCallback((widgetId: string) => {
    setState(prev => {
      if (prev.layout.some(l => l.i === widgetId)) return prev;
      const maxY = Math.max(...prev.layout.map(l => l.y + l.h), 0);
      const newItem: Layout = { i: widgetId, x: 0, y: maxY, w: 1, h: 1 };
      return {
        layout: [...prev.layout, newItem],
        hiddenWidgets: prev.hiddenWidgets.filter(id => id !== widgetId),
      };
    });
  }, []);

  const removeWidget = useCallback((widgetId: string) => {
    setState(prev => ({
      layout: prev.layout.filter(l => l.i !== widgetId),
      hiddenWidgets: [...prev.hiddenWidgets, widgetId],
    }));
  }, []);

  return {
    layout: state.layout,
    hiddenWidgets: state.hiddenWidgets,
    visibleWidgetIds: state.layout.map(l => l.i),
    updateLayout,
    toggleWidget,
    addWidget,
    removeWidget,
    resetLayout,
  };
}
```

---

### Phase 2: Grid Component (~150 lines)

#### 2.1 Customizable Dashboard (`CustomizableDashboard.tsx`)

```typescript
import React, { useMemo } from 'react';
import { Responsive, WidthProvider, Layout } from 'react-grid-layout';
import 'react-grid-layout/css/styles.css';
import 'react-resizable/css/styles.css';
import { useDashboardLayout } from '@/common/hooks/useDashboardLayout';
import { WIDGET_REGISTRY, getWidgetById } from './DashboardWidgetRegistry';
import { DashboardToolbar } from './DashboardToolbar';
import { GripVerticalIcon } from '@/common/components/icons/MdiIcons';

const ResponsiveGridLayout = WidthProvider(Responsive);

interface CustomizableDashboardProps {
  className?: string;
}

export function CustomizableDashboard({ className = '' }: CustomizableDashboardProps) {
  const { 
    layout, 
    hiddenWidgets, 
    updateLayout, 
    addWidget, 
    removeWidget, 
    resetLayout 
  } = useDashboardLayout();

  const [isEditMode, setIsEditMode] = React.useState(false);

  // Generate responsive layouts
  const layouts = useMemo(() => ({
    lg: layout,
    md: layout,
    sm: layout.map(l => ({ ...l, w: 2, x: 0 })), // Stack on small screens
    xs: layout.map(l => ({ ...l, w: 2, x: 0 })),
  }), [layout]);

  const handleLayoutChange = (currentLayout: Layout[], allLayouts: Record<string, Layout[]>) => {
    // Only save the lg layout to avoid responsive layout overwriting user config
    if (allLayouts.lg) {
      updateLayout(allLayouts.lg);
    }
  };

  return (
    <div className={className}>
      <DashboardToolbar
        isEditMode={isEditMode}
        onToggleEditMode={() => setIsEditMode(!isEditMode)}
        onReset={resetLayout}
        hiddenWidgets={hiddenWidgets}
        onAddWidget={addWidget}
      />

      <ResponsiveGridLayout
        className="layout"
        layouts={layouts}
        breakpoints={{ lg: 1200, md: 996, sm: 768, xs: 480 }}
        cols={{ lg: 2, md: 2, sm: 2, xs: 1 }}
        rowHeight={300}
        isDraggable={isEditMode}
        isResizable={isEditMode}
        onLayoutChange={handleLayoutChange}
        draggableHandle=".widget-drag-handle"
        margin={[24, 24]}
        containerPadding={[0, 0]}
      >
        {layout.map((item) => {
          const widget = getWidgetById(item.i);
          if (!widget) return null;
          
          const WidgetComponent = widget.component;
          
          return (
            <div key={item.i} className="relative group">
              {/* Edit mode overlay with drag handle */}
              {isEditMode && (
                <div className="absolute top-0 left-0 right-0 z-10 flex items-center justify-between px-3 py-2 bg-pf-bg-2/90 border-b border-pf-border rounded-t-xl">
                  <div className="widget-drag-handle cursor-move flex items-center gap-2 text-pf-text-secondary">
                    <GripVerticalIcon className="h-5 w-5" />
                    <span className="text-sm font-medium">{widget.title}</span>
                  </div>
                  <button
                    onClick={() => removeWidget(item.i)}
                    className="text-pf-text-tertiary hover:text-pf-error-text transition-colors"
                    aria-label={`Remove ${widget.title} widget`}
                  >
                    <span className="text-lg">×</span>
                  </button>
                </div>
              )}
              
              {/* Widget content */}
              <div className={isEditMode ? 'pt-10' : ''}>
                <WidgetComponent className="h-full" />
              </div>
            </div>
          );
        })}
      </ResponsiveGridLayout>
    </div>
  );
}
```

---

### Phase 3: Toolbar & Modal (~150 lines)

#### 3.1 Dashboard Toolbar (`DashboardToolbar.tsx`)

```typescript
import React, { useState } from 'react';
import { SettingsIcon, RefreshIcon, PlusIcon, GridIcon } from '@/common/components/icons/MdiIcons';
import { DashboardCustomizeModal } from './DashboardCustomizeModal';

interface DashboardToolbarProps {
  isEditMode: boolean;
  onToggleEditMode: () => void;
  onReset: () => void;
  hiddenWidgets: string[];
  onAddWidget: (widgetId: string) => void;
}

export function DashboardToolbar({
  isEditMode,
  onToggleEditMode,
  onReset,
  hiddenWidgets,
  onAddWidget,
}: DashboardToolbarProps) {
  const [isModalOpen, setIsModalOpen] = useState(false);

  return (
    <div className="flex items-center justify-end gap-2 mb-4">
      {/* Add Widget Button - only visible if widgets are hidden */}
      {hiddenWidgets.length > 0 && (
        <button
          onClick={() => setIsModalOpen(true)}
          className="inline-flex items-center gap-2 px-3 py-2 text-sm font-medium text-pf-text-secondary bg-pf-bg-1 border border-pf-border rounded-lg hover:bg-pf-bg-2 transition-colors"
          aria-label="Add widget"
        >
          <PlusIcon className="h-4 w-4" />
          Add Widget
          <span className="ml-1 px-1.5 py-0.5 text-xs bg-pf-accent/20 text-pf-accent rounded-full">
            {hiddenWidgets.length}
          </span>
        </button>
      )}

      {/* Edit/Done Button */}
      <button
        onClick={onToggleEditMode}
        className={`inline-flex items-center gap-2 px-3 py-2 text-sm font-medium rounded-lg transition-colors ${
          isEditMode
            ? 'text-pf-text-on-accent bg-pf-accent hover:bg-pf-accent/90'
            : 'text-pf-text-secondary bg-pf-bg-1 border border-pf-border hover:bg-pf-bg-2'
        }`}
        aria-pressed={isEditMode}
        aria-label={isEditMode ? 'Done editing' : 'Customize dashboard'}
      >
        {isEditMode ? (
          <>
            <CheckIcon className="h-4 w-4" />
            Done
          </>
        ) : (
          <>
            <GridIcon className="h-4 w-4" />
            Customize
          </>
        )}
      </button>

      {/* Reset Button - only visible in edit mode */}
      {isEditMode && (
        <button
          onClick={onReset}
          className="inline-flex items-center gap-2 px-3 py-2 text-sm font-medium text-pf-warning bg-pf-bg-1 border border-pf-border rounded-lg hover:bg-pf-bg-2 transition-colors"
          aria-label="Reset layout to default"
        >
          <RefreshIcon className="h-4 w-4" />
          Reset
        </button>
      )}

      {/* Add Widget Modal */}
      <DashboardCustomizeModal
        isOpen={isModalOpen}
        onClose={() => setIsModalOpen(false)}
        hiddenWidgets={hiddenWidgets}
        onAddWidget={onAddWidget}
      />
    </div>
  );
}
```

#### 3.2 Customize Modal (`DashboardCustomizeModal.tsx`)

```typescript
import React from 'react';
import { Dialog, DialogPanel, DialogTitle, Transition, TransitionChild } from '@headlessui/react';
import { WIDGET_REGISTRY, getWidgetById } from './DashboardWidgetRegistry';
import { PlusIcon, XIcon } from '@/common/components/icons/MdiIcons';

interface DashboardCustomizeModalProps {
  isOpen: boolean;
  onClose: () => void;
  hiddenWidgets: string[];
  onAddWidget: (widgetId: string) => void;
}

export function DashboardCustomizeModal({
  isOpen,
  onClose,
  hiddenWidgets,
  onAddWidget,
}: DashboardCustomizeModalProps) {
  const categories = ['monitoring', 'jobs', 'maintenance', 'system'] as const;
  const categoryLabels: Record<string, string> = {
    monitoring: 'Monitoring',
    jobs: 'Print Jobs',
    maintenance: 'Maintenance',
    system: 'System',
  };

  const handleAdd = (widgetId: string) => {
    onAddWidget(widgetId);
    // Don't close modal - user might want to add multiple widgets
  };

  return (
    <Transition show={isOpen}>
      <Dialog onClose={onClose} className="relative z-50">
        {/* Backdrop */}
        <TransitionChild
          enter="ease-out duration-200"
          enterFrom="opacity-0"
          enterTo="opacity-100"
          leave="ease-in duration-150"
          leaveFrom="opacity-100"
          leaveTo="opacity-0"
        >
          <div className="fixed inset-0 bg-black/50" aria-hidden="true" />
        </TransitionChild>

        {/* Modal */}
        <div className="fixed inset-0 flex items-center justify-center p-4">
          <TransitionChild
            enter="ease-out duration-200"
            enterFrom="opacity-0 scale-95"
            enterTo="opacity-100 scale-100"
            leave="ease-in duration-150"
            leaveFrom="opacity-100 scale-100"
            leaveTo="opacity-0 scale-95"
          >
            <DialogPanel className="w-full max-w-md bg-pf-bg-1 border border-pf-border rounded-xl shadow-xl">
              {/* Header */}
              <div className="flex items-center justify-between p-4 border-b border-pf-border">
                <DialogTitle className="text-lg font-semibold text-pf-text-primary">
                  Add Widgets
                </DialogTitle>
                <button
                  onClick={onClose}
                  className="text-pf-text-tertiary hover:text-pf-text-primary transition-colors"
                  aria-label="Close"
                >
                  <XIcon className="h-5 w-5" />
                </button>
              </div>

              {/* Content */}
              <div className="p-4 max-h-96 overflow-y-auto">
                {hiddenWidgets.length === 0 ? (
                  <p className="text-center text-pf-text-tertiary py-8">
                    All widgets are currently visible on your dashboard.
                  </p>
                ) : (
                  <div className="space-y-4">
                    {categories.map((category) => {
                      const widgets = WIDGET_REGISTRY.filter(
                        w => w.category === category && hiddenWidgets.includes(w.id)
                      );
                      if (widgets.length === 0) return null;

                      return (
                        <div key={category}>
                          <h3 className="text-xs font-semibold text-pf-text-tertiary uppercase tracking-wider mb-2">
                            {categoryLabels[category]}
                          </h3>
                          <div className="space-y-2">
                            {widgets.map((widget) => {
                              const Icon = widget.icon;
                              return (
                                <button
                                  key={widget.id}
                                  onClick={() => handleAdd(widget.id)}
                                  className="w-full flex items-center gap-3 p-3 bg-pf-bg-2 border border-pf-border rounded-lg hover:border-pf-accent transition-colors text-left"
                                >
                                  <div className="p-2 bg-pf-bg-1 rounded-lg">
                                    <Icon className="h-5 w-5 text-pf-text-secondary" />
                                  </div>
                                  <div className="flex-1 min-w-0">
                                    <p className="text-sm font-medium text-pf-text-primary">
                                      {widget.title}
                                    </p>
                                    <p className="text-xs text-pf-text-tertiary truncate">
                                      {widget.description}
                                    </p>
                                  </div>
                                  <PlusIcon className="h-5 w-5 text-pf-accent flex-shrink-0" />
                                </button>
                              );
                            })}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>

              {/* Footer */}
              <div className="flex justify-end p-4 border-t border-pf-border">
                <button
                  onClick={onClose}
                  className="px-4 py-2 text-sm font-medium text-pf-text-primary bg-pf-bg-2 border border-pf-border rounded-lg hover:bg-pf-bg-1 transition-colors"
                >
                  Done
                </button>
              </div>
            </DialogPanel>
          </TransitionChild>
        </div>
      </Dialog>
    </Transition>
  );
}
```

---

### Phase 4: Integration (~50 lines)

#### 4.1 Update PrinterDashboard

Replace the fixed grid rows with `CustomizableDashboard`:

```typescript
// Replace:
{/* Main Dashboard Content */}
<div className="space-y-6">
  {/* Row 1: Alerts and Pending Tasks */}
  <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
    <AlertsWidget />
    <TasksWidget />
  </div>
  {/* ... more rows ... */}
</div>

// With:
{/* Main Dashboard Content */}
<CustomizableDashboard />
```

#### 4.2 Add CSS for react-grid-layout

Add to `src/index.css` or create a new file:

```css
/* Dashboard grid customization */
.react-grid-item {
  transition: all 200ms ease;
  transition-property: left, top, width, height;
}

.react-grid-item.cssTransforms {
  transition-property: transform, width, height;
}

.react-grid-item.resizing {
  z-index: 1;
  will-change: width, height;
}

.react-grid-item.react-draggable-dragging {
  transition: none;
  z-index: 3;
  will-change: transform;
}

.react-grid-item > .react-resizable-handle {
  position: absolute;
  width: 20px;
  height: 20px;
}

.react-grid-item > .react-resizable-handle::after {
  content: '';
  position: absolute;
  right: 3px;
  bottom: 3px;
  width: 8px;
  height: 8px;
  border-right: 2px solid var(--color-pf-border-medium);
  border-bottom: 2px solid var(--color-pf-border-medium);
  border-radius: 1px;
}

.react-grid-placeholder {
  background: var(--color-pf-accent);
  opacity: 0.2;
  border-radius: 0.75rem;
  transition-duration: 100ms;
  z-index: 2;
  user-select: none;
}
```

---

## Testing Plan

### Unit Tests

1. **useDashboardLayout hook**
   - Loads default layout when localStorage is empty
   - Loads saved layout from localStorage
   - Persists layout changes to localStorage
   - `toggleWidget` adds/removes widgets correctly
   - `resetLayout` restores default layout

2. **DashboardWidgetRegistry**
   - All widgets have required properties
   - `getWidgetById` returns correct widget
   - No duplicate widget IDs

### Integration Tests

1. **CustomizableDashboard**
   - Renders all visible widgets
   - Edit mode toggles drag/resize handles
   - Removing widget updates layout
   - Adding widget from modal works

### Manual Testing Checklist

- [ ] Drag widget to new position
- [ ] Resize widget (if enabled)
- [ ] Remove widget via X button
- [ ] Add widget via modal
- [ ] Reset layout to default
- [ ] Refresh page - layout persists
- [ ] Open in incognito - default layout shown
- [ ] Test on mobile breakpoint
- [ ] Keyboard navigation in modal
- [ ] Screen reader announces drag operations

---

## Accessibility Considerations

1. **Keyboard Navigation**
   - Modal uses focus trap
   - Add/remove buttons are keyboard accessible
   - Consider skip links to bypass grid when not editing

2. **Screen Reader Support**
   - Announce when edit mode is toggled
   - Announce when widget is added/removed
   - `aria-pressed` on edit mode toggle
   - `aria-label` on all buttons

3. **Motion Sensitivity**
   - Respect `prefers-reduced-motion`
   - Disable animations for users who prefer reduced motion

```css
@media (prefers-reduced-motion: reduce) {
  .react-grid-item {
    transition: none;
  }
}
```

---

## Performance Considerations

1. **Lazy Load Widgets**
   - Use `React.lazy()` for heavy widgets (SystemHealth, BackgroundServices)
   - Wrap in Suspense with skeleton fallback

2. **Memoization**
   - Memoize widget components to prevent re-renders on layout change
   - Memoize layout calculations

3. **Debounce Persistence**
   - Debounce localStorage writes during drag operations
   - Only save after drag ends

---

## Future Enhancements

1. **Sync Across Devices** - Store layout in user preferences API
2. **Multiple Layouts** - Save named dashboard layouts ("Work", "Monitoring", etc.)
3. **Widget Presets** - Quick-apply preset layouts for different use cases
4. **Wider Widgets** - Allow 2-column widgets for more detail
5. **Custom Widget Titles** - Let users rename widgets
6. **Widget Pinning** - Pin critical widgets to top, always visible

---

## Estimated Effort

| Component                    | Lines | Time     |
|-----------------------------|-------|----------|
| DashboardWidgetRegistry.ts  | ~100  | 30 min   |
| useDashboardLayout.ts       | ~80   | 30 min   |
| CustomizableDashboard.tsx   | ~100  | 45 min   |
| DashboardToolbar.tsx        | ~80   | 30 min   |
| DashboardCustomizeModal.tsx | ~120  | 45 min   |
| CSS/Styling                 | ~50   | 20 min   |
| Update PrinterDashboard.tsx | ~20   | 10 min   |
| Tests                       | ~150  | 60 min   |
| **Total**                   | ~700  | ~4.5 hrs |

---

## Dependencies Summary

```bash
# Required new dependency
npm install react-grid-layout

# Type definitions (included in @types/react-grid-layout)
npm install -D @types/react-grid-layout
```

---

## References

- [react-grid-layout Documentation](https://github.com/react-grid-layout/react-grid-layout)
- [Grafana Dashboard Panels](https://grafana.com/docs/grafana/latest/panels-visualizations/)
- [WCAG 2.1 Drag and Drop](https://www.w3.org/WAI/WCAG21/Understanding/dragging-movements.html)
