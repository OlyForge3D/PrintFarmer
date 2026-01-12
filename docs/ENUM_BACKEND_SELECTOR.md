# Enum-Based Backend Selector

## Overview
The backend selection dropdowns are now dynamically populated from the `PrinterBackend` enum definition. This ensures that when new backends are added to the system, they automatically appear in all UI dropdowns without requiring manual updates to multiple components.

## Architecture

### 1. Enum Definition (`src/Web/ReactApp/src/types/api.ts`)
```typescript
export enum PrinterBackend {
  Unknown = 0,
  Moonraker = 1,
  PrusaLink = 2,
  SDCP = 3,
  OctoPrint = 4
}
```

### 2. Enum Helpers (`src/Web/ReactApp/src/utils/enumHelpers.ts`)
Utility functions that convert TypeScript enums into UI-friendly formats:

- **`getPrinterBackendOptions()`**: Returns array of `{ value: number, label: string }` for all backends
- **`getPrinterBackendName(backend)`**: Converts enum value to display name
- **`getMotionTypeOptions()`**: Returns array for MotionType enum
- **`getMotionTypeName(motionType)`**: Converts MotionType to display name

### 3. Reusable Component (`src/Web/ReactApp/src/components/BackendSelector.tsx`)
A dedicated component that encapsulates backend selection logic:

```tsx
<BackendSelector
  value={formData.backend}
  onChange={(backend) => handleInputChange('backend', backend)}
  className="..."
  title="Printer backend"
/>
```

**Props:**
- `value`: Current PrinterBackend enum value (or undefined)
- `onChange`: Callback receiving PrinterBackend or undefined
- `className`: CSS classes for styling
- `placeholder`: Custom placeholder text (default: "Select backend...")
- `required`: Whether selection is required (hides placeholder if true)
- `disabled`: Whether dropdown is disabled
- `title`: Tooltip/accessibility title

## Usage

### In Components
```tsx
import { BackendSelector } from './BackendSelector';

// In your form:
<BackendSelector
  value={formData.backend}
  onChange={(backend) => setFormData({ ...formData, backend })}
  className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border"
/>
```

### Manual Usage (without component)
```tsx
import { getPrinterBackendOptions } from '@/utils/enumHelpers';

<select value={backend ?? ''} onChange={handleChange}>
  <option value="">Select backend...</option>
  {getPrinterBackendOptions().map(option => (
    <option key={option.value} value={option.value}>
      {option.label}
    </option>
  ))}
</select>
```

## Components Using BackendSelector

1. **EditPrinterModal** - Backend selection when editing/creating printers
2. **EditModelModal** - Default backend selection for printer models

## Adding New Backends

To add a new backend (e.g., "Repetier"):

1. **Update C# enum** (`src/shared/Models.cs`):
```csharp
public enum PrinterBackend
{
    Unknown = 0,
    Moonraker = 1,
    PrusaLink = 2,
    SDCP = 3,
    OctoPrint = 4,
    Repetier = 5  // NEW
}
```

2. **Update TypeScript enum** (`src/Web/ReactApp/src/types/api.ts`):
```typescript
export enum PrinterBackend {
  Unknown = 0,
  Moonraker = 1,
  PrusaLink = 2,
  SDCP = 3,
  OctoPrint = 4,
  Repetier = 5  // NEW - automatically appears in all dropdowns!
}
```

3. **That's it!** All UI dropdowns automatically include the new backend.

## Benefits

✅ **Single Source of Truth**: Enum definition drives all UI options  
✅ **Type Safety**: TypeScript ensures backend values are valid  
✅ **Maintainability**: Add backends in one place, appear everywhere  
✅ **Consistency**: Same options across all forms and filters  
✅ **Testability**: Enum helpers have unit tests

## Testing

Run tests to verify enum helpers work correctly:
```bash
cd src/Web/ReactApp
npm test enumHelpers
```

Tests verify:
- All enum values are converted to options
- Only numeric values are included (filters out reverse mappings)
- Name conversion works correctly
- Undefined handling works as expected
