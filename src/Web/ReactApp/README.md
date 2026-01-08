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

The React app communicates with the ASP.NET Core API backend:

- **API Base URL**: `http://localhost:5245`
- **SignalR Hub**: `/hubs/printers`
- **REST Endpoints**: `/api/printers`, `/api/catalog`, etc.

See `src/services/` for API client implementations.

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
