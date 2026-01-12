# PrintFarmer Explicit Controls Stylesheet Guide

## Overview

The PrintFarmer app now has a **centralized, explicit controls stylesheet** (`src/styles/controls.css`) that defines reusable styles for all common UI controls. This ensures consistency, ease of maintenance, and makes it simple for developers to add new controls with the correct styling.

## Philosophy

- **One source of truth**: All control styles are defined in `controls.css`
- **Explicit over implicit**: Classes clearly indicate what they style (`.btn-primary`, `.input-invalid`, etc.)
- **Easy to extend**: New developers can quickly find where to add a new control type
- **No duplication**: Never duplicate these styles in component-specific CSS modules
- **CSS-only**: Uses pure CSS (no @apply), compatible with all CSS processors

## Organization

The stylesheet is organized into logical sections:

### 1. Input Controls (Lines 28–334)
Text inputs, textareas, selects, checkboxes, radio buttons, range sliders, file uploads.

**Available classes:**
- `.input-base` - Base styles for inputs
- `.input-sm`, `.input-lg` - Size variants
- `.input-invalid` - Error state styling
- `.input-disabled` - Disabled state styling
- `.input-readonly` - Read-only styling

**Direct element styling:**
- `input[type="text"]`, `input[type="email"]`, etc.
- `textarea`
- `select`
- `input[type="checkbox"]`
- `input[type="radio"]`
- `input[type="range"]`
- `input[type="file"]`

### 2. Buttons & Actions (Lines 336–489)
All button variants and states.

**Available classes:**
- `.btn-base` - Base button styles
- `.btn-sm`, `.btn-md`, `.btn-lg` - Size variants
- `.btn-primary` - Primary action (default)
- `.btn-secondary` - Secondary action
- `.btn-success` - Successful/positive action
- `.btn-danger` - Destructive action
- `.btn-subtle` - Low emphasis
- `.btn-ghost` - Minimal styling
- `.btn-icon` - Icon-only button (small square)
- `.btn-link` - Text link button

**Direct element styling:**
- `button` - Default button styling

### 3. Form Elements (Lines 491–547)
Labels, helpers, errors, fieldsets.

**Available classes:**
- `.form-label` - Label styling (with `.required` modifier for asterisk)
- `.form-helper` - Helper text below inputs
- `.form-error` - Error message text
- `.form-group` - Container (with `.inline` modifier for horizontal layout)
- `.form-control` - Input wrapper

**Direct element styling:**
- `fieldset`
- `legend`

### 4. Alerts & Feedback (Lines 549–631)
Alerts, toasts, tooltips.

**Available classes:**
- `.alert-base` - Base alert styles
- `.alert-success` - Success alert
- `.alert-error` - Error alert
- `.alert-warning` - Warning alert
- `.alert-info` - Information alert
- `.alert-title` - Alert title
- `.alert-close` - Close button
- `.alert-inline` - Compact variant
- `.alert-toast` - Toast/notification (fixed position)
- `.tooltip` - Floating label (with `.visible` modifier)

### 5. Progress & Status (Lines 633–723)
Progress bars, status badges, spinners.

**Available classes:**
- `.progress-bar-base` - Progress bar container
- `.progress-bar-fill` - Progress bar fill
- `.progress-bar-fill.success|danger|warning|info` - Color variants
- `.progress-bar-base.xs|sm|md|lg` - Size variants
- `.progress-bar-fill.animated` - Animated shimmer
- `.status-badge` - Status indicator badge
- `.status-badge.online|offline|loading|success|error` - Status variants
- `.spinner` - Loading spinner
- `.spinner.sm|md|lg` - Spinner sizes

### 6. Cards & Containers (Lines 725–800)
Cards, panels, content containers.

**Available classes:**
- `.card` - Primary card styling
- `.card.elevated` - Elevated shadow
- `.card.flat` - No shadow
- `.card-header` - Card header section
- `.card-header-title` - Header title
- `.card-body` - Card body section
- `.card-footer` - Card footer section
- `.panel` - Lightweight container
- `.panel.compact` - Compact padding
- `.panel.spacious` - Generous padding

### 7. Tables & Lists (Lines 802–864)
Table and list item styling.

**Available classes:**
- `.list-item` - Single list item
- `.list-item.active` - Active/selected state
- `.list-group` - List container

**Direct element styling:**
- `table`
- `table thead`
- `table tbody`
- `table th`, `table td`

### 8. Modals & Overlays (Lines 866–917)
Modal dialogs and overlays.

**Available classes:**
- `.modal-overlay` - Backdrop overlay
- `.modal` - Modal container
- `.modal-header` - Modal header
- `.modal-header-title` - Header title
- `.modal-body` - Modal body
- `.modal-footer` - Modal footer

### 9. Loading States & Skeletons (Lines 919–978)
Skeleton screens, loaders, shimmer effects.

**Available classes:**
- `.skeleton-text` - Skeleton for text
- `.skeleton-title` - Skeleton for titles
- `.skeleton-avatar` - Skeleton for avatars
- `.skeleton-button` - Skeleton for buttons
- `.loading-overlay` - Full overlay during loading
- `.pulse` - Subtle pulse animation
- `.shimmer` - Shimmer effect

### 10. Utilities & Helpers (Lines 980–1070)
General-purpose utility classes.

**Available classes:**
- `.sr-only` - Screen reader only (hidden but accessible)
- `.focus-ring` - Keyboard focus indicator
- `.disabled-state` - Disabled state styling
- `.truncate-1|2|3` - Text truncation
- `.divider` - Separator line (with `.horizontal|vertical` modifiers)
- `.gap-xs|sm|md|lg|xl` - Spacing presets
- `.clickable` - Clickable element (cursor + hover effect)
- `.text-align-center|right` - Text alignment

## Usage Examples

### Adding a Form with Input Validation

```jsx
import { Input, Select, FormField } from './components/ui';

export function MyForm() {
  const [errors, setErrors] = useState({});
  
  return (
    <form className="form-group gap-md">
      <FormField
        label="Email"
        htmlFor="email"
        helper="We'll never share your email"
        error={errors.email}
        required
      >
        <Input
          id="email"
          type="email"
          className={errors.email ? 'input-invalid' : ''}
        />
      </FormField>

      <FormField
        label="Account Type"
        htmlFor="type"
        required
      >
        <Select id="type" className={errors.type ? 'input-invalid' : ''}>
          <option value="">Select type...</option>
          <option value="personal">Personal</option>
          <option value="business">Business</option>
        </Select>
      </FormField>

      <div className="flex gap-md justify-end">
        <button className="btn-secondary">Cancel</button>
        <button className="btn-primary">Submit</button>
      </div>
    </form>
  );
}
```

### Creating a Status Card

```jsx
export function PrinterStatus({ printer }) {
  return (
    <div className="card">
      <div className="card-header">
        <div className="card-header-title">Printer Status</div>
      </div>
      <div className="card-body gap-md">
        <div className="flex justify-between items-center">
          <span className="text-pf-text-secondary">State:</span>
          <span className={`status-badge ${printer.online ? 'online' : 'offline'}`}>
            {printer.online ? 'Online' : 'Offline'}
          </span>
        </div>
        <div className="flex justify-between items-center">
          <span className="text-pf-text-secondary">Progress:</span>
        </div>
        <div className="progress-bar-base">
          <div 
            className="progress-bar-fill success"
            style={{ width: `${printer.progress}%` }}
          />
        </div>
      </div>
      <div className="card-footer">
        <button className="btn-md btn-secondary">View Details</button>
      </div>
    </div>
  );
}
```

### Creating an Alert

```jsx
export function ImportantMessage({ onClose }) {
  return (
    <div className="alert-base alert-warning">
      <div className="flex-1">
        <div className="alert-title">Warning</div>
        <div>This action cannot be undone</div>
      </div>
      <button 
        onClick={onClose}
        className="alert-close"
        aria-label="Dismiss"
      >
        ×
      </button>
    </div>
  );
}
```

### Creating a Loading State

```jsx
export function LoadingPrinterList() {
  return (
    <div className="flex flex-col gap-md">
      {[1, 2, 3].map(i => (
        <div key={i} className="card">
          <div className="skeleton-title"></div>
          <div className="skeleton-text mt-2"></div>
          <div className="skeleton-text mt-2 w-3/4"></div>
        </div>
      ))}
    </div>
  );
}
```

### Adding Custom Controls

When you need a new control type:

1. **Identify the control type** (input, button, feedback, etc.)
2. **Find the relevant section** in `controls.css`
3. **Add the class definition** following the existing pattern:
   ```css
   /**
    * New Control Name
    * Used for: [What it's used for]
    */
   .new-control {
     /* base styles */
   }
   
   .new-control:hover {
     /* hover styles */
   }
   ```
4. **Document it** with JSDoc comments
5. **Update this guide** if it's a major new control type

## CSS Variables Reference

All controls use CSS custom properties from `theme.css`:

**Background Colors:**
- `--pf-bg-0` - Main page background
- `--pf-bg-1` - Secondary background
- `--pf-bg-2` - Tertiary background

**Text Colors:**
- `--pf-text-primary` - Primary text
- `--pf-text-secondary` - Secondary text
- `--pf-text-tertiary` - Tertiary/muted text

**States:**
- `--pf-accent` - Accent color
- `--pf-error` - Error color
- `--pf-success` - Success color
- `--pf-warning` - Warning color

**Interactive:**
- `--pf-focus-ring` - Focus ring color
- `--pf-hover-overlay` - Hover background overlay
- `--pf-active-overlay` - Active state overlay

See `styles/theme.css` for complete list.

## Best Practices

1. **Always use explicit classes** instead of inline styles for controls
2. **Combine with Tailwind** for layout (flex, grid, padding, margin)
3. **Don't override control styles** in component modules - extend them in `controls.css`
4. **Use semantic variants** (`.btn-danger`, `.input-invalid`) over custom classes
5. **Keep states consistent** - use the same hover/focus/disabled patterns everywhere
6. **Test with keyboard** - ensure all interactive elements are keyboard accessible
7. **Check focus indicators** - press Tab to verify focus rings are visible
8. **Maintain contrast** - use the defined color system which meets WCAG standards

## Accessibility Notes

All controls in this stylesheet include:
- ✅ Proper focus indicators for keyboard navigation
- ✅ Semantic HTML (labels, aria attributes)
- ✅ Color contrast ratios meeting WCAG AA standards
- ✅ States for disabled/invalid/active states
- ✅ Support for `prefers-reduced-motion`

## File Location

- **Stylesheet**: `src/styles/controls.css`
- **Imported in**: `src/index.css`
- **Theme variables**: `src/styles/theme.css`
- **Component utilities**: `src/components/ui/`

## Quick Reference

| Control Type | Primary Class | Size Variants | State Variants |
|---|---|---|---|
| Text Input | `.input-base` | `.input-sm`, `.input-lg` | `.input-invalid`, `.input-disabled` |
| Button | `.btn-base` | `.btn-sm`, `.btn-md`, `.btn-lg` | `.btn-primary`, `.btn-danger`, `.btn-subtle` |
| Form Group | `.form-group` | - | `.inline` |
| Alert | `.alert-base` | `.alert-inline` | `.alert-success`, `.alert-error`, `.alert-warning` |
| Card | `.card` | - | `.elevated`, `.flat` |
| Status Badge | `.status-badge` | - | `.online`, `.offline`, `.loading`, `.error` |
| Progress Bar | `.progress-bar-base` | `.xs`, `.sm`, `.md`, `.lg` | `.success`, `.danger`, `.warning` |
| Spinner | `.spinner` | `.sm`, `.md`, `.lg` | - |
| Skeleton | `.skeleton-text` | - | - |
| Modal | `.modal` | - | - |

---

**Created**: 2025
**Last Updated**: 2025
**Maintained by**: PrintFarmer Development Team
