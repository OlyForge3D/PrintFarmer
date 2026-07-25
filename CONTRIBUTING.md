# Contributing to PrintFarmer

Thanks for your interest in contributing! This guide has common, practical instructions for a C#/.NET 10 solution with separate API backend and React frontend.

> If `.soft-freeze` exists at the repo root, a soft freeze is active. Only feature / test / doc changes should be made without an exception. See `SOFT_FREEZE.md` for restricted files and how to request an exception.

## Prerequisites
- .NET SDK 10.0 or later
- Git + a code editor (VS Code recommended)
- Optional (VS Code): Extensions "C#", "C# Dev Kit", and "Razor Language Server"

Verify your setup:
```powershell
# PowerShell
dotnet --info
```

## Repository layout
- `src/`
  - `api/` — ASP.NET Core API Server (standalone backend)
  - `backends/` — Backend plugin architecture (Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge, Core)
  - `Web/ReactApp/` — React TypeScript frontend
  - `shared/` — Shared DTOs/models
  - `farm-web.sln` — Solution file

## Restore and build
```powershell
# From repo root
cd ./src
# Restore
dotnet restore ./farm-web.sln
# Build (Debug)
dotnet build ./farm-web.sln -c Debug
# Build (Release)
dotnet build ./farm-web.sln -c Release
```

## Run (development)
Both API server and React frontend need to be run separately during development.

**API Server (Backend):**
```bash
# From repo root
cd ./src
# Run API Server
dotnet run --project ./api/Farm.Web.Api.csproj
```
API will be available at http://localhost:5245

**React Frontend (Frontend) - Run in separate terminal:**
```bash
# From repo root  
cd ./src/Web/ReactApp
npm run dev
```
Frontend will be available at http://localhost:3000

Stop both with Ctrl+C.

Tip: Use hot-reload/watch during active development:
```bash
# API server (first terminal)
cd ./src
dotnet watch --project ./api/Farm.Web.Api.csproj run

# React frontend (second terminal)
cd ./src/Web/ReactApp
npm run dev
```

## Tests
```bash
# From repo root
cd ./src
dotnet test ./farm-web.sln -c Debug

# Frontend tests
cd ./Web/ReactApp
npm run test:run
```

## Code style and formatting
- C#: follow conventional .NET style (PascalCase for types/members, camelCase for locals/params).
- TypeScript: Use camelCase for variables/functions, PascalCase for components/types, strict mode enabled
- Run formatter/analyzers locally:
```bash
# Format C# code in the solution
cd ./src
dotnet format ./farm-web.sln

# Lint React/TypeScript code
cd ./src/Web/ReactApp
npm run lint
```

## React 19 Patterns & Best Practices

PrintFarmer's React frontend uses modern React 19 patterns for improved code quality, type safety, and maintainability. When adding or modifying components, follow these established patterns.

### Pattern 1: Forms with useActionState + useFormStatus

**Use this pattern for any form submission** (login, registration, configuration, etc.)

**Benefits:**
- Automatic pending state without manual loading flags
- Built-in form data handling
- Cleaner error handling
- Type-safe form submissions

**Example:**
```typescript
import { useActionState } from 'react';
import { useFormStatus } from 'react-dom';

// Action function (can be async)
async function submitForm(prevState: FormState, formData: FormData): Promise<FormState> {
  const name = formData.get('name') as string;
  
  if (!name.trim()) {
    return { error: 'Name is required', success: false };
  }
  
  try {
    const result = await api.createItem({ name });
    return { success: true, data: result };
  } catch (error) {
    return { error: 'Failed to create item', success: false };
  }
}

// SubmitButton component (child of form)
function SubmitButton() {
  const { pending } = useFormStatus();
  return (
    <button type="submit" disabled={pending}>
      {pending ? 'Saving...' : 'Save'}
    </button>
  );
}

// Form component
function CreateItemForm() {
  const [state, formAction, isPending] = useActionState(submitForm, { 
    success: false, 
    error: null 
  });
  
  return (
    <form action={formAction}>
      <input name="name" type="text" required />
      {state.error && <p className="text-red-600">{state.error}</p>}
      {state.success && <p className="text-green-600">Item created!</p>}
      <SubmitButton />
    </form>
  );
}
```

**When to use:** Any form that submits data (Create, Edit, Delete, Login, Registration, Configuration).

### Pattern 2: Async Data Fetching with use() + Suspense

**Use this pattern to fetch data before rendering** (loading lists, details pages, etc.)

**Benefits:**
- Cleaner component logic without useEffect
- Automatic loading state via Suspense boundary
- Better code organization (fetch at parent level, render at child level)
- Type-safe promise unwrapping

**Example:**
```typescript
import { use, Suspense } from 'react';

// Async function that returns a promise
async function fetchPrinterDetails(printerId: string): Promise<Printer> {
  const response = await fetch(`/api/printers/${printerId}`);
  if (!response.ok) throw new Error('Failed to fetch printer');
  return response.json();
}

// Content component (receives unwrapped data)
function PrinterDetailsContent({ printer }: { printer: Printer }) {
  return (
    <div>
      <h2>{printer.name}</h2>
      <p>Status: {printer.status}</p>
      <p>Temperature: {printer.hotendTemp}°C</p>
    </div>
  );
}

// Parent component (handles Suspense boundary)
function PrinterDetailsModal({ printerId }: { printerId: string }) {
  const printerPromise = fetchPrinterDetails(printerId);
  
  return (
    <Suspense fallback={<div>Loading printer details...</div>}>
      {/* Content component unwraps the promise with use() hook */}
      <PrinterDetailsContent printer={use(printerPromise)} />
    </Suspense>
  );
}

// Inside PrinterDetailsContent, unwrap the promise:
function PrinterDetailsContent({ printerPromise }: { printerPromise: Promise<Printer> }) {
  // use() suspends rendering until promise resolves
  const printer = use(printerPromise);
  
  return (
    <div>
      <h2>{printer.name}</h2>
      {/* ... rest of component ... */}
    </div>
  );
}
```

**When to use:** Any component that needs to load data before rendering (modals, detail pages, lists).

### Pattern 3: Conditional Component Visibility with Activity (React 19.2)

**Use this pattern for persistent component state during navigation** (tabs, multi-step forms, etc.)

**Benefits:**
- Component state preserved when hidden
- Smooth transitions between states
- No re-initialization when switching tabs

**Example:**
```typescript
import { Activity, useState } from 'react';

function TabPanel() {
  const [activeTab, setActiveTab] = useState<'overview' | 'details' | 'settings'>('overview');
  
  return (
    <div>
      <nav>
        <button onClick={() => setActiveTab('overview')}>Overview</button>
        <button onClick={() => setActiveTab('details')}>Details</button>
        <button onClick={() => setActiveTab('settings')}>Settings</button>
      </nav>
      
      {/* Activity preserves component state when hidden */}
      <Activity mode={activeTab === 'overview' ? 'visible' : 'hidden'}>
        <OverviewTab />
      </Activity>
      
      <Activity mode={activeTab === 'details' ? 'visible' : 'hidden'}>
        <DetailsTab />
      </Activity>
      
      <Activity mode={activeTab === 'settings' ? 'visible' : 'hidden'}>
        <SettingsTab />
      </Activity>
    </div>
  );
}
```

**When to use:** Multi-tab interfaces, step-by-step wizards, or any tabbed layout where component state should persist.

### Pattern 4: Optimistic UI Updates with useOptimistic

**Use this pattern for immediate feedback during async operations** (deleting items, toggling states, etc.)

**Benefits:**
- Instant UI feedback without waiting for server
- Automatic rollback on error
- Better perceived performance

**Example:**
```typescript
import { useState, useOptimistic, useTransition } from 'react';

function PrinterList({ initialPrinters }: { initialPrinters: Printer[] }) {
  const [printers, setPrinters] = useState(initialPrinters);
  const [optimisticPrinters, addOptimisticUpdate] = useOptimistic(
    printers,
    (state, newPrinter) => [...state, newPrinter]
  );
  const [isPending, startTransition] = useTransition();
  
  const handleDelete = async (id: string) => {
    // Optimistically remove from UI
    addOptimisticUpdate({ id } as Printer);
    
    startTransition(async () => {
      try {
        await api.deletePrinter(id);
        // Server confirmed - update state
        setPrinters(printers.filter(p => p.id !== id));
      } catch (error) {
        // Error - optimistic update rolls back automatically
        console.error('Failed to delete printer');
      }
    });
  };
  
  return (
    <ul>
      {optimisticPrinters.map(printer => (
        <li key={printer.id}>
          {printer.name}
          <button onClick={() => handleDelete(printer.id)} disabled={isPending}>
            Delete
          </button>
        </li>
      ))}
    </ul>
  );
}
```

**When to use:** Operations that modify lists (add, delete, update), toggle states, or any action where instant feedback improves UX.

### Anti-Patterns to Avoid

❌ **Don't use `useEffect` for data fetching:**
```typescript
// BAD - old pattern
useEffect(() => {
  fetch(`/api/printers/${id}`)
    .then(r => r.json())
    .then(data => setPrinter(data))
    .catch(err => setError(err));
}, [id]);
```

✅ **Do use `use()` + Suspense instead:**
```typescript
// GOOD - new pattern
const printer = use(fetchPrinter(id));
```

❌ **Don't manage form state manually:**
```typescript
// BAD - lots of state management
const [formData, setFormData] = useState({ name: '', email: '' });
const [isSubmitting, setIsSubmitting] = useState(false);
const [error, setError] = useState(null);
```

✅ **Do use `useActionState`:**
```typescript
// GOOD - clean and automatic
const [state, formAction] = useActionState(submitForm, initialState);
```

### TypeScript Guidelines for React 19

- **Always type props interfaces explicitly:**
  ```typescript
  interface ComponentProps {
    title: string;
    onClose: () => void;
    data?: Printer[];
  }
  ```

- **Use proper return types for async functions:**
  ```typescript
  async function fetchData(id: string): Promise<Data> {
    // ...
  }
  ```

- **Type action functions correctly:**
  ```typescript
  async function handleSubmit(prevState: FormState, formData: FormData): Promise<FormState> {
    // ...
  }
  ```

- **Use discriminated unions for state:**
  ```typescript
  type FormState = 
    | { status: 'idle'; error: null }
    | { status: 'loading'; error: null }
    | { status: 'success'; data: Data; error: null }
    | { status: 'error'; error: string };
  ```

## Tag Management System

PrintFarmer includes a comprehensive **polymorphic tagging system** for organizing 3D models and G-code files. See `docs/TAGGING_SYSTEM.md` for complete details.

### Quick Tag System Overview

**For End Users:**
- Navigate to `Admin → Tag Management` to create/manage tags
- Edit model tags in the Models page detail view
- Use bulk tagging for multiple models at once
- View tag analytics in the `Admin → Tag Management → Analytics` tab

**For Developers Adding Tags to New Objects:**

1. **Create Mapping Entity** (e.g., `PrinterTag` for tagging printers):
   ```csharp
   public class PrinterTag
   {
       public Guid PrinterId { get; set; }
       public Guid TagId { get; set; }
       public DateTime TaggedAt { get; set; }
       
       public Printer Printer { get; set; }
       public Tag Tag { get; set; }
   }
   ```

2. **Configure in DbContext:**
   ```csharp
   modelBuilder.Entity<PrinterTag>()
       .HasKey(pt => new { pt.PrinterId, pt.TagId });
   
   modelBuilder.Entity<PrinterTag>()
       .HasOne(pt => pt.Printer)
       .WithMany(p => p.TagMappings)
       .HasForeignKey(pt => pt.PrinterId)
       .OnDelete(DeleteBehavior.Cascade);
   
   modelBuilder.Entity<PrinterTag>()
       .HasOne(pt => pt.Tag)
       .WithMany()
       .HasForeignKey(pt => pt.TagId)
       .OnDelete(DeleteBehavior.Cascade);
   ```

3. **Add Repository Interface:**
   ```csharp
   public interface IPrinterTagRepository
   {
       Task<IEnumerable<Tag>> GetTagsForPrinterAsync(Guid printerId);
       Task AddTagToPrinterAsync(Guid printerId, Guid tagId);
       Task RemoveTagFromPrinterAsync(Guid printerId, Guid tagId);
   }
   ```

4. **Add Controller Endpoints:**
   ```csharp
   [HttpPost("{printerId}/tags/{tagId}")]
   public async Task<IActionResult> AddTag(Guid printerId, Guid tagId)
   {
       await _printerTagRepository.AddTagToPrinterAsync(printerId, tagId);
       return Ok();
   }
   ```

5. **Test thoroughly** and update documentation.

**Tag Name Normalization:**
- All tag names are automatically normalized to PascalCase
- "my-tag" → "MyTag", "MY_TAG" → "MyTag", "my tag" → "MyTag"
- Ensures consistency and prevents duplicate tags with different cases

**Polymorphic Architecture Benefits:**
- One `Tag` entity shared across all object types (3D Models, G-code Files, Printers, etc.)
- Single tag management interface for all taggable objects
- Efficient queries with type-specific mapping tables
- Easy to extend to new object types

For comprehensive guidance, see `docs/TAGGING_SYSTEM.md` which includes:
- Full architecture documentation
- User workflows (creating, assigning, bulk tagging)
- API endpoint reference
- Component guide
- Developer guide for extending the system

## EF Core and SQLite
- The application includes startup safety for SQLite to ease local development.
- Migrations are not required for typical dev runs; the app will bring the local DB to a usable state on startup.

## Git and PRs
- Create feature branches from `main`.
- Keep PRs small and focused; include a short summary of changes and any notes for reviewers.
- Ensure builds and tests pass locally before opening a PR.

## Troubleshooting
- Locked files during build (Windows): close any running app instances that might hold `bin/`/`obj/` outputs.
- Clean rebuild:
```powershell
# From repo root
rd /s /q .\src\client\bin; rd /s /q .\src\client\obj
rd /s /q .\src\api\bin; rd /s /q .\src\api\obj
rd /s /q .\src\shared\bin; rd /s /q .\src\shared\obj
cd .\src; dotnet restore .\farm-web.sln; dotnet build .\farm-web.sln -c Debug
```
- If ports are occupied, change the launch profile URLs in `api/Properties/launchSettings.json` or set `ASPNETCORE_URLS`.

## Questions
Open a discussion or an issue with clear repro steps, logs, and environment info (`dotnet --info`).
