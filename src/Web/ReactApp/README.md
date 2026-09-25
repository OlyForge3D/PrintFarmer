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

### Typecheck gates

Run `npm run ci:typecheck-app`, `npm run ci:typecheck-tests`, and
`npm run ci:typecheck-src-coverage` before submitting changes. These commands
exercise the gate harnesses and run their compiler or source-coverage checks.
Both compiler gates require zero diagnostics; replacing one error with another
cannot pass, and fixing all errors requires no diagnostic-baseline update.
Keep the file-coverage floors and application `@ts-nocheck` guard intact.
See [testing patterns](../../../docs/TESTING_PATTERNS.md) for the policy.

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

### Random IDs and HTTP LAN deployments

All application-generated random IDs share `generateUUID` from `@/utils/uuid`,
including upload queue entries, temporary aliases, harvest rows, slicer text
models, correlation IDs, and idempotency/operation keys. See the [shared UUID policy](./eslint-rules/README.md#shared-uuid-generation)
for the HTTP LAN compatibility convention and lint guard.

The helper throws if neither secure random source exists. Keep generation inside
the caller's existing mutation, submit error handling, or render error boundary;
never silently substitute a timestamp/`Math.random()` ID.

Generate at the existing logical-operation boundary, not on each retry. Preserve
saved keys verbatim, including older non-UUID keys; do not migrate or regenerate
them. Harvest retries reuse the dialog's key, unchanged stock-adjustment retries
reuse the cached payload key, and bed-clear retries reuse the key stored for the
reviewed job/ETags. Correlation IDs remain fresh per HTTP request (including
streaming export requests), independently of operation idempotency keys. UI row
and temporary IDs remain local and are omitted from request payloads as before.
Color selection and retry jitter are non-ID randomness and still use
`Math.random()`; deterministic identifiers and React `useId()` are unchanged.

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

### Direct printer movement

All printer plugins use the ordinary `/api/printers/{id}/home`, `homexy`,
`homez`, `move`, and `moveto` routes for homing, relative jogs, absolute
positioning, and calibration. Existing backend capabilities, online state,
homing checks, and travel protections still govern availability.
GO and Enter submit one absolute request with finite X, Y, and Z targets.
Calibration Z adjustments send only the selected Z target.

Controls for the same printer share a pending-request guard. It lasts only
until the direct HTTP request settles; it is not a physical-completion gate.
There is no motion-status panel, durable operation ID, receipt journal, recovery
workflow, status polling, or reconnect replay. A successful `CommandResult`
means the command was accepted, not that the printer physically completed it.
Calibration explicitly asks the operator to wait for the printer to stop and
verify its position before continuing.

HTTP errors, timeouts, aborts, and unsuccessful command results surface errors
without automatically retrying physical commands. Check the printer before
manually sending another command after an uncertain response.

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
