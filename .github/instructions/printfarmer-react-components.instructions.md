---
description: 'PrintFarmer-specific React component patterns: UI library, query/mutation conventions, form handling, and feature structure'
applyTo: '**/*.tsx, **/*.ts'
---

# PrintFarmer React Component Patterns

Conventions for building React components in PrintFarmer. Covers both general React best practices and project-specific patterns.

## General React Guidelines

- **Functional components with hooks** — no class components
- **Naming**: PascalCase for components/types, camelCase for functions/variables
- **Hooks**: Always provide correct `useEffect` dependency arrays; add cleanup functions to prevent memory leaks; follow rules of hooks (top-level only)
- **Performance**: Use `React.memo`, `useMemo`, `useCallback` judiciously — profile first with React DevTools before optimizing
- **Code splitting**: Use `React.lazy` + `Suspense` for route-level splitting
- **Error Boundaries**: Wrap feature sections in Error Boundaries for graceful degradation
- **Testing**: Use Vitest + React Testing Library; test behavior, not implementation details; mock API calls via `vi.mock`
- **Security**: Never render unsanitized user input; use `textContent` over `dangerouslySetInnerHTML`
- **TypeScript**: Use interfaces for props; leverage `React.ComponentProps`, `React.ReactNode`; strict mode enabled

## Feature Folder Structure

Organize new features under `src/features/<feature>/`:

```
src/features/<feature>/
├── components/    # Feature-specific components and modals
├── pages/         # Route-level page components
├── hooks/         # Feature-specific custom hooks (optional)
└── utils/         # Feature-specific utilities (optional)
```

Shared, cross-feature components go in `src/common/components/` or `src/components/`.

## UI Component Library

Import all shared UI components from the barrel export:

```tsx
import { Button, Input, Select, FormField, Card, Badge, Spinner } from '@/common/components/ui';
```

Do NOT use raw HTML `<button>`, `<input>`, `<select>` elements — global CSS in `controls.css` overrides their styling. Always use the library components.

### Key Components and Props

| Component | Key Props | Notes |
|-----------|-----------|-------|
| `Button` | `variant` (primary/secondary/danger/subtle/ghost/success/link/unstyled), `size` (sm/md/lg), `loading`, `iconLeft`/`iconRight` | Uses `data-pf-button` to escape global gradient |
| `Input` | Extends `<input>`, adds `invalid` | Wrap in `FormField` for labels |
| `Select` | Extends `<select>`, adds `invalid`, `containerClassName` | Wrap in `FormField` for labels |
| `FormField` | `label`, `htmlFor`, `error`, `required`, `helper`, `inline` | Associates label with control |
| `Card` | Sub-components: `Card.Header`, `Card.Body`, `Card.Footer` | Layout container |
| `Badge` | `variant` (default/primary/success/warning/error), `size` | Status indicators |
| `Spinner` | `size` | Loading indicator |
| `DataTable` | `columns`, `data`, `sortable` | Tabular data |
| `Tabs` | `TabList`, `Tab`, `TabPanels`, `TabPanel` | Tabbed content |
| `Alert` | `variant`, `title` | Informational banners |
| `Toggle` | Standard toggle switch | Boolean settings |
| `FileUpload` | File input with drag-and-drop | File uploads |

### Modals

Modals live in `@/common/components/modals/Modal`:

```tsx
import { Modal } from '@/common/components/modals/Modal';

<Modal isOpen={isOpen} onClose={onClose} title="Edit Item" size="md" footer={footerButtons}>
  {/* content */}
</Modal>
```

Sizes: `sm`, `md`, `lg`, `xl`, `full`.

### Icons

Use MDI icons from the project icon set:

```tsx
import { PlusIcon, DeleteIcon, SearchIcon } from '@/common/components/icons/MdiIcons';
```

`@heroicons/react/24/solid` is used in some older components but prefer MDI for consistency.

## Page Layout

Wrap all page components in `PageTemplate`:

```tsx
import { PageTemplate } from '@/common/components/PageTemplate';

function MyPage() {
  return (
    <PageTemplate title="Page Title" subtitle="Optional subtitle" icon={<MyIcon />} actions={<Button>Action</Button>}>
      {/* page content */}
    </PageTemplate>
  );
}
```

## Styling

- **Tailwind CSS v4** with custom `pf-` design tokens
- Use `clsx` (not `classnames`) for conditional class composition
- Common tokens: `bg-pf-bg-0`, `text-pf-text-primary`, `border-pf-border`, `text-pf-error`, `bg-pf-accent-bg`
- No CSS modules — use Tailwind utility classes exclusively

```tsx
import clsx from 'clsx';

<div className={clsx('p-4 rounded-lg bg-pf-bg-0', isActive && 'border-pf-accent')}>
```

## Data Fetching with TanStack Query

### Query Keys

Use the centralized `queryKeys` object from `useApi.ts` for shared data:

```tsx
import { queryKeys, usePrinters, useManufacturers } from '@/common/hooks/useApi';
```

For feature-specific queries, use descriptive kebab-case string arrays:

```tsx
const { data } = useQuery({
  queryKey: ['project-templates'],
  queryFn: () => templateService.getTemplates(),
});
```

Key hierarchy: `['entity', id?, 'sub-resource']` — e.g., `['printers', id, 'details']`.

### useQuery Pattern

```tsx
const { data: items = [], isLoading, error } = useQuery({
  queryKey: queryKeys.myEntity,        // or ['my-entity', filter]
  queryFn: () => apiClient.getItems(),
  staleTime: 30_000,                   // 30s for frequently-changing data
  enabled: !!requiredParam,            // Skip query until param ready
});
```

**staleTime guidelines:**
- 10s — real-time data (printer status)
- 30s — frequently-updated lists (printers, jobs)
- 5min (`300_000`) — catalog/reference data (manufacturers, models)
- 10min (`600_000`) — rarely-changing data (capabilities)

### Shared Hooks in useApi.ts

For data used across multiple features, define hooks in `src/common/hooks/useApi.ts`:

```tsx
export function useMyEntities(options?: Omit<UseQueryOptions<MyEntity[], ApiError>, 'queryKey' | 'queryFn'>) {
  return useQuery({
    queryKey: queryKeys.myEntities,
    queryFn: () => apiClient.getMyEntities(),
    staleTime: 30_000,
    ...options,
  });
}
```

### useMutation Pattern

Always invalidate relevant queries on success and show toast feedback:

```tsx
import { toast } from 'sonner';

const queryClient = useQueryClient();

const deleteMutation = useMutation({
  mutationFn: (id: string) => apiClient.deleteItem(id),
  onSuccess: () => {
    queryClient.invalidateQueries({ queryKey: queryKeys.items });
    toast.success('Item deleted');
  },
  onError: (error: ApiError) => {
    toast.error(`Failed to delete item: ${error.message}`);
  },
});
```

**Optimistic updates** (for core entities like printers):

```tsx
export function useCreatePrinter() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (dto: CreatePrinterDto) => apiClient.createPrinter(dto),
    onMutate: async (dto) => {
      await queryClient.cancelQueries({ queryKey: queryKeys.printers });
      const previous = queryClient.getQueryData<Printer[]>(queryKeys.printers);
      // Insert optimistic item...
      return { previous };
    },
    onError: (_err, _vars, ctx) => {
      if (ctx?.previous) queryClient.setQueryData(queryKeys.printers, ctx.previous);
      toast.error('Failed to create printer');
    },
    onSuccess: (created) => {
      toast.success(`Printer "${created.name}" created`);
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
    },
  });
}
```

### Query Invalidation Rules

- **Always invalidate on mutation success** — never rely on stale cache after writes
- **Invalidate related queries** — e.g., deleting a printer should invalidate both `queryKeys.printers` and `queryKeys.printerDetails(id)`
- **Use `onSettled`** for optimistic mutations (fires on both success and error) to ensure cache consistency
- **Prefer `invalidateQueries` over `setQueryData`** unless doing optimistic updates
- **Await invalidation in modal onSuccess** if closing the modal depends on fresh data:
  ```tsx
  onSuccess: async () => {
    await queryClient.invalidateQueries({ queryKey: queryKeys.items });
    onClose();
  }
  ```

## Toast Notifications

Use `sonner` for all user feedback:

```tsx
import { toast } from 'sonner';

toast.success('Printer created');
toast.error(`Failed to save: ${error.message}`);
toast.info('Discovery started');
```

- **Success**: Confirm the action with entity name when available
- **Error**: Start with "Failed to [action]" and append error detail
- **Info**: For informational/progress messages

## Form Handling

Forms use **controlled `useState`** — no react-hook-form:

```tsx
const [name, setName] = useState('');
const [description, setDescription] = useState('');

// Reset form when modal opens with edit data
useEffect(() => {
  if (isOpen && editItem) {
    setName(editItem.name);
    setDescription(editItem.description || '');
  }
}, [isOpen, editItem?.id]);

const handleSubmit = () => {
  if (!name.trim()) {
    toast.error('Name is required');
    return;
  }
  createMutation.mutate({ name: name.trim(), description: description.trim() });
};
```

- **Validation**: Inline checks before calling `mutation.mutate()`, with `toast.error` for validation messages
- **FormField + error prop**: For inline field-level errors:
  ```tsx
  <FormField label="Name" htmlFor="name" required error={nameError}>
    <Input id="name" value={name} onChange={(e) => setName(e.target.value)} invalid={!!nameError} />
  </FormField>
  ```

## Loading and Error States

### Page-Level Loading

```tsx
if (isLoading) return <PageTemplate title="Items"><Spinner size="lg" /></PageTemplate>;
```

Or use Skeleton components for structural loading:

```tsx
import { Skeleton } from '@/common/components/skeletons/Skeleton';
if (isLoading) return <Skeleton lines={5} />;
```

### Page-Level Errors

```tsx
if (error) {
  return (
    <PageTemplate title="Items">
      <div className="p-4 text-pf-error">Failed to load items: {String(error)}</div>
    </PageTemplate>
  );
}
```

### Button Loading State

```tsx
<Button loading={mutation.isPending} disabled={mutation.isPending}>
  Save
</Button>
```

## Services Layer

- **Central API client**: `src/services/api.ts` — `apiClient` singleton with all REST methods
- **Feature services**: Wrap specific API groups (e.g., `projectService`, `sliceJobService`)
- **Types**: All API types defined in `src/types/api.ts` and imported from `@/types/api`
- **SignalR**: Feature-specific services in `src/services/` (e.g., `printer-signalr.ts`, `harvest-signalr.ts`)

### API Client Rules

- **MANDATORY**: All API calls must use `apiClient` from `src/services/api.ts`
- **NEVER** create raw axios instances, use `fetch`, or direct HTTP calls
- **NEVER** manually add authentication headers — apiClient handles this automatically
- **NEVER** bypass apiClient for convenience — centralization is critical for:
  - Consistent auth token management (localStorage → Bearer header)
  - Automatic correlation ID tracking (request tracing)
  - Centralized 401 error handling (automatic logout)
  - Global timeout settings (30 seconds)
  - Error transformation and handling

```typescript
// ✅ CORRECT: Use apiClient directly
import { apiClient } from '@/services/api';
const printers = await apiClient.getPrinters();
const locations = await apiClient.getAllLocations();

// ✅ CORRECT: Use service wrappers (for caching/debouncing)
import { jobSchedulingService } from '@/services/jobSchedulingService';
const job = await jobSchedulingService.getScheduledJob(jobId);

// ❌ WRONG: Creating raw axios instances
const api = axios.create({ baseURL: '/api' }); // DON'T DO THIS

// ❌ WRONG: Using fetch for API calls
const response = await fetch('/api/printers', { ...options }); // DON'T DO THIS

// ❌ WRONG: Manual header management
axios.defaults.headers.common['Authorization'] = `Bearer ${token}`; // DON'T DO THIS
```

### Adding a New API Method

1. Add the method to `ApiClient` class in `src/services/api.ts`
2. Add/import the TypeScript types in `src/types/api.ts`
3. If shared across features, add a `useQuery`/`useMutation` hook in `src/common/hooks/useApi.ts`
4. If feature-specific, use `useQuery`/`useMutation` inline in the component
5. Service wrappers should delegate to apiClient, not make raw HTTP calls

## Backend Capabilities Pattern

For features that depend on backend support (e.g., filament control, movement, temperature):

```tsx
import { getPrinterSupport, canFilamentControl } from '@/features/printers/utils/printerSupport';

const support = getPrinterSupport(backendCapabilities);

// Show/hide UI sections based on capability
{support.supportsFilamentControl && (
  <section>
    <Button disabled={!canFilamentControl({ isOnline, isPrinting, support })}>
      Load Filament
    </Button>
  </section>
)}
```

- **Show/hide**: Use `support.supportsX` to conditionally render entire sections
- **Enable/disable**: Use `canX()` helpers that check online status + capability + printer state
- **Defaults**: Most capabilities default to `true` so controls aren't hidden while data loads; specialized capabilities (like filament control) default to `false`

## View Mode Persistence

For pages with cards/table toggle, use `localStorage`:

```tsx
import { useViewModePreference } from '@/common/hooks/useViewModePreference';

const [viewMode, setViewMode] = useViewModePreference('my-page', 'cards');
```

## Complete Component Example

```tsx
import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import clsx from 'clsx';
import { Button, Input, FormField, Card, Spinner } from '@/common/components/ui';
import { PlusIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import { apiClient } from '@/services/api';
import type { MyItem } from '@/types/api';

export function MyFeaturePage() {
  const queryClient = useQueryClient();
  const [newName, setNewName] = useState('');

  const { data: items = [], isLoading, error } = useQuery({
    queryKey: ['my-items'],
    queryFn: () => apiClient.getMyItems(),
    staleTime: 30_000,
  });

  const createMutation = useMutation({
    mutationFn: (name: string) => apiClient.createMyItem({ name }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['my-items'] });
      toast.success('Item created');
      setNewName('');
    },
    onError: (err: Error) => toast.error(`Failed to create: ${err.message}`),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => apiClient.deleteMyItem(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['my-items'] });
      toast.success('Item deleted');
    },
    onError: (err: Error) => toast.error(`Failed to delete: ${err.message}`),
  });

  if (isLoading) return <PageTemplate title="My Items"><Spinner size="lg" /></PageTemplate>;
  if (error) return <PageTemplate title="My Items"><div className="text-pf-error">Failed to load: {String(error)}</div></PageTemplate>;

  return (
    <PageTemplate title="My Items" icon={<PlusIcon />}>
      <div className="flex gap-2 mb-4">
        <FormField label="Name" htmlFor="new-name" required>
          <Input id="new-name" value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="Item name" />
        </FormField>
        <Button variant="primary" loading={createMutation.isPending} onClick={() => createMutation.mutate(newName.trim())} iconLeft={<PlusIcon />}>
          Add
        </Button>
      </div>
      <div className="grid gap-3">
        {items.map((item: MyItem) => (
          <Card key={item.id}>
            <Card.Body className="flex items-center justify-between">
              <span className="text-pf-text-primary">{item.name}</span>
              <Button variant="danger" size="sm" onClick={() => deleteMutation.mutate(item.id)} iconLeft={<DeleteIcon />}>
                Delete
              </Button>
            </Card.Body>
          </Card>
        ))}
      </div>
    </PageTemplate>
  );
}
```
