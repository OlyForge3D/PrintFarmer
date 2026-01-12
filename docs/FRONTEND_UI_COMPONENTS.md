# PrintFarmer Shared UI Components Guide

## Overview

PrintFarmer uses a standardized set of shared UI components built with React and TypeScript. All components follow the PrintFarmer design system using `pf-*` color tokens defined in `tailwind.config.js` for consistent theming and dynamic theme support.

## Available Components

| Component | Description |
|-----------|-------------|
| [`Alert`](#alert) | Success/error/info/warning messages |
| [`Badge`](#badge) | Status badges and tags |
| [`Button`](#button) | Consistent button variants |
| [`Card`](#card) | Content container with header/footer |
| [`Checkbox`](#checkbox) | Checkbox with optional label |
| [`FormField`](#formfield) | Form field wrapper with label/error |
| [`Input`](#input) | Text input |
| [`Label`](#label) | Simple form field label |
| [`Modal`](#modal) | Dialog/modal with overlay |
| [`ProgressBar`](#progressbar) | Progress indicator |
| [`Radio`](#radio) | Radio button with label |
| [`RadioGroup`](#radiogroup) | Grouped radio buttons |
| [`Select`](#select) | Dropdown select |
| [`Tabs`](#tabs) | Tabbed content navigation |
| [`Textarea`](#textarea) | Multi-line text input |
| [`Toggle`](#toggle) | Switch/toggle for booleans |
| [`Tooltip`](#tooltip) | Hover tooltips |

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

// Icon-only button (no text)
<Button iconCenter={<DeleteIcon />} variant="danger" aria-label="Delete" />
```

**Props**:
- `variant?: 'primary' | 'secondary' | 'danger' | 'subtle' | 'success' | 'tab' | 'toggle'` (default: `'primary'`)
- `size?: 'sm' | 'md' | 'lg'` (default: `'md'`)
- `loading?: boolean` - Shows loading text and disables button
- `iconLeft?: React.ReactNode` - Icon before text
- `iconRight?: React.ReactNode` - Icon after text
- `iconCenter?: React.ReactNode` - Icon-only button with no text (centered)
- All standard `HTMLButtonElement` attributes

**Note**: When using `iconCenter`, any children (text) will be ignored. This prop is specifically for icon-only buttons and ensures proper centering without extra whitespace.

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

### Label

Simple form field label component for consistent styling across form inputs.

**Location**: `src/components/ui/Label.tsx`

**Usage**:
```tsx
import { Label } from '@/components/ui/Label';

// Basic label
<Label htmlFor="username">Username</Label>
<Input id="username" ... />

// Required field (shows asterisk)
<Label htmlFor="email" required>Email Address</Label>
<Input id="email" type="email" ... />

// With custom className
<Label htmlFor="filter" className="mb-2">Filter Options</Label>
```

**Props**:
- `required?: boolean` - Adds red asterisk after label text
- All standard `HTMLLabelElement` attributes (`htmlFor`, `className`, etc.)

**Styling**:
- Text: `text-sm font-medium text-pf-text-secondary`
- Display: `block` (full width by default)
- Required indicator: Red `*` with `text-red-500`

**When to use Label vs FormField**:
- Use `Label` for simple label+input pairs without error handling
- Use `FormField` for complete form patterns with label, input, helper text, and error messages

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

### Checkbox

Standardized checkbox component with optional label.

**Location**: `src/components/ui/Checkbox.tsx`

**Usage**:
```tsx
import { Checkbox } from '@/components/ui/Checkbox';

// Simple checkbox
<Checkbox 
  checked={isSelected} 
  onChange={e => setIsSelected(e.target.checked)}
  aria-label="Select item"
/>

// With label
<Checkbox 
  label="Accept terms and conditions"
  checked={accepted} 
  onChange={e => setAccepted(e.target.checked)}
/>

// Disabled state
<Checkbox 
  label="Feature unavailable"
  checked={false}
  disabled
/>

// With validation error
<Checkbox 
  label="Required checkbox"
  checked={value}
  onChange={handleChange}
  invalid={hasError}
/>
```

**Props**:
- `label?: string` - Label text displayed next to checkbox
- `invalid?: boolean` - Applies error styling
- All standard checkbox input attributes (`checked`, `onChange`, `disabled`, etc.)

**Styling**:
- Background: `pf-bg-0` (unchecked), `pf-accent` (checked)
- Border: `pf-border` (normal), `pf-error` (invalid)
- Focus ring: `pf-accent` (normal), `pf-error` (invalid)

---

### Radio

Standardized radio button component with optional label.

**Location**: `src/components/ui/Radio.tsx`

**Usage**:
```tsx
import { Radio } from '@/components/ui/Radio';

// Simple radio buttons
<Radio 
  name="size"
  value="small"
  checked={size === 'small'}
  onChange={() => setSize('small')}
  label="Small"
/>
<Radio 
  name="size"
  value="large"
  checked={size === 'large'}
  onChange={() => setSize('large')}
  label="Large"
/>

// Without label
<Radio 
  name="option"
  value="A"
  checked={option === 'A'}
  onChange={() => setOption('A')}
  aria-label="Option A"
/>
```

**Props**:
- `label?: string` - Label text displayed next to radio
- `invalid?: boolean` - Applies error styling
- All standard radio input attributes (`name`, `value`, `checked`, `onChange`, etc.)

**Styling**:
- Same as Checkbox for consistency

---

### RadioGroup

Grouped radio button component for managing multiple options.

**Location**: `src/components/ui/RadioGroup.tsx`

**Usage**:
```tsx
import { RadioGroup } from '@/components/ui/RadioGroup';

// Basic radio group
<RadioGroup
  name="priority"
  value={priority}
  onChange={setPriority}
  options={[
    { value: 'low', label: 'Low' },
    { value: 'normal', label: 'Normal' },
    { value: 'high', label: 'High' },
  ]}
/>

// Horizontal layout
<RadioGroup
  name="mode"
  value={mode}
  onChange={setMode}
  direction="horizontal"
  options={[
    { value: 'auto', label: 'Auto' },
    { value: 'manual', label: 'Manual' },
  ]}
/>

// With disabled options
<RadioGroup
  name="plan"
  value={plan}
  onChange={setPlan}
  options={[
    { value: 'free', label: 'Free' },
    { value: 'pro', label: 'Pro', disabled: true },
    { value: 'enterprise', label: 'Enterprise' },
  ]}
/>
```

**Props**:
- `name: string` - Name attribute for all radio inputs
- `options: { value: string; label: string; disabled?: boolean }[]` - Array of options
- `value?: string` - Currently selected value
- `onChange?: (value: string) => void` - Selection change handler
- `direction?: 'horizontal' | 'vertical'` (default: `'vertical'`)
- `disabled?: boolean` - Disable entire group
- `invalid?: boolean` - Apply error styling

---

### Textarea

Standardized multi-line text input.

**Location**: `src/components/ui/Textarea.tsx`

**Usage**:
```tsx
import { Textarea } from '@/components/ui/Textarea';

// Basic textarea
<Textarea 
  value={notes}
  onChange={e => setNotes(e.target.value)}
  placeholder="Enter notes..."
/>

// With custom rows
<Textarea 
  value={description}
  onChange={e => setDescription(e.target.value)}
  rows={6}
/>

// With validation error
<Textarea 
  value={content}
  onChange={e => setContent(e.target.value)}
  invalid={hasError}
/>
```

**Props**:
- `invalid?: boolean` - Applies error styling
- All standard `HTMLTextAreaElement` attributes (`value`, `onChange`, `rows`, `placeholder`, etc.)

**Styling**:
- Same token usage as `Input` for consistency
- Default minimum height: 80px
- Vertical resize enabled

---

### Toggle

Switch/toggle component for boolean options (alternative to checkbox).

**Location**: `src/components/ui/Toggle.tsx`

**Usage**:
```tsx
import { Toggle } from '@/components/ui/Toggle';

// Basic toggle
<Toggle 
  checked={isEnabled}
  onChange={e => setIsEnabled(e.target.checked)}
/>

// With label
<Toggle 
  label="Enable notifications"
  checked={notifications}
  onChange={e => setNotifications(e.target.checked)}
/>

// Small size
<Toggle 
  size="sm"
  checked={compact}
  onChange={e => setCompact(e.target.checked)}
/>

// Disabled
<Toggle 
  label="Premium feature"
  checked={false}
  disabled
/>
```

**Props**:
- `label?: string` - Label text displayed next to toggle
- `size?: 'sm' | 'md'` (default: `'md'`)
- `invalid?: boolean` - Applies error styling
- All standard checkbox input attributes (`checked`, `onChange`, `disabled`, etc.)

**Styling**:
- Track: `pf-bg-2` (off), `pf-accent` (on)
- Thumb: White with shadow
- Focus ring: `pf-accent`

---

### Badge

Status badges and tags for labeling content.

**Location**: `src/components/ui/Badge.tsx`

**Usage**:
```tsx
import { Badge } from '@/components/ui/Badge';

// Default badge
<Badge>Default</Badge>

// Variants
<Badge variant="primary">Primary</Badge>
<Badge variant="success">Success</Badge>
<Badge variant="warning">Warning</Badge>
<Badge variant="error">Error</Badge>
<Badge variant="info">Info</Badge>

// Sizes
<Badge size="sm">Small</Badge>
<Badge size="md">Medium</Badge>

// Pill style (fully rounded)
<Badge variant="success" pill>Active</Badge>
```

**Props**:
- `variant?: 'default' | 'primary' | 'success' | 'warning' | 'error' | 'info'` (default: `'default'`)
- `size?: 'sm' | 'md'` (default: `'sm'`)
- `pill?: boolean` - Fully rounded corners (default: `false`)
- `className?: string` - Additional classes
- `children: React.ReactNode` - Badge content

---

### Card

Content container with optional header and footer.

**Location**: `src/components/ui/Card.tsx`

**Usage**:
```tsx
import { Card } from '@/components/ui/Card';

// Basic card
<Card>
  <Card.Header>
    <h3>Card Title</h3>
  </Card.Header>
  <Card.Body>
    Card content goes here.
  </Card.Body>
  <Card.Footer>
    <Button>Action</Button>
  </Card.Footer>
</Card>

// Without header/footer
<Card>
  <Card.Body>
    Simple card content
  </Card.Body>
</Card>

// Hover effect
<Card hover>
  <Card.Body>Hoverable card</Card.Body>
</Card>
```

**Props (Card)**:
- `hover?: boolean` - Enable hover effect
- `className?: string` - Additional classes
- `children: React.ReactNode` - Card content

**Subcomponents**:
- `Card.Header` - Card header section
- `Card.Body` - Main content area
- `Card.Footer` - Footer with actions

---

### Modal

Dialog/modal component with overlay.

**Location**: `src/components/ui/Modal.tsx`

**Usage**:
```tsx
import { Modal } from '@/components/ui/Modal';

// Basic modal
<Modal isOpen={isOpen} onClose={() => setIsOpen(false)} title="Modal Title">
  <p>Modal content goes here.</p>
</Modal>

// With footer actions
<Modal 
  isOpen={isOpen} 
  onClose={handleClose} 
  title="Confirm Action"
  footer={
    <>
      <Button variant="secondary" onClick={handleClose}>Cancel</Button>
      <Button variant="primary" onClick={handleConfirm}>Confirm</Button>
    </>
  }
>
  <p>Are you sure you want to proceed?</p>
</Modal>

// Different sizes
<Modal isOpen={isOpen} onClose={handleClose} title="Large Modal" size="lg">
  <p>More content space</p>
</Modal>

// Non-closable (no X button, no overlay click)
<Modal isOpen={isOpen} onClose={handleClose} title="Required" closable={false}>
  <p>You must complete this action.</p>
</Modal>
```

**Props**:
- `isOpen: boolean` - Controls modal visibility
- `onClose: () => void` - Close handler
- `title?: string` - Modal title
- `size?: 'sm' | 'md' | 'lg' | 'xl'` (default: `'md'`)
- `closable?: boolean` - Show X button, allow overlay click (default: `true`)
- `footer?: React.ReactNode` - Footer content
- `className?: string` - Additional classes for content
- `children: React.ReactNode` - Modal body content

---

### Tooltip

Hover tooltip for additional information.

**Location**: `src/components/ui/Tooltip.tsx`

**Usage**:
```tsx
import { Tooltip } from '@/components/ui/Tooltip';

// Basic tooltip
<Tooltip content="Helpful information">
  <Button>Hover me</Button>
</Tooltip>

// Positions
<Tooltip content="Top tooltip" position="top">
  <span>Top</span>
</Tooltip>
<Tooltip content="Bottom tooltip" position="bottom">
  <span>Bottom</span>
</Tooltip>
<Tooltip content="Left tooltip" position="left">
  <span>Left</span>
</Tooltip>
<Tooltip content="Right tooltip" position="right">
  <span>Right</span>
</Tooltip>

// Custom delay
<Tooltip content="Delayed tooltip" delay={500}>
  <Button>Wait 500ms</Button>
</Tooltip>
```

**Props**:
- `content: React.ReactNode` - Tooltip content
- `position?: 'top' | 'bottom' | 'left' | 'right'` (default: `'top'`)
- `delay?: number` - Show delay in ms (default: `0`)
- `className?: string` - Additional classes
- `children: React.ReactNode` - Trigger element

---

### Tabs

Tabbed content navigation component.

**Location**: `src/components/ui/Tabs.tsx`

**Usage**:
```tsx
import { Tabs } from '@/components/ui/Tabs';

// Basic tabs
<Tabs defaultIndex={0}>
  <Tabs.List>
    <Tabs.Tab>Tab 1</Tabs.Tab>
    <Tabs.Tab>Tab 2</Tabs.Tab>
    <Tabs.Tab>Tab 3</Tabs.Tab>
  </Tabs.List>
  <Tabs.Panels>
    <Tabs.Panel>Content for tab 1</Tabs.Panel>
    <Tabs.Panel>Content for tab 2</Tabs.Panel>
    <Tabs.Panel>Content for tab 3</Tabs.Panel>
  </Tabs.Panels>
</Tabs>

// Controlled tabs
const [tabIndex, setTabIndex] = useState(0);
<Tabs index={tabIndex} onChange={setTabIndex}>
  <Tabs.List>
    <Tabs.Tab>First</Tabs.Tab>
    <Tabs.Tab>Second</Tabs.Tab>
  </Tabs.List>
  <Tabs.Panels>
    <Tabs.Panel>First content</Tabs.Panel>
    <Tabs.Panel>Second content</Tabs.Panel>
  </Tabs.Panels>
</Tabs>

// Disabled tab
<Tabs>
  <Tabs.List>
    <Tabs.Tab>Enabled</Tabs.Tab>
    <Tabs.Tab disabled>Disabled</Tabs.Tab>
  </Tabs.List>
  ...
</Tabs>
```

**Props (Tabs)**:
- `defaultIndex?: number` - Initial tab index (uncontrolled)
- `index?: number` - Current tab index (controlled)
- `onChange?: (index: number) => void` - Tab change handler
- `className?: string` - Additional classes

**Subcomponents**:
- `Tabs.List` - Tab button container
- `Tabs.Tab` - Individual tab button (accepts `disabled`)
- `Tabs.Panels` - Tab content container
- `Tabs.Panel` - Individual tab content

---

## Importing Components

All UI components can be imported from the barrel export:

```tsx
import { 
  Alert,
  Badge,
  Button, 
  Card,
  Checkbox, 
  FormField, 
  Input, 
  Modal,
  ProgressBar,
  Radio, 
  RadioGroup,
  Select, 
  Tabs,
  Textarea, 
  Toggle,
  Tooltip,
} from '@/components/ui';
```

Or import individually:

```tsx
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
```

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

## Questions or Issues?

If you have questions about using these components or need a new shared component:
1. Check this guide and `/src/Web/ReactApp/COLOR_SYSTEM_GUIDE.md`
2. Review existing usage in pages like `NewSliceJobPage.tsx`, `SlicerProfilesPage.tsx`
3. Open a GitHub issue with the `ui-component` label

---

**Last Updated**: UI Component Library Complete (2025-11-28)
