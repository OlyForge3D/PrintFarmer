# PrintFarmer Design System

Complete reference for the PrintFarmer UI component library, design tokens, and usage patterns.

## Overview

PrintFarmer includes a comprehensive, production-grade design system built with **React TypeScript**, **Tailwind CSS v4**, and **CSS Custom Properties** (design tokens). The system provides:

- **40+ reusable React components** with consistent styling
- **Dynamic theming** via CSS custom properties (`pf-*` tokens)
- **Three theme variants**: GitHub Dark (default), PrintFarmer Dark, and Light
- **Accessibility-first design** (WCAG 2.2 Level AA compliance)
- **Responsive, mobile-ready** layout utilities

This document is the single source of truth for UI component usage in PrintFarmer.

---

## Architecture

### Three-Layer Design System

```
Layer 3: React Components        (Button, Input, Card, Modal, etc.)
         ├─ Composed of Tailwind utilities + design tokens
         ├─ Typed props, accessible, keyboard navigable
         └─ Reusable across entire application

Layer 2: Tailwind Utilities      (flex, gap-4, rounded-lg, etc.)
         ├─ Standard Tailwind classes
         ├─ Custom utilities (pf-skeleton, pf-animate-spin, etc.)
         └─ Responsive modifiers (md:, lg:, dark:)

Layer 1: CSS Custom Properties   (--pf-bg-0, --pf-accent, etc.)
         ├─ Theme-aware variables
         ├─ GitHub Dark, PrintFarmer Dark, Light themes
         └─ Supports dynamic theme switching via data-theme attribute
```

**Key Files:**
- `src/Web/ReactApp/src/common/components/ui/` — React components (Button, Input, Card, etc.)
- `src/Web/ReactApp/src/styles/theme.css` — CSS variable orchestrator and default (GitHub Dark) theme
- `src/Web/ReactApp/src/styles/themes/*.css` — Theme variant definitions (printfarmer-dark, light)
- `src/Web/ReactApp/src/index.css` — Global styles, `@theme` block for design tokens, and custom utilities

---

## Design Tokens (pf-* CSS Variables)

All design tokens use the `pf-` prefix and are managed through CSS custom properties. This enables **dynamic theme switching** without rebuilding.

### Color Palette

#### Primary Backgrounds
| Token | Default (GitHub Dark) | Usage |
|-------|----------------------|-------|
| `--pf-bg-0` | `#0d1117` | Main page background, base layer |
| `--pf-bg-1` | `#161b22` | Secondary background, cards, modals |
| `--pf-bg-2` | `#21262d` | Tertiary background, nested panels |
| `--pf-panel` | `#161b22` | Panel/container background |
| `--pf-card-bg` | `#161b22` | Card-specific background |
| `--pf-sidebar-bg` | `#161b22` | Sidebar background |

#### Text Colors
| Token | Default (GitHub Dark) | Contrast | Usage |
|-------|----------------------|----------|-------|
| `--pf-text-primary` | `#c9d1d9` | 13.6:1 | Body text, primary labels |
| `--pf-text-secondary` | `#8b949e` | 6.5:1 | Secondary text, muted labels |
| `--pf-text-tertiary` | `#6e7681` | 4.6:1 | Tertiary text, hints |
| `--pf-text-light` | `#c9d1d9` | 13.6:1 | Light text (same as primary) |
| `--pf-text-muted` | `#8b949e` | 6.5:1 | Muted text, disabled state |

#### Accent & Brand Colors
| Token | Default (GitHub Dark) | Usage |
|-------|----------------------|-------|
| `--pf-accent` | `#58a6ff` | Primary accent text, links, highlights |
| `--pf-accent-bg` | `#0969da` | Primary button backgrounds |
| `--pf-accent-hover` | (derived) | Hover state for accent buttons |
| `--pf-accent-2` | `#58a6ff` | Secondary accent (blue) |
| `--pf-success` | `#3fb950` | Success text, online indicators |
| `--pf-success-bg` | `#238636` | Success button backgrounds |
| `--pf-success-hover` | `#2ea043` | Success button hover state |

#### Status Colors
| Token | Default | Usage |
|-------|---------|-------|
| `--pf-status-online-bg` | `#0d1117` | Online printer status background |
| `--pf-status-online-text` | `#3fb950` | Online printer status text (green) |
| `--pf-status-online-border` | `#238636` | Online printer status border |
| `--pf-status-offline-bg` | `#0d1117` | Offline printer status background |
| `--pf-status-offline-text` | `#f85149` | Offline printer status text (red) |
| `--pf-status-offline-border` | `#da3633` | Offline printer status border |

#### Error & Warning
| Token | Default | Contrast | Usage |
|-------|---------|----------|-------|
| `--pf-error` | `#f85149` | 7.8:1 | Error text, validation errors |
| `--pf-error-bg` | `#0d1117` | — | Error background |
| `--pf-error-text` | `#f85149` | — | Error message text |
| `--pf-error-border` | `#da3633` | — | Error border |
| `--pf-warning` | `#d29922` | 5.1:1 | Warning text |
| `--pf-warning-text` | `#d29922` | — | Warning message text |

#### Borders
| Token | Default | Usage |
|-------|---------|-------|
| `--pf-border` | `#30363d` | Main border color |
| `--pf-border-light` | `#484f58` | Light/subtle borders |
| `--pf-border-medium` | `#30363d` | Medium borders |
| `--pf-border-dark` | `#21262d` | Dark borders |
| `--pf-border-gray` | `#6e7681` | Gray borders |

#### Gradient Colors (Buttons, Effects)
| Token | Default | Usage |
|-------|---------|-------|
| `--pf-gradient-primary-start` | `#161b22` | Primary button gradient start |
| `--pf-gradient-primary-end` | `#0d1117` | Primary button gradient end |
| `--pf-gradient-secondary-start` | `#21262d` | Secondary button gradient start |
| `--pf-gradient-secondary-end` | `#161b22` | Secondary button gradient end |
| `--pf-gradient-success-start` | `#1a6b2f` | Success button gradient start |
| `--pf-gradient-success-end` | `#238636` | Success button gradient end |
| `--pf-gradient-gray-start` | `#6e7681` | Gray gradient start |
| `--pf-gradient-gray-end` | `#484f58` | Gray gradient end |

#### State & Accessibility
| Token | Default | Usage |
|-------|---------|-------|
| `--pf-focus-ring` | `rgba(88, 166, 255, 0.5)` | Focus ring/outline color |
| `--pf-focus-ring-offset` | `#0d1117` | Focus ring offset background |
| `--pf-disabled` | `#6e7681` | Disabled text color |
| `--pf-loading` | `#58a6ff` | Loading spinner/indicator color |
| `--pf-skeleton-bg` | `#21262d` | Skeleton placeholder background |
| `--pf-skeleton-bg-alt` | `#30363d` | Alternative skeleton background |

### Using Design Tokens in Code

**Tailwind Classes (Recommended)**
```jsx
// Backgrounds
<div className="bg-pf-bg-0">Main background</div>
<div className="bg-pf-panel">Panel background</div>

// Text colors
<p className="text-pf-text-primary">Primary text</p>
<p className="text-pf-text-secondary">Secondary text</p>

// Borders
<div className="border border-pf-border">Border</div>
<div className="border border-pf-error">Error border</div>

// Accents
<div className="bg-pf-accent-bg text-white">Accent button</div>
<span className="text-pf-success">Success</span>

// Combinations
<div className="bg-pf-panel border border-pf-border rounded-lg p-4 text-pf-text-primary">
  Styled card
</div>
```

**Direct CSS Variables**
```css
.custom-element {
  background-color: var(--pf-bg-1);
  color: var(--pf-text-primary);
  border-color: var(--pf-border);
}

.custom-button {
  background: linear-gradient(
    to bottom,
    var(--pf-gradient-primary-start),
    var(--pf-gradient-primary-end)
  );
  color: white;
}
```

---

## Available Themes

### 1. GitHub Dark (Default)
**Identifier**: No `data-theme` attribute or `data-theme="github-dark"`
**Description**: GitHub's official dark theme colors, based on GitHub.com UI
**Best for**: Developers familiar with GitHub, minimal contrast issues
**Key Colors**: Blues (#58a6ff), Greens (#3fb950), Reds (#f85149)

### 2. PrintFarmer Dark
**Identifier**: `data-theme="printfarmer-dark"`
**Description**: Custom dark theme with adjusted contrast for better readability
**Best for**: Extended viewing sessions, print-heavy workflows
**Key Colors**: Greens (#10b981), Blues (#1d4ed8), Reds (#dc2626)

### 3. Light
**Identifier**: `data-theme="light"`
**Description**: High-contrast light theme for daylight environments
**Best for**: Daytime use, accessibility, paper-like appearance
**Key Colors**: Blues (#059669), Greens (#047857), Reds (#dc2626)

**Switching Themes:**
```jsx
// Set theme globally (applies to entire app)
document.documentElement.setAttribute('data-theme', 'light');

// Remove theme attribute to revert to default (GitHub Dark)
document.documentElement.removeAttribute('data-theme');

// In React with ThemeContext (if implemented)
const { setTheme } = useTheme();
setTheme('printfarmer-dark');
```

---

## Component Library

All components are located in `src/Web/ReactApp/src/common/components/ui/` and exported via the barrel file `index.ts`.

### Import Pattern
```tsx
// ✅ Correct: Use barrel export
import { Button, Input, Select, Card } from '@/common/components/ui';

// ❌ Avoid: Direct imports (not optimized for code splitting)
import Button from '@/common/components/ui/Button';
```

---

## Components Reference

### Button

Standardized button with multiple variants, sizes, and states.

**Props:**
```tsx
interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'secondary' | 'danger' | 'subtle' | 'ghost' | 'success' | 'tab' | 'toggle' | 'link' | 'unstyled';
  size?: 'sm' | 'md' | 'lg';
  loading?: boolean;
  iconLeft?: React.ReactNode;
  iconRight?: React.ReactNode;
  iconCenter?: React.ReactNode;
  active?: boolean; // For tab variant
  children?: React.ReactNode;
}
```

**Variants:**
| Variant | Use Case | Example |
|---------|----------|---------|
| `primary` | Primary action, submit | "Save", "Create", "Add" |
| `secondary` | Secondary action, cancel | "Cancel", "Reset", "Clear" |
| `danger` | Destructive action | "Delete", "Remove", "Revoke" |
| `success` | Positive action | "Approve", "Publish", "Confirm" |
| `subtle` | Low-emphasis action | "View Details", "Learn More" |
| `ghost` | Blend into background | Icon-only, toolbar buttons |
| `tab` | Tab navigation | Tab bars, segmented controls |
| `toggle` | Toggle state | On/off controls |
| `link` | Link-style button | Inline actions |
| `unstyled` | Full custom control | When you need to override everything |

**Usage:**
```tsx
import { Button } from '@/common/components/ui';

// Basic
<Button>Click me</Button>

// Variants
<Button variant="primary">Save</Button>
<Button variant="danger">Delete</Button>
<Button variant="success">Approve</Button>
<Button variant="secondary">Cancel</Button>

// Sizes
<Button size="sm">Small</Button>
<Button size="md">Medium (default)</Button>
<Button size="lg">Large</Button>

// States
<Button disabled>Disabled</Button>
<Button loading>Processing...</Button>

// With icons
<Button iconLeft={<PlusIcon />}>Add Item</Button>
<Button iconRight={<ChevronDownIcon />}>Menu</Button>
<Button iconCenter={<SpinnerIcon />} />

// Tab variant with active state
<Button variant="tab" active>Active Tab</Button>
<Button variant="tab">Inactive Tab</Button>
```

---

### Input

Text input field with validation states.

**Props:**
```tsx
interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
  invalid?: boolean;
}
```

**Usage:**
```tsx
import { Input } from '@/common/components/ui';

<Input 
  placeholder="Enter name"
  type="text"
/>

<Input 
  invalid={!!errors.email}
  placeholder="Email address"
  type="email"
/>

<Input 
  disabled
  value="Read-only value"
/>
```

**With FormField (Recommended):**
```tsx
import { FormField, Input } from '@/common/components/ui';

<FormField 
  label="Email" 
  htmlFor="email"
  error={errors.email}
  required
>
  <Input 
    id="email"
    type="email"
    invalid={!!errors.email}
    placeholder="name@example.com"
  />
</FormField>
```

---

### Select

Dropdown select field with chevron indicator.

**Props:**
```tsx
interface SelectProps extends React.SelectHTMLAttributes<HTMLSelectElement> {
  invalid?: boolean;
  containerClassName?: string;
}
```

**Usage:**
```tsx
import { Select, FormField } from '@/common/components/ui';

// Basic
<Select>
  <option value="">Select an option</option>
  <option value="option1">Option 1</option>
  <option value="option2">Option 2</option>
</Select>

// With FormField
<FormField label="Backend Type" htmlFor="backend">
  <Select id="backend" invalid={!!errors.backend}>
    <option value="">Choose backend</option>
    <option value="moonraker">Moonraker (Klipper)</option>
    <option value="prusalink">PrusaLink (Prusa)</option>
    <option value="octoprint">OctoPrint</option>
  </Select>
</FormField>

// Disabled
<Select disabled>
  <option>No options available</option>
</Select>
```

---

### FormField

Wrapper component combining label, input, error message, and helper text.

**Props:**
```tsx
interface FormFieldProps {
  label?: string;
  htmlFor?: string;
  error?: string;
  helper?: string;
  required?: boolean;
  inline?: boolean;
  children: React.ReactNode;
}
```

**Usage:**
```tsx
import { FormField, Input } from '@/common/components/ui';

<FormField
  label="Printer Name"
  htmlFor="printer-name"
  required
  error={errors.name}
  helper="Enter a unique name for easy identification"
>
  <Input 
    id="printer-name"
    placeholder="My Printer"
    invalid={!!errors.name}
  />
</FormField>

// Inline layout
<FormField label="Online" inline>
  <Toggle value={isOnline} onChange={setIsOnline} />
</FormField>
```

---

### Card

Container component with optional header/footer subcomponents.

**Props:**
```tsx
interface CardProps {
  children: React.ReactNode;
  className?: string;
  title?: string;
  hoverable?: boolean;
  onClick?: () => void;
}
```

**Usage:**
```tsx
import { Card } from '@/common/components/ui';

// Simple card
<Card>
  <p>Card content</p>
</Card>

// Card with title
<Card title="Printer Status">
  <p>Status details</p>
</Card>

// Card with structure
<Card>
  <Card.Header>
    <h3>Title</h3>
  </Card.Header>
  <Card.Body>
    <p>Body content</p>
  </Card.Body>
  <Card.Footer>
    <Button>Action</Button>
  </Card.Footer>
</Card>

// Hoverable/interactive card
<Card hoverable onClick={() => navigate(`/printers/${id}`)}>
  <p>Click to view printer</p>
</Card>
```

---

### Badge

Small badge/tag component for status, labels, or indicators.

**Props:**
```tsx
interface BadgeProps {
  children: React.ReactNode;
  variant?: 'default' | 'primary' | 'success' | 'warning' | 'error' | 'info';
  size?: 'sm' | 'md';
  dot?: boolean;
  className?: string;
}
```

**Usage:**
```tsx
import { Badge } from '@/common/components/ui';

// Text badge
<Badge variant="success">Approved</Badge>
<Badge variant="warning">Pending</Badge>
<Badge variant="error">Failed</Badge>

// Dot indicator
<Badge variant="success" dot /> {/* Green dot */}
<Badge variant="offline" dot /> {/* Red dot */}

// Sizes
<Badge size="sm">Small</Badge>
<Badge size="md">Medium</Badge>

// In context
<div className="flex items-center gap-2">
  <span>Printer Status:</span>
  <Badge variant={isOnline ? 'success' : 'error'}>
    {isOnline ? 'Online' : 'Offline'}
  </Badge>
</div>
```

---

### Alert

Alert/notification component for success, error, warning, info messages.

**Props:**
```tsx
interface AlertProps {
  variant?: 'success' | 'error' | 'warning' | 'info';
  title?: string;
  children: React.ReactNode;
  onClose?: () => void;
  className?: string;
}
```

**Usage:**
```tsx
import { Alert } from '@/common/components/ui';

<Alert variant="success" title="Success">
  Your changes have been saved successfully.
</Alert>

<Alert variant="error" title="Error">
  Failed to delete printer. Please try again.
</Alert>

<Alert variant="warning">
  This action cannot be undone.
</Alert>

<Alert variant="info" onClose={() => setDismissed(true)}>
  New version available. <a href="/update">Update now</a>
</Alert>
```

---

### Spinner

Loading spinner with size options.

**Props:**
```tsx
interface SpinnerProps {
  size?: 'sm' | 'md' | 'lg';
  className?: string;
}
```

**Usage:**
```tsx
import { Spinner } from '@/common/components/ui';

// In a loading state
{isLoading ? <Spinner /> : <Content />}

// Inline with text
<div className="flex items-center gap-2">
  <Spinner size="sm" />
  <span>Loading...</span>
</div>

// As page loader
<div className="flex justify-center items-center h-96">
  <Spinner size="lg" />
</div>
```

---

### Modal

Dialog/modal overlay component with customizable footer.

**Props:**
```tsx
interface ModalProps {
  isOpen: boolean;
  onClose: () => void;
  title?: string;
  children: React.ReactNode;
  footer?: React.ReactNode;
  size?: 'sm' | 'md' | 'lg' | 'xl' | 'full';
  titleIcon?: React.ReactNode;
  closeOnBackdrop?: boolean;
  closeOnEscape?: boolean;
  showCloseButton?: boolean;
  closeButtonVariant?: ButtonVariant;
  closeButtonClassName?: string;
  className?: string;
  maxHeight?: string;
  isDisabled?: boolean;
}
```

**Usage:**
```tsx
import { Modal, Button } from '@/common/components/ui';
import { useState } from 'react';

export function ExampleModal() {
  const [isOpen, setIsOpen] = useState(false);

  return (
    <>
      <Button onClick={() => setIsOpen(true)}>Open Modal</Button>

      <Modal
        isOpen={isOpen}
        onClose={() => setIsOpen(false)}
        title="Create Printer"
        size="md"
        footer={
          <>
            <Button variant="secondary" onClick={() => setIsOpen(false)}>
              Cancel
            </Button>
            <Button variant="primary" onClick={handleSubmit}>
              Create
            </Button>
          </>
        }
      >
        <form>
          <FormField label="Name">
            <Input placeholder="Printer name" />
          </FormField>
        </form>
      </Modal>
    </>
  );
}
```

---

### Tabs

Tabbed content navigation component.

**Props:**
```tsx
interface TabsProps {
  defaultTab?: string;
  activeTab?: string;
  onTabChange?: (tabId: string) => void;
  children: React.ReactNode;
  className?: string;
}
```

**Usage:**
```tsx
import { Tabs } from '@/common/components/ui';

<Tabs defaultTab="status">
  <Tabs.List>
    <Tabs.Tab id="status">Status</Tabs.Tab>
    <Tabs.Tab id="settings">Settings</Tabs.Tab>
    <Tabs.Tab id="history">History</Tabs.Tab>
  </Tabs.List>

  <Tabs.Panels>
    <Tabs.Panel id="status">
      <p>Printer status information</p>
    </Tabs.Panel>
    <Tabs.Panel id="settings">
      <p>Printer settings form</p>
    </Tabs.Panel>
    <Tabs.Panel id="history">
      <p>Printer job history</p>
    </Tabs.Panel>
  </Tabs.Panels>
</Tabs>
```

---

### DataTable

Table component with sorting, keyboard navigation, and row selection.

**Props:**
```tsx
interface DataTableProps<T> {
  data: T[];
  columns: DataTableColumn<T>[];
  getRowKey: (item: T) => string | number;
  keyboardNavigation?: boolean;
  defaultSortColumn?: string;
  defaultSortDirection?: 'asc' | 'desc';
  onRowSelect?: (item: T, index: number) => void;
  onRowFocus?: (item: T, index: number) => void;
  renderActions?: (item: T) => React.ReactNode;
  actionsHeader?: React.ReactNode;
}
```

**Usage:**
```tsx
import { DataTable } from '@/common/components/ui';

<DataTable
  data={printers}
  columns={[
    {
      key: 'name',
      header: 'Printer Name',
      sortable: true,
      sort: (a, b) => a.name.localeCompare(b.name),
      render: (item) => item.name,
    },
    {
      key: 'status',
      header: 'Status',
      render: (item) => (
        <Badge variant={item.isOnline ? 'success' : 'error'}>
          {item.isOnline ? 'Online' : 'Offline'}
        </Badge>
      ),
    },
    {
      key: 'model',
      header: 'Model',
      render: (item) => item.model.name,
    },
  ]}
  getRowKey={(printer) => printer.id}
  renderActions={(printer) => (
    <Button size="sm" variant="secondary" onClick={() => editPrinter(printer.id)}>
      Edit
    </Button>
  )}
  keyboardNavigation
  onRowSelect={(printer) => navigate(`/printers/${printer.id}`)}
/>
```

---

### Toggle

Switch/toggle component for boolean values.

**Props:**
```tsx
interface ToggleProps extends React.InputHTMLAttributes<HTMLInputElement> {
  onLabel?: string;
  offLabel?: string;
}
```

**Usage:**
```tsx
import { Toggle } from '@/common/components/ui';

<Toggle 
  checked={isEnabled}
  onChange={(e) => setIsEnabled(e.target.checked)}
/>

<FormField label="Enable Auto-Dispatch" inline>
  <Toggle 
    checked={isAutoDispatchEnabled}
    onChange={(e) => setAutoDispatchEnabled(e.target.checked)}
  />
</FormField>
```

---

### FileUpload

File input with drag-and-drop support.

**Props:**
```tsx
interface FileUploadProps extends React.InputHTMLAttributes<HTMLInputElement> {
  accept?: string;
  multiple?: boolean;
  onFilesSelected?: (files: File[]) => void;
  onError?: (error: string) => void;
}
```

**Usage:**
```tsx
import { FileUpload } from '@/common/components/ui';

<FileUpload
  accept=".gcode,.nc"
  multiple={false}
  onFilesSelected={([file]) => uploadGcode(file)}
  onError={(error) => toast.error(error)}
/>
```

---

### Checkbox, Radio, RadioGroup

Selection controls for single/multiple options.

**Usage:**
```tsx
import { Checkbox, Radio, RadioGroup } from '@/common/components/ui';

// Checkbox
<Checkbox 
  id="remember"
  checked={rememberMe}
  onChange={(e) => setRememberMe(e.target.checked)}
/>
<label htmlFor="remember">Remember me</label>

// Radio button
<Radio 
  id="option1"
  name="choice"
  value="option1"
  checked={choice === 'option1'}
  onChange={(e) => setChoice(e.target.value)}
/>
<label htmlFor="option1">Option 1</label>

// RadioGroup (with helper)
<RadioGroup
  value={selectedOption}
  onChange={setSelectedOption}
  options={[
    { value: 'a', label: 'Option A' },
    { value: 'b', label: 'Option B' },
    { value: 'c', label: 'Option C' },
  ]}
/>
```

---

### Textarea

Multi-line text input.

**Props:**
```tsx
interface TextareaProps extends React.TextareaHTMLAttributes<HTMLTextAreaElement> {
  invalid?: boolean;
}
```

**Usage:**
```tsx
import { Textarea } from '@/common/components/ui';

<Textarea 
  placeholder="Enter description"
  rows={4}
/>

<Textarea 
  invalid={!!errors.description}
  value={description}
  onChange={(e) => setDescription(e.target.value)}
/>
```

---

### ProgressBar

Progress indicator bar.

**Props:**
```tsx
interface ProgressBarProps {
  value: number; // 0-100
  variant?: 'success' | 'warning' | 'error' | 'info';
  showLabel?: boolean;
  label?: string;
  className?: string;
}
```

**Usage:**
```tsx
import { ProgressBar } from '@/common/components/ui';

<ProgressBar value={65} showLabel />
<ProgressBar value={100} variant="success" label="Complete" />
<ProgressBar value={45} variant="warning" />
```

---

### Tooltip

Hover tooltip with positioning.

**Props:**
```tsx
interface TooltipProps {
  content: React.ReactNode;
  position?: 'top' | 'bottom' | 'left' | 'right';
  children: React.ReactNode;
  delay?: number;
  className?: string;
}
```

**Usage:**
```tsx
import { Tooltip } from '@/common/components/ui';

<Tooltip content="Click to edit">
  <Button variant="ghost">Edit</Button>
</Tooltip>

<Tooltip content="Online" position="top">
  <Badge variant="success" dot />
</Tooltip>
```

---

## Usage Patterns

### Form with Validation

```tsx
import { FormField, Input, Select, Button, Alert } from '@/common/components/ui';
import { useState } from 'react';

export function PrinterForm() {
  const [formData, setFormData] = useState({ name: '', backend: '' });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [success, setSuccess] = useState(false);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    
    // Validate
    const newErrors: Record<string, string> = {};
    if (!formData.name.trim()) newErrors.name = 'Name is required';
    if (!formData.backend) newErrors.backend = 'Backend is required';
    
    if (Object.keys(newErrors).length > 0) {
      setErrors(newErrors);
      return;
    }

    // Submit
    savePrinter(formData);
    setSuccess(true);
    setFormData({ name: '', backend: '' });
  };

  return (
    <div className="max-w-md">
      {success && (
        <Alert variant="success" title="Success">
          Printer has been saved.
        </Alert>
      )}

      <form onSubmit={handleSubmit} className="space-y-4">
        <FormField
          label="Printer Name"
          htmlFor="name"
          required
          error={errors.name}
        >
          <Input
            id="name"
            value={formData.name}
            onChange={(e) => setFormData({ ...formData, name: e.target.value })}
            invalid={!!errors.name}
            placeholder="Enter name"
          />
        </FormField>

        <FormField
          label="Backend Type"
          htmlFor="backend"
          required
          error={errors.backend}
        >
          <Select
            id="backend"
            value={formData.backend}
            onChange={(e) => setFormData({ ...formData, backend: e.target.value })}
            invalid={!!errors.backend}
          >
            <option value="">Select backend</option>
            <option value="moonraker">Moonraker</option>
            <option value="prusalink">PrusaLink</option>
          </Select>
        </FormField>

        <Button type="submit" variant="primary">
          Save Printer
        </Button>
      </form>
    </div>
  );
}
```

---

### Data Presentation with DataTable

```tsx
import { DataTable, Badge, Button } from '@/common/components/ui';
import { useQuery } from '@tanstack/react-query';

export function PrintersTable() {
  const { data: printers = [], isLoading } = useQuery({
    queryKey: ['printers'],
    queryFn: () => apiClient.getPrinters(),
  });

  if (isLoading) return <Spinner />;

  return (
    <DataTable
      data={printers}
      columns={[
        {
          key: 'name',
          header: 'Printer Name',
          sortable: true,
          sort: (a, b) => a.name.localeCompare(b.name),
          render: (printer) => printer.name,
        },
        {
          key: 'status',
          header: 'Status',
          render: (printer) => (
            <Badge variant={printer.isOnline ? 'success' : 'error'}>
              {printer.isOnline ? 'Online' : 'Offline'}
            </Badge>
          ),
        },
        {
          key: 'backend',
          header: 'Backend',
          render: (printer) => printer.backend,
        },
      ]}
      getRowKey={(printer) => printer.id}
      renderActions={(printer) => (
        <Button
          size="sm"
          variant="secondary"
          onClick={() => editPrinter(printer.id)}
        >
          Edit
        </Button>
      )}
      keyboardNavigation
      onRowSelect={(printer) => navigate(`/printers/${printer.id}`)}
    />
  );
}
```

---

## Accessibility Features

All PrintFarmer components are built with accessibility in mind:

### Keyboard Navigation
- **Tab** — Move between interactive elements
- **Arrow keys** — Navigate within composite components (tabs, menus, tables)
- **Enter/Space** — Activate buttons, toggle switches, select options
- **Escape** — Close modals, menus, dropdowns

### Screen Reader Support
- Semantic HTML (`<button>`, `<label>`, `<input>`, etc.)
- ARIA attributes for complex components (`aria-label`, `aria-describedby`, `aria-expanded`, etc.)
- Form labels associated via `htmlFor` attributes
- Error messages programmatically linked via `aria-describedby`

### Visual Accessibility
- **Minimum contrast ratios**: 4.5:1 for normal text, 3:1 for large text (WCAG AA)
- **Focus indicators**: Visible focus ring (default blue)
- **Reduced motion**: Respects `prefers-reduced-motion` for animations
- **Color-independent**: Status not conveyed by color alone (icons, text, borders used)

### Testing Accessibility
Use tools like:
- [Accessibility Insights](https://accessibilityinsights.io/) — Browser extension for automated checks
- [WAVE](https://wave.webaim.org/) — Web accessibility evaluation tool
- [axe DevTools](https://www.deque.com/axe/devtools/) — Accessibility testing

---

## Common Patterns

### Conditional Rendering with Loading/Error States

```tsx
import { Spinner, Alert, DataTable } from '@/common/components/ui';

export function PrintersPage() {
  const { data, isLoading, error } = useQuery({
    queryKey: ['printers'],
    queryFn: getPrinters,
  });

  if (isLoading) {
    return <Spinner size="lg" />;
  }

  if (error) {
    return (
      <Alert variant="error" title="Failed to load">
        {error.message}
      </Alert>
    );
  }

  if (data?.length === 0) {
    return (
      <Alert variant="info">
        No printers found. <a href="/add-printer">Add one</a>
      </Alert>
    );
  }

  return <DataTable data={data} columns={columns} />;
}
```

---

### Modal with Form

```tsx
import { Modal, FormField, Input, Button } from '@/common/components/ui';
import { useState } from 'react';

export function AddPrinterModal({ isOpen, onClose }) {
  const [name, setName] = useState('');
  const [error, setError] = useState('');
  const [isLoading, setIsLoading] = useState(false);

  const handleSubmit = async () => {
    if (!name.trim()) {
      setError('Name is required');
      return;
    }

    setIsLoading(true);
    try {
      await addPrinter({ name });
      onClose();
    } catch (err) {
      setError(err.message);
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Add Printer"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            loading={isLoading}
            disabled={isLoading}
          >
            Add Printer
          </Button>
        </>
      }
    >
      {error && <Alert variant="error">{error}</Alert>}
      <FormField label="Printer Name" htmlFor="name" required>
        <Input
          id="name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Enter name"
        />
      </FormField>
    </Modal>
  );
}
```

---

## Best Practices

### Do's
✅ Use the component library for consistency  
✅ Use semantic HTML elements (`<button>`, `<input>`, `<label>`)  
✅ Combine components for complex UIs  
✅ Use `pf-*` color tokens via Tailwind classes  
✅ Test with keyboard and screen readers  
✅ Provide clear error messages  

### Don'ts
❌ Create raw HTML `<button>`, `<input>`, `<select>` elements  
❌ Hardcode colors (use `pf-*` tokens)  
❌ Skip labels on form inputs  
❌ Use `aria-label` as a substitute for visible labels  
❌ Disable buttons without visual feedback  
❌ Ignore focus states  

---

## Troubleshooting

### "Component not found" error
- Check the component is exported from `src/Web/ReactApp/src/common/components/ui/index.ts`
- Verify you're importing from `@/common/components/ui` (not a direct path)

### Styles not applying
- Ensure the component class uses `bg-pf-*`, `text-pf-*`, `border-pf-*` tokens
- Verify component files are included in Tailwind's content detection (Tailwind v4 auto-detects `.tsx` files)
- Run Tailwind rebuild: `npm run build`

### Focus ring not visible
- Check `--pf-focus-ring` CSS variable is defined for your theme
- Ensure `focus:ring-2 focus:ring-pf-accent` classes are present on the element
- Don't override with `outline: none` without providing an alternative

### Theme not switching
- Ensure `data-theme` attribute is set on `<html>` or `<root>` element
- Verify CSS variable overrides exist in the target theme file
- Check browser DevTools to confirm CSS variables are recalculated

---

## Resources

- [Tailwind CSS Documentation](https://tailwindcss.com/docs)
- [WCAG 2.2 Accessibility Guidelines](https://www.w3.org/WAI/WCAG22/quickref/)
- [MDN: ARIA Attributes](https://developer.mozilla.org/en-US/docs/Web/Accessibility/ARIA)
- [React Hook Form](https://react-hook-form.com/) — For complex form management
- [TanStack React Query](https://tanstack.com/query/latest) — For server state management

---

## Contributing

When adding new components:

1. **Create the component** in `src/Web/ReactApp/src/common/components/ui/`
2. **Use `pf-*` tokens** for all colors/styling via Tailwind classes
3. **Export from `index.ts`** barrel file
4. **Add TypeScript interfaces** for props
5. **Include JSDoc comments** with `@example` code blocks
6. **Write Vitest tests** in `__tests__/` subdirectory
7. **Update this documentation** with usage examples and props
8. **Test accessibility** with keyboard and screen readers

---

**Last Updated:** 2026-03-21  
**Maintained by:** Ash (Documentation Specialist)
