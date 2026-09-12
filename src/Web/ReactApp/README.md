# PrintFarmer React Application

This is the React TypeScript frontend for PrintFarmer, a dashboard for managing multiple 3D printers. Built with React 18, TypeScript, Vite, and Tailwind CSS.

## Quick Start

```bash
# Install dependencies
npm install

# Start development server
npm run dev

# Build for production
npm run build

# Run tests
npm test

# Lint code
npm run lint
```

## Documentation

- **[UI Components Guide](./UI_COMPONENTS_GUIDE.md)** - Comprehensive guide to shared UI components (Button, Alert, FormField, Input, Select, ProgressBar) with usage examples
- **[Color System Guide](./COLOR_SYSTEM_GUIDE.md)** - PrintFarmer design token system and accessibility guidelines

## Technology Stack

- **React 18** - Modern React with hooks and concurrent features
- **TypeScript** - Type-safe JavaScript
- **Vite** - Fast build tool with HMR (Hot Module Replacement)
- **Tailwind CSS** - Utility-first CSS with custom PrintFarmer design tokens
- **React Query** - Server state management
- **SignalR** - Real-time communication with API
- **React Router** - Client-side routing
- **Vitest** - Unit testing with React Testing Library

## Project Structure

```
src/
├── components/        # React components
│   └── ui/           # Shared UI component library
├── contexts/         # React contexts (Auth, Theme, etc.)
├── pages/            # Page components
├── services/         # API clients and services
├── types/            # TypeScript type definitions
├── utils/            # Utility functions
├── test/             # Test files
└── styles/           # Global styles and theme
```

## Development Guidelines

### Using Shared Components

Always use shared components from `components/ui/` for consistency:

```tsx
import { Button } from '@/components/ui/Button';
import { Alert } from '@/components/ui/Alert';
import { FormField } from '@/components/ui/FormField';
import { Input } from '@/components/ui/Input';

function MyForm() {
  return (
    <form>
      <FormField label="Email" required>
        <Input type="email" value={email} onChange={handleChange} />
      </FormField>
      
      <Button variant="primary" type="submit">
        Submit
      </Button>
    </form>
  );
}
```

See [UI_COMPONENTS_GUIDE.md](./UI_COMPONENTS_GUIDE.md) for complete component documentation.

### Design System

PrintFarmer uses a comprehensive color token system with `pf-*` prefixed classes:

```tsx
// ✅ DO: Use design tokens
<div className="bg-pf-panel text-pf-text-primary border border-pf-border">
  <h2 className="text-pf-text-primary">Title</h2>
  <p className="text-pf-text-secondary">Description</p>
</div>

// ❌ DON'T: Use raw Tailwind colors
<div className="bg-white text-gray-900 border border-gray-300">
  <h2 className="text-gray-900">Title</h2>
  <p className="text-gray-600">Description</p>
</div>
```

See [COLOR_SYSTEM_GUIDE.md](./COLOR_SYSTEM_GUIDE.md) for complete color token reference.

#### Theme safety ratchet

`src/test/features/admin/AdminThemeSafety.test.ts` validates production TS,
TSX, and CSS theme references. It covers ordinary `*-pf-*` utilities without a
colour-prefix enumeration, variants, important and negative markers, opacity
modifiers, direct `var(--pf-*)` and `var(--color-pf-*)` references, arbitrary
values such as `bg-[var(--pf-card-bg)]`, and nested forms such as
`ring-offset-[var(--pf-focus-ring-offset)]`. Aliases are followed transitively
in an isolated resolution graph for each theme, and failures report the
relative file, line, column, token, source syntax, and affected themes.

CSS custom-property declarations are definitions, not usages. Runtime-assigned
raw properties require an exact file-and-token allowance with a rationale.
Dynamic custom-property names assembled across template expressions and class
names with no literal `pf-` segment are not inferred; keep a literal token
segment or document the runtime bridge in the scanner allowlist.

### Code Style

- **TypeScript**: Strict mode enabled with comprehensive type checking
- **ESLint**: Configured for React + TypeScript best practices
- **Prettier**: (Future) Automatic code formatting

### Testing

```bash
# Run all tests
npm test

# Run tests in watch mode
npm test -- --watch

# Run tests with coverage
npm test -- --coverage
```

Tests use Vitest and React Testing Library. See `src/test/` for examples.

## API Integration

### Durable Moonraker motion

Home All/XY/Z, relative jogs, absolute positioning, and calibration use durable
`/api/printers/{id}/control-operations` records. HTTP 202 means admission, not
completion, and must contain an unresolved operation. HTTP 200 admission replay
must contain a valid terminal receipt for that same operation ID; mismatched
status/state combinations, malformed receipts, and other successful HTTP statuses
are rejected without confirming admission. Even valid terminal POST receipts
still require canonical GET/current checks before success or release.
Receipts associated with a locally saved UUID must also match its original kind,
X/Y/Z, and feed rate on admission, canonical reads, and recovery. Omitted request
coordinates match null receipt values; the saved request is never rewritten.
An intent mismatch preserves the journal and keeps motion uncertain and locked.
The UI keeps motion locked until REST confirms the outcome and
rechecks the current operation for a successor. Position telemetry, homed axes,
and elapsed time never establish completion.
The current endpoint identifies only a barrier owner; when unlocked its operation,
operation ID, and state are null. Settled receipts are read by their exact UUID.

Before submission, the client saves the operation UUID and intent in local
storage scoped to the authenticated account, API server, and printer. Closing
a panel or navigating away does not cancel backend work. Reopening, returning
to the foreground, or reconnecting rechecks REST. SignalR's lowercase
`printercontroloperationupdated` event is an invalidation hint, not an outcome.
While an operation or saved admission remains unresolved, a shared two-second
read-only REST polling cadence also runs even when SignalR is connected; missed
notifications cannot leave it waiting forever. Overlapping polls reuse the
in-flight read, and timers never release barriers or replay physical commands.
Unavailable status keeps motion locked. A lost admission response never triggers
a new UUID or a legacy fallback; an explicitly offered admission retry reuses
the saved UUID and intent. Older servers must be updated for Moonraker motion.
That deliberate retry can admit and start physical motion if the server never
admitted the original request; operation recovery cannot resolve a client-only
receipt. The review shows the exact saved intent and requires confirmation;
declining leaves motion blocked. Before re-submission, REST status, session
authority, and the unchanged saved receipt are rechecked. Confirmed admission is
persisted, so later 404 responses cannot offer a known Unknown/Recovering execution
for re-submission. A missing operation response never discards the saved UUID.
Other printer backends retain their existing motion endpoints.
Moonraker absolute GO/Enter requires finite X, Y, and Z coordinates together;
non-Moonraker partial-axis controls retain their existing behavior. Calibration
Z adjustments retain the explicitly selected bed-center X/Y target.

Unknown outcomes require operator recovery. The motion panel explains required
farm-administrator role, `queue:reconcile` permission, and printer Submit access
(administrator permission bypass applies). Nonadmin queue reconcilers can view
status but are not offered recovery actions. It also explains prior-sender
isolation, controller-queue clearance, and physical
stationarity. Recovery completion requires separate, initially unchecked
attestations and written evidence, using the exact reviewed GET ETag. No stop,
reset, or hardware command is sent by the recovery form. `Recovered` is not
successful execution; calibration advances only after confirmed success and
fresh printer safety checks. It reads enabled/maintenance configuration from
the printer list and fact-specific `safetyTelemetry.homedAxes` from
`/api/printers/{id}/status`, not absent fields on basic printer GET or the legacy
homed-axes string. The observation must contain all three axes, be no older than
the completed operation, and satisfy its positive freshness window without a
future timestamp. The current-operation barrier is checked again before advancing.

The React app communicates with the ASP.NET Core API backend using a centralized **apiClient** singleton:

- **API Base URL**: `http://localhost:5245`
- **SignalR Hub**: `/hubs/printers`
- **REST Endpoints**: `/api/printers`, `/api/catalog`, etc.

### Using apiClient (Required Pattern)

All API communication **must** go through `apiClient` from `src/services/api.ts`. This ensures:
- ✅ **Authentication**: Bearer token automatically added to all requests
- ✅ **Correlation IDs**: X-Correlation-Id header automatically added for request tracing
- ✅ **Error Handling**: Centralized 401/error handling with automatic logout on auth failures
- ✅ **Request Timeout**: 30-second timeout configured globally

**Example:**

```typescript
import { apiClient } from '@/services/api';

// Direct usage (recommended for simple calls)
const printers = await apiClient.getPrinters();

// Through service wrapper (for caching/debouncing)
import { jobSchedulingService } from '@/services/jobSchedulingService';
const scheduled = await jobSchedulingService.getScheduledJob(jobId);
```

**DO NOT create raw axios instances or use fetch for API calls.** All services delegate to `apiClient`:
- ✗ Don't: `axios.get('/api/printers')`
- ✗ Don't: `fetch('/api/printers').then(...)`
- ✅ Do: `apiClient.getPrinters()`

See `src/services/` for all available API methods and service wrappers.

## Deployment

```bash
# Build for production
npm run build

# Preview production build locally
npm run preview
```

The production build outputs to `dist/` and is served by the ASP.NET Core backend.

## Contributing

See [CONTRIBUTING.md](../../../CONTRIBUTING.md) for development guidelines.

## Migration from Blazor

This React application is the replacement for the legacy Blazor WebAssembly client. See [REACT_MIGRATION_README.md](../../../REACT_MIGRATION_README.md) for migration details and status.
