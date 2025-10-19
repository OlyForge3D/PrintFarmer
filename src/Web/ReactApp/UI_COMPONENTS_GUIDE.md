# PrintFarmer Shared UI Components Guide

## Overview

PrintFarmer uses a standardized set of shared UI components built with React and TypeScript. All components follow the PrintFarmer design system using `pf-*` color tokens defined in `tailwind.config.js` for consistent theming and dynamic theme support.

## Location

All shared UI components are located in `/src/components/ui/`.

## Design System Integration

Components use CSS custom properties via the `pf-*` naming convention:
- **Backgrounds**: `pf-bg-0`, `pf-bg-1`, `pf-bg-2`, `pf-panel`
- **Text**: `pf-text-primary`, `pf-text-secondary`, `pf-text-muted`
- **Borders**: `pf-border`, `pf-border-light`, `pf-border-medium`
- **Accents**: `pf-accent`, `pf-success`, `pf-error`, `pf-warning`
- **Status**: `pf-status-online-*`, `pf-status-offline-*`

See `/src/Web/ReactApp/COLOR_SYSTEM_GUIDE.md` for complete color token reference.

---

## Components

### Button

Standardized button component with consistent variants, sizing, and focus states.

**Location**: `src/components/ui/Button.tsx`

**Usage**:
```tsx
import { Button } from '@/components/ui/Button';

// Primary action button
<Button variant="primary" onClick={handleSubmit}>
  Submit
</Button>

// Secondary/cancel button
<Button variant="secondary" onClick={handleCancel}>
  Cancel
</Button>

// Danger/destructive action
<Button variant="danger" onClick={handleDelete}>
  Delete
</Button>

// Success action
<Button variant="success" onClick={handleApprove}>
  Approve
</Button>

// Subtle/ghost button
<Button variant="subtle" onClick={handleMinor}>
  View Details
</Button>

// With loading state
<Button variant="primary" loading={isSubmitting}>
  Save
</Button>

// Small size
<Button variant="primary" size="sm">
  Quick Action
</Button>

// With icons
<Button iconLeft={<SaveIcon />} variant="primary">
  Save Changes
</Button>
```

**Props**:
- `variant?: 'primary' | 'secondary' | 'danger' | 'subtle' | 'success'` (default: `'primary'`)
- `size?: 'sm' | 'md'` (default: `'md'`)
- `loading?: boolean` - Shows loading text and disables button
- `iconLeft?: React.ReactNode` - Icon before text
- `iconRight?: React.ReactNode` - Icon after text
- All standard `HTMLButtonElement` attributes

**Styling**:
- Uses `pf-gradient-*` for primary/secondary/success variants
- Uses `pf-error` for danger variant
- Uses `pf-accent` for focus ring
- Automatically handles disabled states with `pf-disabled`

---

### Alert

Message display component for success, error, info, and warning notifications.

**Location**: `src/components/ui/Alert.tsx`

**Usage**:
```tsx
import { Alert } from '@/components/ui/Alert';

// Success message
<Alert type="success">
  Profile imported successfully!
</Alert>

// Error message
<Alert type="error">
  Failed to submit job. Please try again.
</Alert>

// Info message
<Alert type="info" title="Note">
  This operation may take a few moments.
</Alert>

// Warning message
<Alert type="warning">
  This action cannot be undone.
</Alert>

// With dismiss button
<Alert type="success" onClose={() => setMessage(null)}>
  Changes saved.
</Alert>
```

**Props**:
- `type?: 'success' | 'error' | 'info' | 'warning'` (default: `'info'`)
- `title?: string` - Optional bold title above message
- `children: React.ReactNode` - Alert message content
- `className?: string` - Additional classes
- `onClose?: () => void` - If provided, shows dismiss button

**Styling**:
- Uses `pf-success-bg` / `pf-error-bg` / `pf-accent-bg` / `pf-warning` for backgrounds
- Uses `pf-error-text` for error messages
- Applies `role="alert"` for errors (accessibility)

---

### FormField

Wrapper component for consistent form field layout with labels, helpers, and error messages.

**Location**: `src/components/ui/FormField.tsx`

**Usage**:
```tsx
import { FormField } from '@/components/ui/FormField';
import { Input } from '@/components/ui/Input';

// Basic field
<FormField label="Username" required>
  <Input type="text" value={username} onChange={e => setUsername(e.target.value)} />
</FormField>

// With helper text
<FormField 
  label="Email" 
  helper="We'll never share your email with anyone."
>
  <Input type="email" value={email} onChange={e => setEmail(e.target.value)} />
</FormField>

// With error message
<FormField 
  label="Password" 
  error={passwordError}
  required
>
  <Input 
    type="password" 
    value={password} 
    onChange={e => setPassword(e.target.value)}
    invalid={!!passwordError}
  />
</FormField>

// Inline layout (for checkboxes, etc.)
<FormField 
  label="Use Model Picker" 
  helper="Select from uploaded models" 
  inline
>
  <input type="checkbox" checked={usePicker} onChange={e => setUsePicker(e.target.checked)} />
</FormField>
```

**Props**:
- `label?: string` - Field label
- `htmlFor?: string` - Associates label with input via ID
- `helper?: string | React.ReactNode` - Helper text (hidden when error present)
- `error?: string | React.ReactNode` - Error message (overrides helper)
- `children: React.ReactNode` - Input/control element
- `required?: boolean` - Shows asterisk in label
- `className?: string` - Additional wrapper classes
- `inline?: boolean` - Side-by-side layout (label + control)

**Styling**:
- Labels use `pf-text-primary`
- Helper text uses `pf-text-muted`
- Error messages use `pf-error-text` with `role="alert"`

---

### Input

Standardized text input with consistent focus states and validation styling.

**Location**: `src/components/ui/Input.tsx`

**Usage**:
```tsx
import { Input } from '@/components/ui/Input';

// Standard text input
<Input 
  type="text" 
  value={name} 
  onChange={e => setName(e.target.value)}
  placeholder="Enter name"
/>

// With validation error
<Input 
  type="email" 
  value={email} 
  onChange={e => setEmail(e.target.value)}
  invalid={emailError}
/>

// Disabled state
<Input 
  type="text" 
  value={readOnlyValue} 
  disabled
/>

// Number input
<Input 
  type="number" 
  value={quantity} 
  onChange={e => setQuantity(Number(e.target.value))}
  min={1}
  max={100}
/>
```

**Props**:
- `invalid?: boolean` - Applies error styling
- All standard `HTMLInputElement` attributes (`type`, `value`, `onChange`, `disabled`, etc.)

**Styling**:
- Background: `pf-bg-0`
- Text: `pf-text-primary`
- Border: `pf-border` (normal), `pf-error` (invalid)
- Focus ring: `pf-accent` (normal), `pf-error` (invalid)
- Disabled: `pf-disabled`

---

### Select

Standardized select dropdown with consistent focus states and validation styling.

**Location**: `src/components/ui/Select.tsx`

**Usage**:
```tsx
import { Select } from '@/components/ui/Select';

// Basic dropdown
<Select 
  value={engine} 
  onChange={e => setEngine(Number(e.target.value))}
  aria-label="Slicer engine"
>
  <option value={0}>OrcaSlicer</option>
  <option value={1}>PrusaSlicer</option>
  <option value={2}>Cura</option>
</Select>

// With validation error
<Select 
  value={priority} 
  onChange={e => setPriority(e.target.value)}
  invalid={priorityError}
>
  <option value="">-- Select priority --</option>
  <option value="low">Low</option>
  <option value="normal">Normal</option>
  <option value="high">High</option>
</Select>

// Disabled state
<Select disabled aria-label="No options available">
  <option>-- No profiles --</option>
</Select>
```

**Props**:
- `invalid?: boolean` - Applies error styling
- All standard `HTMLSelectElement` attributes (`value`, `onChange`, `disabled`, etc.)

**Styling**:
- Same token usage as `Input` for consistency
- Background: `pf-bg-0`
- Text: `pf-text-primary`
- Border: `pf-border` (normal), `pf-error` (invalid)
- Focus ring: `pf-accent` (normal), `pf-error` (invalid)
- Disabled: `pf-disabled`

---

### ProgressBar

Progress indicator with configurable size, color, and label.

**Location**: `src/components/ui/ProgressBar.tsx`

**Usage**:
```tsx
import { ProgressBar } from '@/components/ui/ProgressBar';

// Basic progress bar
<ProgressBar value={45} />

// With custom label
<ProgressBar 
  value={progressPercent} 
  label={`ETA: ${estimatedTime}`}
/>

// Without percentage display
<ProgressBar 
  value={uploadProgress} 
  showPercent={false}
/>

// Custom size
<ProgressBar 
  value={75} 
  size="md"
/>

// Custom color
<ProgressBar 
  value={100} 
  color="green" 
  label="Complete!"
/>

// Error state
<ProgressBar 
  value={30} 
  color="red" 
  label="Upload failed"
/>
```

**Props**:
- `value: number` - Progress value (0-100)
- `label?: string` - Custom label text
- `size?: 'xs' | 'sm' | 'md'` (default: `'sm'`)
- `color?: 'blue' | 'green' | 'purple' | 'red' | 'gray'` (default: `'blue'`)
- `showPercent?: boolean` - Display percentage (default: `true`)
- `animated?: boolean` - Enable animation (default: `true`)
- `className?: string` - Additional classes

**Styling**:
- Track background: `pf-bg-1`
- Label text: `pf-text-secondary`
- Fill colors map to `pf-accent`, `pf-success`, `pf-error`, etc.
- Uses `data-width` attribute with CSS module for width (no inline styles)

---

## Best Practices

### 1. Always Use Shared Components
Replace raw `<button>`, `<input>`, `<select>` elements with shared components for consistency:

❌ **Don't**:
```tsx
<button className="px-4 py-2 bg-blue-600 text-white rounded">
  Submit
</button>
```

✅ **Do**:
```tsx
<Button variant="primary">Submit</Button>
```

### 2. Combine with FormField
Wrap inputs/selects with `FormField` for consistent layout:

```tsx
<FormField label="Email" helper="Required for notifications" required>
  <Input type="email" value={email} onChange={handleChange} />
</FormField>
```

### 3. Use Alert for User Feedback
Replace custom error/success messages with `Alert`:

```tsx
{error && <Alert type="error">{error}</Alert>}
{success && <Alert type="success">{success}</Alert>}
```

### 4. Leverage Loading States
Use `loading` prop instead of custom spinner logic:

```tsx
<Button variant="primary" loading={isSubmitting}>
  {isSubmitting ? 'Saving...' : 'Save Changes'}
</Button>
```

### 5. Maintain Accessibility
Always provide `aria-label` or associated labels:

```tsx
<Select aria-label="Priority level" value={priority} onChange={handleChange}>
  <option value={0}>Low</option>
  <option value={1}>Normal</option>
</Select>
```

### 6. Use pf-* Tokens for Custom Styles
When adding custom styles, use PrintFarmer color tokens:

```tsx
<div className="bg-pf-panel border border-pf-border rounded p-4">
  <h3 className="text-pf-text-primary font-semibold">Section Title</h3>
  <p className="text-pf-text-secondary">Description text</p>
</div>
```

---

## Migration from Raw Elements

When refactoring existing components:

1. **Replace buttons**: `<button className="...">` → `<Button variant="...">`
2. **Replace inputs**: `<input className="...">` → `<Input />`
3. **Replace selects**: `<select className="...">` → `<Select>`
4. **Wrap with FormField**: Add labels, helpers, error handling
5. **Use Alert**: Replace custom message divs
6. **Use ProgressBar**: Replace manual progress implementations
7. **Update colors**: Replace raw Tailwind colors with `pf-*` tokens

---

## Component Dependencies

All components require:
- `clsx` - For conditional class merging
- `tailwindcss` - For utility classes
- PrintFarmer color tokens configured in `tailwind.config.js`

ProgressBar additionally requires:
- `ProgressBar.module.css` - CSS module for data-attribute width styling

---

## Testing

Components are designed to be easily testable:

```tsx
import { render, screen, fireEvent } from '@testing-library/react';
import { Button } from '@/components/ui/Button';

test('button click handler', () => {
  const handleClick = vi.fn();
  render(<Button onClick={handleClick}>Click Me</Button>);
  
  fireEvent.click(screen.getByText('Click Me'));
  expect(handleClick).toHaveBeenCalledOnce();
});

test('button loading state', () => {
  render(<Button loading>Submit</Button>);
  
  expect(screen.getByText('Please wait…')).toBeInTheDocument();
  expect(screen.getByRole('button')).toBeDisabled();
});
```

---

## Future Enhancements

Planned additions to the shared component library:
- **RadioGroup** - Grouped radio button component
- **Checkbox** - Standalone checkbox with label
- **Textarea** - Text area with auto-resize
- **Badge** - Status badges and tags
- **Modal** - Dialog component
- **Tooltip** - Hover tooltips
- **Tabs** - Tabbed content navigation
- **Card** - Content card container

---

## Questions or Issues?

If you have questions about using these components or need a new shared component:
1. Check this guide and `/src/Web/ReactApp/COLOR_SYSTEM_GUIDE.md`
2. Review existing usage in pages like `NewSliceJobPage.tsx`, `SlicerProfilesPage.tsx`
3. Open a GitHub issue with the `ui-component` label

---

**Last Updated**: Phase 6 UI Component Standardization (2025-10-19)
