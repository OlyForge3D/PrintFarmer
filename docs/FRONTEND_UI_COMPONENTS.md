# PrintFarmer Shared UI Components Guide

## Overview

PrintFarmer uses a standardized set of shared UI components built with React and TypeScript. All components follow the PrintFarmer design system using `pf-*` color tokens defined in `tailwind.config.js` for consistent theming and dynamic theme support.

## Available Components

| Component | Location | Description |
|-----------|----------|-------------|
| [`Alert`](#alert) | `src/components/ui/Alert.tsx` | Success/error/info/warning messages |
| [`Badge`](#badge) | `src/components/ui/Badge.tsx` | Status badges and tags |
| [`Breadcrumbs`](#breadcrumbs) | `src/common/components/Breadcrumbs.tsx` | Navigation breadcrumb trail |
| [`Button`](#button) | `src/components/ui/Button.tsx` | Consistent button variants |
| [`Card`](#card) | `src/components/ui/Card.tsx` | Content container with header/footer |
| [`Checkbox`](#checkbox) | `src/components/ui/Checkbox.tsx` | Checkbox with optional label |
| [`ConfirmationModal`](#confirmationmodal) | `src/common/components/modals/ConfirmationModal.tsx` | Generic confirmation dialog for destructive operations |
| [`ContextMenu`](#contextmenu) | `src/common/components/ContextMenu.tsx` | Right-click context menu with smart positioning |
| [`FloatingActionButton`](#floatingactionbutton) | `src/common/components/FloatingActionButton.tsx` | Fixed position action button for primary actions |
| [`FormField`](#formfield) | `src/components/ui/FormField.tsx` | Form field wrapper with label/error |
| [`InfiniteScroll`](#infinitescroll) | `src/common/components/InfiniteScroll.tsx` | Infinite scroll wrapper for paginated content |
| [`Input`](#input) | `src/components/ui/Input.tsx` | Text input |
| [`Label`](#label) | `src/components/ui/Label.tsx` | Simple form field label |
| [`MasterDetailLayout`](#masterdetaillayout) | `src/common/components/layout/MasterDetailLayout.tsx` | Responsive master/detail sidebar layout for lists |
| [`Modal`](#modal) | `src/common/components/modals/Modal.tsx` | Dialog/modal with overlay |
| [`ProgressBar`](#progressbar) | `src/components/ui/ProgressBar.tsx` | Progress indicator |
| [`Radio`](#radio) | `src/components/ui/Radio.tsx` | Radio button with label |
| [`RadioGroup`](#radiogroup) | `src/components/ui/RadioGroup.tsx` | Grouped radio buttons |
| [`Select`](#select) | `src/components/ui/Select.tsx` | Dropdown select |
| [`Tabs`](#tabs) | `src/components/ui/Tabs.tsx` | Tabbed content navigation |
| [`Textarea`](#textarea) | `src/components/ui/Textarea.tsx` | Multi-line text input |
| [`Toggle`](#toggle) | `src/components/ui/Toggle.tsx` | Switch/toggle for booleans |
| [`Tooltip`](#tooltip) | `src/components/ui/Tooltip.tsx` | Hover tooltips |

## Locations

- **Base UI Components**: `src/components/ui/` - Foundational, low-level UI controls
- **Shared Modals**: `src/common/components/modals/` - Reusable modal/dialog components
- **Shared Utilities**: `src/common/components/` - Other reusable components (ContextMenu, etc.)

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

### FloatingActionButton

Floating action button (FAB) component for primary actions, typically positioned at bottom-right.

**Location**: `src/common/components/FloatingActionButton.tsx`

**Usage**:
```tsx
import { FloatingActionButton } from '@/common/components/FloatingActionButton';

// Default FAB (bottom-right)
<FloatingActionButton 
  icon={PlusIcon}
  onClick={handleAddNewItem}
  label="Add Item"
/>

// Custom position
<FloatingActionButton 
  icon={EditIcon}
  onClick={handleEdit}
  label="Edit"
  position="bottom-center"
/>

// With loading state
<FloatingActionButton 
  icon={SaveIcon}
  onClick={handleSave}
  label="Save"
  loading={isSaving}
/>

// Disabled FAB
<FloatingActionButton 
  icon={DeleteIcon}
  onClick={handleDelete}
  label="Delete"
  disabled={!hasPermission}
  variant="danger"
/>
```

**Props**:
- `icon: React.ReactNode` - Icon component to display
- `onClick: () => void` - Click handler
- `label: string` - Tooltip and aria-label text
- `position?: 'bottom-right' | 'bottom-center' | 'bottom-left'` (default: `'bottom-right'`)
- `variant?: 'primary' | 'secondary' | 'danger'` (default: `'primary'`)
- `loading?: boolean` - Shows spinner overlay (default: `false`)
- `disabled?: boolean` - Disables button (default: `false`)
- `className?: string` - Additional CSS classes

**Features**:
- Fixed positioning (sticky footer area)
- Smooth fade-in animation on mount
- Loading spinner with animation during async operations
- Accessible with ARIA labels
- Keyboard support (space/enter to activate)
- Built on Button component foundation
- Prevents obstruction by scrollable content

**Best Practices**:
- Use for primary/critical actions (Create, Edit, Save)
- Keep to one FAB per screen when possible
- Use appropriate position based on layout (bottom-right most common)
- Provide meaningful labels for screen readers
- Show loading state during async operations
- Disable when action is not available

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

### Breadcrumbs

Navigation breadcrumb trail showing hierarchy and current location.

**Location**: `src/common/components/Breadcrumbs.tsx`

**Usage**:
```tsx
import { Breadcrumbs } from '@/common/components/Breadcrumbs';

// Basic breadcrumbs
<Breadcrumbs items={[
  { label: 'Home', href: '/' },
  { label: 'Files', href: '/files' },
  { label: 'Models', current: true }
]} />

// With navigation
function ModelsPage() {
  const location = useLocation();
  const navigate = useNavigate();

  const breadcrumbs = [
    { label: 'Dashboard', href: '/' },
    { label: 'Files', href: '/files' },
    { label: 'Models', current: true }
  ];

  return (
    <>
      <Breadcrumbs items={breadcrumbs} className="mb-4" />
      {/* Page content */}
    </>
  );
}
```

**BreadcrumbItem Interface**:
```tsx
interface BreadcrumbItem {
  label: string;      // Display text
  href?: string;      // Navigation link (omit for current page)
  current?: boolean;  // Mark as current/active page
}
```

**Props**:
- `items: BreadcrumbItem[]` - Array of breadcrumb items
- `className?: string` - Additional CSS classes

**Features**:
- Accessible navigation with `aria-label="Breadcrumb"`
- Uses semantic `<nav>` and `<ol>` elements
- Current page shown in bold with primary text color
- Navigation items are links with hover state
- Keyboard-accessible focus ring
- Chevron separators between items

**Best Practices**:
- Always include the current page as the last item with `current: true`
- Don't include the current page in the link (`href` optional for current)
- Use for navigation pages (not modals or side panels)
- Keep breadcrumbs to 3-5 levels maximum
- Start with home/dashboard link

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

### ConfirmationModal

Generic confirmation dialog for user confirmation before destructive operations.

**Location**: `src/common/components/modals/ConfirmationModal.tsx`

**Usage**:
```tsx
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';

const [showConfirm, setShowConfirm] = useState(false);

// Delete confirmation
<ConfirmationModal
  isOpen={showConfirm}
  title="Delete Model?"
  message={`Are you sure you want to delete "${modelName}"? This action cannot be undone.`}
  confirmButtonText="Delete"
  cancelButtonText="Cancel"
  isDangerous={true}
  onConfirm={async () => {
    await deleteModel(modelId);
    setShowConfirm(false);
  }}
  onCancel={() => setShowConfirm(false)}
/>

// Generic confirmation
<ConfirmationModal
  isOpen={showConfirm}
  title="Approve Changes?"
  message="This will permanently update the configuration."
  confirmButtonText="Approve"
  isDangerous={false}
  onConfirm={handleApprove}
  onCancel={() => setShowConfirm(false)}
/>

// With additional content
<ConfirmationModal
  isOpen={showConfirm}
  title="Delete User?"
  message={`Delete "${username}"? This cannot be undone.`}
  isDangerous={true}
  onConfirm={handleDelete}
  onCancel={() => setShowConfirm(false)}
>
  <div className="mt-4 p-3 bg-pf-bg-2 rounded text-sm">
    <p>Associated data will be:</p>
    <ul className="list-disc ml-5">
      <li>Removed from all projects</li>
      <li>Archived, not deleted</li>
    </ul>
  </div>
</ConfirmationModal>
```

**Props**:
- `isOpen: boolean` - Controls modal visibility
- `title: string` - Modal title
- `message: string` - Confirmation message text
- `confirmButtonText?: string` - Confirm button label (default: `'Confirm'`)
- `cancelButtonText?: string` - Cancel button label (default: `'Cancel'`)
- `isDangerous?: boolean` - Set to `true` for destructive operations (changes button to danger variant, shows alert icon)
- `onConfirm: () => void` - Confirm handler
- `onCancel: () => void` - Cancel handler
- `children?: React.ReactNode` - Additional content below the message

**Best Practices**:
- Use `isDangerous={true}` for delete/destructive operations
- Keep message concise and action-focused
- Always provide `confirmButtonText` that clearly describes the action ("Delete", "Remove", "Proceed", etc.)
- Place in a state hook near the action that triggers it
- **Never create a custom delete dialog** - use this component instead

---

### ContextMenu

Right-click context menu with intelligent positioning to prevent viewport overflow.

**Location**: `src/common/components/ContextMenu.tsx`

**Associated Hook**: `src/common/hooks/useContextMenu.ts`

**Usage**:
```tsx
import { ContextMenu } from '@/common/components/ContextMenu';
import { useContextMenu } from '@/common/hooks/useContextMenu';
import { DeleteIcon, DownloadIcon, TagIcon } from '@/common/components/icons/MdiIcons';

function MyComponent() {
  const { position, handleContextMenu, closeMenu, isOpen } = useContextMenu();
  const [selectedItem, setSelectedItem] = useState<Item | null>(null);

  const handleRightClick = (e: React.MouseEvent, item: Item) => {
    setSelectedItem(item);
    handleContextMenu(e);
  };

  return (
    <>
      <div onContextMenu={(e) => handleRightClick(e, item)}>
        {/* Your content */}
      </div>

      {isOpen && position && (
        <ContextMenu
          x={position.x}
          y={position.y}
          items={[
            {
              label: 'Tag',
              icon: TagIcon,
              onClick: () => {
                onTagItem(selectedItem);
                closeMenu();
              },
            },
            {
              label: 'Download',
              icon: DownloadIcon,
              onClick: () => {
                downloadItem(selectedItem);
                closeMenu();
              },
            },
            { divider: true },
            {
              label: 'Delete',
              icon: DeleteIcon,
              variant: 'danger',
              onClick: () => {
                setConfirmDelete(selectedItem);
                closeMenu();
              },
            },
          ]}
          onClose={closeMenu}
        />
      )}

      {/* Confirmation dialog for delete */}
      {confirmDelete && (
        <ConfirmationModal
          isOpen={!!confirmDelete}
          title="Delete Item?"
          message={`Delete "${confirmDelete.name}"?`}
          isDangerous={true}
          onConfirm={() => handleDelete(confirmDelete)}
          onCancel={() => setConfirmDelete(null)}
        />
      )}
    </>
  );
}
```

**ContextMenuItem Interface**:
```tsx
interface ContextMenuItem {
  label: string;                                          // Display text
  icon?: React.ComponentType<{ className?: string }>;    // Icon component (will be positioned LEFT)
  onClick: () => void;                                    // Click handler
  variant?: 'default' | 'danger';                        // 'danger' for destructive items
  disabled?: boolean;                                     // Disabled state
  divider?: boolean;                                      // Render as divider instead of item
}
```

**useContextMenu Hook**:
```tsx
const { 
  position,        // { x: number, y: number } | null - Current menu position
  isOpen,          // boolean - Whether menu is visible
  handleContextMenu, // (e: React.MouseEvent) => void - Right-click handler
  closeMenu        // () => void - Close the menu
} = useContextMenu();
```

**Props**:
- `x: number` - X coordinate (from mouse event)
- `y: number` - Y coordinate (from mouse event)
- `items: ContextMenuItem[]` - Menu items
- `onClose: () => void` - Close handler

**Features**:
- Auto-closes on outside click (with 50ms delay to prevent immediate close)
- Escape key closes menu
- Smart positioning prevents menu from rendering off-screen
- ARIA-compliant with `role="menu"`
- Icons positioned to the LEFT of text using Button's `iconLeft` prop
- Support for dividers and disabled items

**Best Practices**:
- Always pair with `useContextMenu` hook for consistent positioning
- Keep menu items to 5-7 items maximum
- Use dividers to group related items
- Put destructive actions (delete, remove) at the bottom with `variant="danger"`
- Always pair delete items with `ConfirmationModal`
- Close menu immediately after triggering action

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

### InfiniteScroll

Wrapper component for infinite scrolling with automatic pagination.

**Location**: `src/common/components/InfiniteScroll.tsx`

**Usage**:
```tsx
import { InfiniteScroll } from '@/common/components/InfiniteScroll';
import { useInfiniteList } from '@/common/hooks/useInfiniteList';

interface Item {
  id: string;
  title: string;
}

function ItemsList() {
  const { allItems, hasMore, isLoadingMore, fetchNextPage } = useInfiniteList<Item>(
    (pageParam) => fetch(`/api/items?page=${pageParam}`).then(r => r.json()),
    { initialPageParam: 1 }
  );

  return (
    <InfiniteScroll
      items={allItems}
      hasMore={hasMore}
      isLoading={isLoadingMore}
      onLoadMore={() => fetchNextPage()}
      renderItem={(item) => (
        <div key={item.id} className="border-b p-4">
          {item.title}
        </div>
      )}
    />
  );
}

// Custom loader component
<InfiniteScroll
  items={items}
  hasMore={hasMore}
  isLoading={isLoadingMore}
  onLoadMore={fetchNextPage}
  renderItem={renderItem}
  loader={<CustomLoadingSpinner />}
/>

// Custom end message
<InfiniteScroll
  items={items}
  hasMore={hasMore}
  isLoading={isLoadingMore}
  onLoadMore={fetchNextPage}
  renderItem={renderItem}
  endMessage={<p className="text-center text-gray-500">No more items</p>}
/>
```

**Props**:
- `items: T[]` - Array of items to render
- `hasMore: boolean` - Whether more items are available
- `isLoading: boolean` - Loading state for next page
- `onLoadMore: () => void` - Callback when end is reached
- `renderItem: (item: T) => React.ReactNode` - Render function for each item
- `loader?: React.ReactNode` - Custom loading indicator (default: spinner)
- `endMessage?: React.ReactNode` - Message when no more items
- `threshold?: number` - Pixel distance from bottom to trigger load (default: `200`)
- `className?: string` - Container CSS classes

**Features**:
- Uses IntersectionObserver for efficient scroll detection
- Automatically triggers loading when threshold reached
- Handles loading and end states
- Works with `useInfiniteList` hook
- Customizable loader and end messages
- Generic type support for any item type

**Best Practices**:
- Pair with `useInfiniteList` hook for API integration
- Set appropriate `threshold` for your content (200px-500px typical)
- Provide meaningful `endMessage` when no more items
- Use stable key props in rendered items
- Consider showing total count or remaining items count

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

## Custom Hooks

### useInfiniteList

React Query hook for handling infinite/paginated data fetching with automatic pagination.

**Location**: `src/common/hooks/useInfiniteList.ts`

**Usage**:
```tsx
import { useInfiniteList } from '@/common/hooks/useInfiniteList';

interface Item {
  id: string;
  title: string;
}

interface PaginatedResponse<T> {
  data: T[];
  pageNum: number;
  pageSize: number;
  totalItems: number;
  totalPages: number;
}

function ItemsList() {
  // Basic usage with API endpoint
  const { allItems, hasMore, isLoadingMore, fetchNextPage } = useInfiniteList<Item>(
    (pageParam) => 
      fetch(`/api/items?page=${pageParam}`).then(r => r.json() as Promise<PaginatedResponse<Item>>),
    { initialPageParam: 1 }
  );

  return (
    <InfiniteScroll
      items={allItems}
      hasMore={hasMore}
      isLoading={isLoadingMore}
      onLoadMore={fetchNextPage}
      renderItem={(item) => (
        <div key={item.id}>{item.title}</div>
      )}
    />
  );
}

// With filters/search
function SearchItems() {
  const [searchTerm, setSearchTerm] = useState('');
  
  const { allItems, hasMore, isLoadingMore, fetchNextPage } = useInfiniteList<Item>(
    (pageParam) => 
      fetch(`/api/items?page=${pageParam}&search=${searchTerm}`)
        .then(r => r.json() as Promise<PaginatedResponse<Item>>),
    { 
      initialPageParam: 1,
      enabled: searchTerm.length > 0  // Don't fetch if search empty
    }
  );

  return (
    <>
      <input 
        value={searchTerm} 
        onChange={e => setSearchTerm(e.target.value)} 
        placeholder="Search..."
      />
      <InfiniteScroll
        items={allItems}
        hasMore={hasMore}
        isLoading={isLoadingMore}
        onLoadMore={fetchNextPage}
        renderItem={(item) => <div key={item.id}>{item.title}</div>}
      />
    </>
  );
}
```

**Hook Return Type**:
```tsx
interface UseInfiniteListReturn<T> {
  allItems: T[];                    // Flattened array of all loaded items
  hasMore: boolean;                 // Whether more items are available
  isLoading: boolean;               // Initial load state
  isLoadingMore: boolean;           // Loading next page state
  isFetching: boolean;              // Any fetch activity
  error: Error | null;              // Error if fetch failed
  fetchNextPage: () => void;        // Trigger next page load
  refetch: () => void;              // Reload all data
  status: 'idle' | 'pending' | 'success' | 'error';  // Query status
}
```

**Parameters**:
- `queryFn: (pageParam: number) => Promise<PaginatedResponse<T>>` - Async function that fetches paginated data
- `options?: UseInfiniteQueryOptions` - React Query options (initialPageParam, enabled, etc.)

**Expected PaginatedResponse Format**:
```tsx
interface PaginatedResponse<T> {
  data: T[];           // Array of items for this page
  pageNum: number;     // Current page number
  pageSize: number;    // Items per page
  totalItems: number;  // Total items available
  totalPages: number;  // Total pages available
}
```

**Features**:
- Generic type support for any item type
- Automatic page flattening into single array
- React Query integration for caching and refetching
- Built-in error handling
- Disable fetching with `enabled` option
- Works seamlessly with `InfiniteScroll` component

**Best Practices**:
- Always type the generic parameter: `useInfiniteList<YourType>(...)`
- Validate API response has required `PaginatedResponse` structure
- Use `enabled` option to prevent unnecessary fetches (e.g., when search term empty)
- Pair with `InfiniteScroll` component for UI rendering
- Handle empty states (when `allItems.length === 0`)
- Show error message if `error` is set
- Consider adding debounce to search filters before refetch

---

### useKeyboardNavigation

React hook for handling keyboard navigation in lists, grids, and similar components. Supports arrow key navigation with proper boundary checking and selection callbacks.

**Location**: `src/common/hooks/useKeyboardNavigation.ts`

**Usage**:
```tsx
import { useKeyboardNavigation } from '@/common/hooks/useKeyboardNavigation';

interface Item {
  id: string;
  title: string;
}

function SelectableList({ items }: { items: Item[] }) {
  const { selectedIndex, setSelectedIndex, isNavigating } = useKeyboardNavigation<Item>({
    items,
    columns: 1,  // Single column list
    onEnter: (item) => {
      console.log('Selected:', item);
    },
    onEscapeKey: () => {
      console.log('Navigation cancelled');
    }
  });

  return (
    <ul>
      {items.map((item, index) => (
        <li 
          key={item.id}
          className={selectedIndex === index ? 'bg-pf-accent text-white' : ''}
          onClick={() => setSelectedIndex(index)}
        >
          {item.title}
        </li>
      ))}
    </ul>
  );
}

// Grid usage with 3 columns
function SelectableGrid({ items }: { items: Item[] }) {
  const { selectedIndex, setSelectedIndex } = useKeyboardNavigation<Item>({
    items,
    columns: 3,  // 3-column grid
    onEnter: (item) => handleSelectItem(item)
  });

  const selectedItem = items[selectedIndex];

  return (
    <div className="grid grid-cols-3 gap-4">
      {items.map((item, index) => (
        <div 
          key={item.id}
          className={`p-4 border ${selectedIndex === index ? 'border-pf-accent bg-pf-accent/10' : 'border-pf-border'}`}
          tabIndex={selectedIndex === index ? 0 : -1}
        >
          {item.title}
        </div>
      ))}
    </div>
  );
}
```

**Hook Return Type**:
```tsx
interface UseKeyboardNavigationReturn<T> {
  selectedIndex: number;              // Index of currently selected item
  setSelectedIndex: (index: number) => void;  // Update selected index
  isNavigating: boolean;              // Whether keyboard navigation is active
}
```

**Parameters**:
```tsx
interface UseKeyboardNavigationOptions<T> {
  items: T[];                         // Array of items to navigate
  columns?: number;                   // Number of columns for grid (default: 1)
  onEnter?: (item: T) => void;       // Callback when Enter is pressed
  onEscapeKey?: () => void;          // Callback when Escape is pressed
}
```

**Supported Keys**:
- **Arrow Up**: Move selection up (by columns, wraps at boundary)
- **Arrow Down**: Move selection down (by columns, wraps at boundary)
- **Arrow Left**: Move selection left in grid
- **Arrow Right**: Move selection right in grid
- **Enter**: Trigger `onEnter` callback with selected item
- **Escape**: Trigger `onEscapeKey` callback and reset navigation

**Features**:
- Generic type support for any item type
- Multi-column grid support with proper directional navigation
- Boundary checking (prevents over/under selection)
- Automatic wrapping at list edges
- useCallback optimization for handlers
- Proper cleanup of event listeners on unmount

**Best Practices**:
- Always provide `items` array that matches rendered items
- Use `columns` parameter correctly for grid layouts
- Update visual selection state based on `selectedIndex`
- Set `tabIndex={0}` on selected item for focus management
- Use `isNavigating` to prevent other keyboard handlers from interfering

---

### useKeyboardShortcuts

React hook for managing global keyboard shortcuts (Ctrl+key combinations). Provides a centralized way to handle keyboard shortcuts with metadata for help displays.

**Location**: `src/common/hooks/useKeyboardShortcuts.ts`

**Usage**:
```tsx
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';

function FileManager() {
  // Define keyboard shortcuts
  const shortcuts = useKeyboardShortcuts([
    {
      key: 'u',
      handler: handleUpload,
      description: 'Upload new file'
    },
    {
      key: 'd',
      handler: handleDelete,
      description: 'Delete selected file'
    },
    {
      key: 't',
      handler: handleTag,
      description: 'Tag file'
    },
    {
      key: 'f',
      handler: handleFilter,
      description: 'Open filter menu'
    }
  ], {
    enabled: true  // Can disable all shortcuts
  });

  // Render help text showing available shortcuts
  return (
    <div>
      <div className="mb-4">
        <h3>Keyboard Shortcuts:</h3>
        <ul>
          {shortcuts.map((sc) => (
            <li key={sc.key}>
              <kbd>Ctrl+{sc.display}</kbd> - {sc.description}
            </li>
          ))}
        </ul>
      </div>
      
      {/* File manager content */}
    </div>
  );
}

// With conditional enabling
function SearchForm() {
  const [isOpen, setIsOpen] = useState(false);

  useKeyboardShortcuts([
    {
      key: 'f',
      handler: () => setIsOpen(!isOpen),
      description: 'Open search'
    }
  ], {
    enabled: !isOpen  // Don't capture shortcuts when modal open
  });

  return (
    // ...
  );
}
```

**Hook Return Type**:
```tsx
interface ShortcutMetadata {
  key: string;                   // Single character (shortcut key)
  display: string;               // Uppercase display format (for Ctrl+K)
  description: string;           // User-friendly description
  handler: () => void;           // Function to call on shortcut
}

// Hook returns array of ShortcutMetadata
const shortcuts: ShortcutMetadata[] = useKeyboardShortcuts([...], options);
```

**Parameters**:
```tsx
interface KeyboardShortcut {
  key: string;                   // Single character to combine with Ctrl
  handler: () => void;           // Function to execute
  description: string;           // Help text for display
}

interface UseKeyboardShortcutsOptions {
  enabled?: boolean;             // Enable/disable all shortcuts (default: true)
}
```

**Default Shortcuts** (if not overridden):
| Shortcut | Action | Description |
|----------|--------|-------------|
| `Ctrl+U` | Upload | Upload new file or model |
| `Ctrl+D` | Delete | Delete selected item |
| `Ctrl+T` | Tag | Tag or label item |
| `Ctrl+F` | Filter | Open filter menu |
| `Ctrl+N` | New | Create new item |
| `Ctrl+S` | Save | Save changes |
| `Ctrl+C` | Cancel/Copy | Copy or cancel operation |
| `Ctrl+P` | Print/Pause | Print job or pause action |

**Features**:
- Global keyboard event handling with proper cleanup
- Works with both Ctrl (Windows/Linux) and Cmd (macOS) modifier keys
- Automatic handler metadata generation for help displays
- Enable/disable all shortcuts at once with `enabled` option
- Proper event listener cleanup on unmount
- No interference with form inputs

**Best Practices**:
- Document your shortcuts for users (show them in help/settings)
- Disable shortcuts in modals/forms to avoid conflicts
- Use single-character keys for simplicity (avoid multi-key combos)
- Test shortcuts across browsers for compatibility
- Don't override system shortcuts (Ctrl+S, Ctrl+W, etc.)
- Show available shortcuts in UI help text
- Consider accessibility - keyboard shortcuts must be optional, not required

---

### MasterDetailLayout

Responsive layout component that displays a master list/sidebar alongside a detail panel. Automatically adapts between desktop side-by-side view and mobile list-only/detail-only toggle.

**Location**: `src/common/components/layout/MasterDetailLayout.tsx`

**Usage**:
```tsx
import { useState } from 'react';
import { MasterDetailLayout } from '@/common/components/layout/MasterDetailLayout';

interface Item {
  id: string;
  title: string;
}

function ItemManager() {
  const [items, setItems] = useState<Item[]>([
    { id: '1', title: 'Item 1' },
    { id: '2', title: 'Item 2' },
  ]);
  const [selectedItem, setSelectedItem] = useState<Item | null>(null);

  return (
    <MasterDetailLayout
      master={
        <div className="space-y-2">
          {items.map((item) => (
            <button
              key={item.id}
              onClick={() => setSelectedItem(item)}
              className={`w-full p-2 text-left ${
                selectedItem?.id === item.id 
                  ? 'bg-pf-accent text-white' 
                  : 'hover:bg-pf-bg-2'
              }`}
            >
              {item.title}
            </button>
          ))}
        </div>
      }
      detail={
        selectedItem && (
          <div className="p-4 space-y-4">
            <h2>{selectedItem.title}</h2>
            <p>Item details for: {selectedItem.id}</p>
            {/* Edit form, details, etc. */}
          </div>
        )
      }
      hasDetail={selectedItem !== null}
      detailTitle={selectedItem?.title}
      onCloseDetail={() => setSelectedItem(null)}
      masterWidth="w-80"  // Custom sidebar width
    />
  );
}

// With custom styling
function StyledItemManager() {
  const [selected, setSelected] = useState<Item | null>(null);

  return (
    <MasterDetailLayout
      master={<ItemList onSelect={setSelected} />}
      detail={<ItemDetail item={selected} />}
      hasDetail={selected !== null}
      detailTitle={selected?.title}
      onCloseDetail={() => setSelected(null)}
      masterClassName="bg-pf-bg-2 border-r border-pf-border"
      detailClassName="bg-pf-bg-1"
      breakpoint="lg"  // Use 'lg' breakpoint instead of 'md'
    />
  );
}
```

**Component Props**:
```tsx
interface MasterDetailLayoutProps {
  // Content
  master: React.ReactNode;         // Master panel/sidebar content
  detail: React.ReactNode;         // Detail panel content
  
  // State
  hasDetail: boolean;              // Whether to show detail panel
  detailTitle?: string;            // Title shown in mobile detail header
  
  // Callbacks
  onCloseDetail: () => void;       // Called when user closes detail on mobile
  
  // Styling
  masterWidth?: string;            // Tailwind width class (default: 'w-80')
  masterClassName?: string;        // Additional master panel classes
  detailClassName?: string;        // Additional detail panel classes
  
  // Responsive
  breakpoint?: 'sm' | 'md' | 'lg' | 'xl' | '2xl';  // Breakpoint for layout switch (default: 'md')
}
```

**Responsive Behavior**:

**Desktop (1024px+, `md` breakpoint and up)**:
- Side-by-side layout: master on left, detail on right
- Master panel has fixed width (default `w-80` = 320px)
- Detail panel takes remaining space
- Both visible simultaneously
- Clicking items in master updates detail immediately

**Mobile (<1024px)**:
- Master panel displayed by default
- Tapping an item shows detail panel (full screen)
- Detail header shows back button + title
- Clicking back button hides detail, returns to master list
- Only one view visible at a time

**Breakpoint Configuration**:
The layout switches from mobile to desktop at the specified breakpoint:
- `sm`: 640px
- `md`: 768px (default)
- `lg`: 1024px
- `xl`: 1280px
- `2xl`: 1536px

**Features**:
- Full responsive behavior without media queries needed in parent
- WCAG accessible with proper ARIA labels
- Mobile back button with title for context
- Smooth transition between list and detail
- Master panel sticky on desktop, scrollable on mobile
- Detail panel scrollable with fixed header on mobile
- TypeScript support with full prop typing
- Works with any content (lists, grids, forms, etc.)

**Best Practices**:
- Keep master panel simple and scannable (list of items, buttons, etc.)
- Detail panel should show full content/edit form for selected item
- Set `hasDetail={false}` when no item selected to show only master
- Use appropriate `detailTitle` for mobile context
- Ensure master content is keyboard navigable
- Pair with `useKeyboardNavigation` for better UX
- Test responsive behavior on actual mobile devices
- Master width should be proportional to content (320-384px typical)

**Styling Notes**:
- Uses Tailwind CSS for layout and responsive design
- Respects PrintFarmer color tokens (`pf-bg-*`, `pf-border`, etc.)
- Master and detail have separate scrollable containers
- Mobile detail header is non-scrollable for consistent back button placement
- Borders and backgrounds can be customized via `masterClassName` / `detailClassName`

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
