# Frontend (UI) Documentation

## 🎨 Design System & Components

For complete information about styling, themes, colors, and UI components, see:

**[DESIGN_SYSTEM.md](./DESIGN_SYSTEM.md)** - Master guide to:
- Theme system and CSS variables
- Color tokens and accessibility
- React component library
- Quick navigation by task and role

Also see:
- [UI & Styling Index](./UI_STYLING_INDEX.md) - Navigation index for all UI docs

## Overview

PrintFarmer uses a **component-based React architecture** with TypeScript for type safety and Tailwind CSS for responsive design.

## Page Structure

### Dashboard (Home)
Main overview page showing all printers with real-time status.

**Components:**
- `Dashboard.tsx` - Main container
- `PrinterGrid` - Grid layout of printers
- `PrinterCard` - Individual printer display
- `LocationFilter` - Filter by location dropdown
- `JobQueue` - Active jobs panel
- `HealthStatus` - System health indicator

**Features:**
- Real-time printer status via SignalR
- Filter by location
- Quick job queue view
- One-click print controls
- Auto-refresh on status changes

### Files Page
Unified file management interface with tabbed organization for 3D models, G-code files, and print harvest data.

**Tabs:**

1. **3D Models** (`/files?tab=models`)
   - `ModelsPage.tsx` - Model management interface
   - Upload and organize 3D model files
   - Tag models for categorization
   - Model metadata and preview
   - Search and filtering
   - Bulk operations

2. **G-Code** (`/files?tab=gcode`)
   - `GcodeLibraryPage.tsx` - G-code file browser
   - View sliced G-code files
   - File organization and search
   - Queue G-code to printers
   - File health and audit information
   - Printer model associations

3. **Print Harvest** (`/files?tab=harvest`)
   - `HarvestPage.tsx` - Print result data
   - Track completed print results
   - Harvest data analysis and statistics
   - Print success/failure rates
   - Model performance metrics
   - Integration with printer history

**Features:**
- Tab navigation with icons
- Persistent tab selection
- Responsive layout (full-width on all devices)
- Unified file operations across tabs
- Real-time file updates via SignalR
- Comprehensive search and filtering

**Tab Interface:**
```tsx
// User can switch between tabs with icon + label
Models | G-Code | Print Harvest
```

### Slice Page
Standalone browser-based 3D slicer application for converting models to G-code.

**Location:** `/slice`

**Features:**
- Full-featured slicer UI (OrcaSlicer/PrusaSlicer-like)
- 3D model preview and manipulation
- Slicer profile selection
- Real-time parameter adjustment
- Print preview and analysis
- Direct output to print queue

**Note:** This is a dedicated application interface separate from Files management. It provides an immersive slicing experience as a first-class application feature.

### Admin Control Center And Settings

Administrative UI is a **single URL-driven shell** rendered by
`SettingsShell.tsx`. Every admin/settings page is one of three routes:

| Route | Scope | Access |
|---|---|---|
| `/settings` | User (self-service) | Any authenticated user |
| `/admin/settings` | System (farm-wide config) | `farm_admin` role |
| `/admin/manage` | Admin (accounts, data, operations) | `farm_admin` role |
| `/admin` | Control Center hub | `farm_admin` role |

The shell is entirely driven by URL parameters:

- `?scope=<user|system|admin>` — which shell you are in (defaults from the route).
- `?tab=<categoryId>` — category within the scope.
- `?sub=<subPageId>` — sub-page within the tab.
- `?q=<query>` — search the current sub-page's metadata.
- `?field=<Section.Property>` — deep-link to one property row.

Exactly ONE settings page mounts at a time. The metadata-driven pages
(`<SettingsPage>`) filter by group and render fields from the backend metadata
API — no hand-written forms per property. Bespoke sub-pages (Bed Types,
Cameras, Users, Login Audit, Tags, Data Management, Slicer Profiles, Webhooks,
Workers, System Status) render their own components inside the shell.

For the full architecture, tab-to-group map, save model, Essential mode, and
palette details, see [SETTINGS_ARCHITECTURE.md](./SETTINGS_ARCHITECTURE.md).

#### Legacy Admin Routes

Older admin URLs (`/admin/tags`, `/admin/slicer-profiles`, `/admin/webhooks`,
`/admin/cameras`, `/admin/quotas`, `/admin/workers`, `/admin/system`, …) still
resolve — they auto-redirect to the appropriate shell location via
`src/Web/ReactApp/src/features/admin/registry/legacyRedirects.ts`. Do not delete
entries from that file when you rename a route; add a new one instead so
external bookmarks keep working.

### Command Palette (Ctrl+K)

`GlobalCommandPaletteProvider` is mounted globally in `Layout.tsx`, so
`Ctrl+K` (or `Cmd+K` on macOS) works on every authenticated route — not just
settings pages.

Items come from four sources:

1. **Places** — every registered admin destination.
2. **User settings sections** — user-scope settings-nav entries.
3. **Individual settings properties** — one row per property (farm_admin only),
   each linking to a `?field=Section.Property` deep-link on the appropriate
   sub-page.
4. **Actions** — Sign out, Refresh admin overview (farm_admin), Switch theme.

The palette is only mounted for authenticated users. Its metadata query is
disabled until first open so signed-out users never trigger a background
`401` on the metadata endpoint.

## Component Library

### Layout Components

**PageHeader**
```tsx
<PageHeader 
  title="Printers" 
  subtitle="Manage your 3D printers"
/>
```

**Card**
```tsx
<Card className="bg-white shadow-md">
  <CardHeader>Title</CardHeader>
  <CardBody>Content</CardBody>
</Card>
```

**Grid**
```tsx
<Grid cols={3} gap={4}>
  {items.map(item => <GridItem key={item.id}>{item}</GridItem>)}
</Grid>
```

### Form Components

**Form Input**
```tsx
<Input
  label="Printer Name"
  value={name}
  onChange={(e) => setName(e.target.value)}
  required
  placeholder="e.g., Printer 1"
/>
```

**Select Dropdown**
```tsx
<Select
  label="Select Location"
  value={locationId}
  onChange={(e) => setLocationId(e.target.value)}
  options={locations.map(l => ({ value: l.id, label: l.name }))}
/>
```

**Form Buttons**
```tsx
<FormButtons>
  <Button type="submit" variant="primary">Save</Button>
  <Button type="button" variant="secondary" onClick={onCancel}>Cancel</Button>
</FormButtons>
```

### Data Display

**Table**
```tsx
<Table>
  <TableHeader>
    <TableRow>
      <TableCell>Name</TableCell>
      <TableCell>Status</TableCell>
      <TableCell>Actions</TableCell>
    </TableRow>
  </TableHeader>
  <TableBody>
    {items.map(item => (
      <TableRow key={item.id}>
        <TableCell>{item.name}</TableCell>
        <TableCell><StatusBadge status={item.status} /></TableCell>
        <TableCell>
          <Button variant="small">Edit</Button>
        </TableCell>
      </TableRow>
    ))}
  </TableBody>
</Table>
```

**Badge**
```tsx
<Badge color="success">Online</Badge>
<Badge color="error">Offline</Badge>
<Badge color="warning">Idle</Badge>
```

## Printer Card Component

Displays individual printer status with real-time updates.

### Features

- **Printer Name** - Bold heading
- **Server URL** - Connection address
- **Status Indicator** - Online/offline badge
- **Temperature Display** - Current hotend/bed temps
- **Progress Bar** - Current job progress
- **Quick Actions** - Pause/resume/cancel buttons
- **Location Badge** - Current assigned location

### Usage

```tsx
import { PrinterCard } from '@/components/PrinterCard';

<PrinterCard 
  printer={printerData}
  onSelect={() => navigateToPrinter(printer.id)}
  onAction={(action) => handlePrinterAction(printer.id, action)}
/>
```

## Location Components

### LocationManagement
Administrative UI for managing locations.

**Capabilities:**
- List all active locations with printer counts
- Create new location with name/description
- Edit location details
- Delete location (soft delete)
- Export location list

**Usage:**
```tsx
<LocationManagement />
```

### LocationSelector
Reusable dropdown for selecting a location (used in printer forms).

**Features:**
- Auto-load locations from API
- Show printer count per location
- Optional "unassigned" option
- Disabled state support

**Usage:**
```tsx
<LocationSelector
  value={selectedLocationId}
  onChange={(locationId) => setLocation(locationId)}
  includeUnassigned={true}
/>
```

### PrinterLocationDragDrop
Interactive drag-and-drop interface for assigning printers to locations.

**Location:** `src/Web/ReactApp/src/components/PrinterLocationDragDrop.tsx`

**Layout:**
```
┌─────────────────────────────────────────┐
│ Unassigned  │ Location 1  │ Location 2  │
│ (4 items)   │(2 items)    │(3 items)    │
├─────────────┼────────────┼────────────┤
│[Printer A]  │[Printer C] │[Printer E] │
│[Printer B]  │[Printer D] │[Printer F] │
│[Printer G]  │            │            │
│[Printer H]  │            │            │
└─────────────┴────────────┴────────────┘
```

**Features:**
- Drag printers from "Unassigned" to assign
- Drag between locations to move
- Drag back to "Unassigned" to unassign
- Visual feedback: opacity 50% while dragging
- Drop zone highlighting (blue dashed border)
- Location cards show printer counts
- Error messages for failed operations
- Responsive grid (1 col mobile, 3 cols desktop)
- Automatic API updates on drop

**Sub-component - PrinterCard:**
- Printer name and server URL
- Draggable with visual feedback
- Hover effects

**API Integration:**
- `GET /api/printers` - Load all printers
- `POST /api/printers/{id}/location` - Assign to location
- `DELETE /api/printers/{id}/location` - Unassign

**Usage:**
```tsx
<PrinterLocationDragDrop />
```

## Drag and Drop Component

### PrinterLocationDragDrop
Interactive drag-and-drop interface for assigning printers to locations.

**Layout:**
```
┌─────────────────────────────────────────────────────────────┐
│  Unassigned    │  Location 1    │  Location 2    │  Location 3
│  (drag printers here)  (drop zone)    (drop zone)    (drop zone)
│                │                │                │
│  [Printer A]   │  [Printer D]   │  [Printer F]   │  [Printer H]
│  [Printer B]   │  [Printer E]   │  [Printer G]   │
│  [Printer C]   │                │                │
└─────────────────────────────────────────────────────────────┘
```

**Features:**
- Drag from unassigned to assign
- Drag from location to unassigned
- Visual feedback (opacity, border color)
- Hover effects on drop zones
- Error messages on failure
- Printer count display

**Usage:**
```tsx
<PrinterLocationDragDrop />
```

## Styling System

### Color Palette

```css
/* Primary */
--color-primary: #3b82f6;      /* Blue */

/* Status Colors */
--color-success: #10b981;      /* Green */
--color-warning: #f59e0b;      /* Amber */
--color-error: #ef4444;        /* Red */

/* Neutral */
--color-neutral-50: #f9fafb;
--color-neutral-100: #f3f4f6;
--color-neutral-200: #e5e7eb;
--color-neutral-900: #111827;
```

### Responsive Breakpoints

- **xs**: 0px and up
- **sm**: 640px and up
- **md**: 768px and up
- **lg**: 1024px and up
- **xl**: 1280px and up
- **2xl**: 1536px and up

### Example Responsive Design

```tsx
<div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
  {/* 1 column mobile, 2 tablet, 3 desktop, 4 large desktop */}
</div>
```

## Error Handling

### Error Boundary

```tsx
<ErrorBoundary fallback={<ErrorFallback />}>
  <YourComponent />
</ErrorBoundary>
```

### Error Messages

- **Toast Notifications** - Temporary messages (success, error, warning)
- **Form Errors** - Inline validation messages
- **Page Errors** - Full-page error display with recovery options

### Example

```tsx
const [error, setError] = useState(null);

try {
  const result = await locationService.createLocation(data);
  showToast('Location created successfully', 'success');
} catch (err) {
  setError(err.message);
  showToast('Failed to create location', 'error');
}
```

## Forms and Validation

### Form Pattern

```tsx
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';

const schema = z.object({
  name: z.string().min(1, 'Name is required'),
  description: z.string().optional(),
});

export function MyForm() {
  const { register, handleSubmit, formState: { errors } } = useForm({
    resolver: zodResolver(schema),
  });

  const onSubmit = async (data) => {
    // Handle submission
  };

  return (
    <form onSubmit={handleSubmit(onSubmit)}>
      <Input {...register('name')} error={errors.name?.message} />
      <Button type="submit">Submit</Button>
    </form>
  );
}
```

## Real-time Updates

### Using SignalR Context

```tsx
import { useSignalR } from '@/contexts/SignalRContext';

export function PrinterStatus() {
  const { printers, isConnected } = useSignalR();

  return (
    <div>
      <ConnectionStatus connected={isConnected} />
      {printers.map(p => (
        <PrinterCard key={p.id} printer={p} />
      ))}
    </div>
  );
}
```

### Manual Event Listening

```tsx
import { useEffect } from 'react';
import { signalRConnection } from '@/services/printerSignalR';

export function JobMonitor() {
  useEffect(() => {
    const handler = (jobData) => {
      console.log('Job updated:', jobData);
      // Update UI
    };

    signalRConnection.on('jobProgressUpdated', handler);

    return () => {
      signalRConnection.off('jobProgressUpdated', handler);
    };
  }, []);

  return <div>Job Monitor</div>;
}
```

## Loading States

### Skeleton Loading

```tsx
import { Skeleton } from '@/components/Skeleton';

{isLoading ? (
  <Skeleton className="h-64 w-full" />
) : (
  <PrinterGrid printers={printers} />
)}
```

### Loading Spinner

```tsx
import { Spinner } from '@/components/Spinner';

{isLoading && <Spinner />}
```

## Modal Dialogs

```tsx
import { Modal, ModalHeader, ModalBody, ModalFooter } from '@/components/Modal';

<Modal isOpen={isOpen} onClose={onClose}>
  <ModalHeader>Create New Location</ModalHeader>
  <ModalBody>
    <Form onSubmit={onSubmit} />
  </ModalBody>
  <ModalFooter>
    <Button onClick={onClose}>Cancel</Button>
    <Button variant="primary">Create</Button>
  </ModalFooter>
</Modal>
```

## Accessibility

### ARIA Labels

```tsx
<button aria-label="Delete printer" onClick={handleDelete}>
  <TrashIcon />
</button>
```

### Keyboard Navigation

- **Tab** - Navigate between elements
- **Enter** - Activate button/link
- **Escape** - Close modal/menu
- **Arrow Keys** - Navigate menu items

## Performance Tips

### Code Splitting

```tsx
import { lazy, Suspense } from 'react';

const AdminPage = lazy(() => import('@/pages/AdminPage'));

<Suspense fallback={<Spinner />}>
  <AdminPage />
</Suspense>
```

### Memoization

```tsx
import { memo } from 'react';

export const PrinterCard = memo(function PrinterCard({ printer }) {
  return <Card>{printer.name}</Card>;
});
```

### Query Optimization

```tsx
import { useQuery } from '@tanstack/react-query';

const { data: printers } = useQuery({
  queryKey: ['printers'],
  queryFn: () => printerService.getAllPrinters(),
  staleTime: 1000 * 60, // 1 minute
});
```

## Development Guidelines

### Component Naming
- Use PascalCase for component filenames and exports
- Use descriptive names (e.g., `PrinterCard` not `Card`)
- Group related components in subdirectories

### Props Typing
```tsx
interface PrinterCardProps {
  printer: Printer;
  isSelected?: boolean;
  onSelect?: (printer: Printer) => void;
}

export function PrinterCard({ printer, isSelected, onSelect }: PrinterCardProps) {
  // ...
}
```

### Hook Usage
- Use custom hooks to share component logic
- Hook names start with `use` (e.g., `usePrinter`)
- Keep hooks focused on single concern

### TypeScript Type Organization

All TypeScript types are organized in feature-based files in `src/types/`:

| File | Purpose | Key Types |
|------|---------|-----------|
| `api.ts` | Backend API DTOs | Printer, GcodeFile, QueueJob, PrinterBackend |
| `models.ts` | 3D model management | Model, ModelTag, ModelListItem |
| `queue.ts` | Print queue management | JobStatus, JobAction, HistoryJob, ModelStats |
| `gcode.ts` | G-code file management | GCodeFile, FileEntry, HarvestDiscoveredFile |
| `slicer.ts` | Slicer configuration | MaterialType, SlicerSettingsDto, MaterialPreset |
| `admin.ts` | Admin interface | User, Role, SystemLog, TagOption |
| `components.ts` | Component props | 77+ component prop interfaces |

**Import patterns:**
```tsx
// Feature types
import type { Model, ModelTag } from '@/types/models';
import type { JobStatus, HistoryJob } from '@/types/queue';

// Component props
import type { ModelGridViewProps } from '@/types/components';

// API types
import type { Printer, GcodeFile } from '@/types/api';
```

**Guidelines:**
- Add new types to the appropriate feature file
- Use named exports for all types
- Add JSDoc comments explaining the purpose
- Avoid circular imports between type files

## Testing Components

```tsx
import { render, screen } from '@testing-library/react';
import { PrinterCard } from '@/components/PrinterCard';

describe('PrinterCard', () => {
  it('displays printer name', () => {
    render(<PrinterCard printer={{ name: 'Test' }} />);
    expect(screen.getByText('Test')).toBeInTheDocument();
  });
});
```
