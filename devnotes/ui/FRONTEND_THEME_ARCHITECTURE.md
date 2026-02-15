# PrintFarmer Theme System Architecture

## Overview

The PrintFarmer theme system provides **comprehensive, theme-aware styling** for all UI controls through a three-layer architecture:

1. **Theme Variables** - CSS custom properties defined per theme
2. **Component Utilities** - Reusable CSS classes for controls
3. **Component Integration** - React components using the system

## Layer 1: Theme Variables

### Theme Files
- `src/styles/theme.css` - Main theme system orchestrator
- `src/styles/themes/github-dark.css` - GitHub Dark (default) theme colors
- `src/styles/themes/printfarmer-dark.css` - PrintFarmer Dark theme colors

### Variable Categories

#### Background Colors
- `--pf-bg-0` - Main page background
- `--pf-bg-1` - Secondary background (cards, modals)
- `--pf-bg-2` - Tertiary background (nested containers)
- `--pf-panel` - Panel-specific background

#### Text Colors
- `--pf-text-primary` - Primary text (high contrast)
- `--pf-text-secondary` - Secondary text (medium contrast)
- `--pf-text-tertiary` - Tertiary text (low contrast)
- `--pf-text-muted` - Muted/disabled text
- `--pf-text-light` - Light text for dark backgrounds

#### Border Colors
- `--pf-border` - Main border color
- `--pf-border-light` - Light borders
- `--pf-border-medium` - Medium borders
- `--pf-border-dark` - Dark borders

#### Control Styling Variables (NEW)
**Input/Select/Textarea Controls:**
- `--pf-control-bg` - Input background color
- `--pf-control-border` - Input border default state
- `--pf-control-border-hover` - Input border on hover
- `--pf-control-border-focus` - Input border on focus
- `--pf-control-text` - Input text color
- `--pf-control-placeholder` - Placeholder text color
- `--pf-control-disabled-bg` - Disabled background
- `--pf-control-disabled-text` - Disabled text color

**Focus Ring:**
- `--pf-control-focus-ring` - Focus ring color (with alpha)
- `--pf-control-focus-ring-offset` - Focus ring offset background
- `--pf-control-focus-ring-width` - Focus ring width (default: 2px)

**Button States:**
- `--pf-button-primary-bg/hover/active` - Primary button states
- `--pf-button-secondary-bg/hover/active` - Secondary button states
- `--pf-button-danger-bg/hover/active` - Danger button states
- `--pf-button-*-text` - Button text colors
- `--pf-button-*-border` - Button border colors

**Validation States:**
- `--pf-validation-error-bg/border/text` - Error styling
- `--pf-validation-success-border/text` - Success styling

#### Status & Semantic Colors
- `--pf-status-online-*` - Online status colors
- `--pf-status-offline-*` - Offline status colors
- `--pf-error`, `--pf-warning`, `--pf-success` - Semantic colors
- `--pf-accent`, `--pf-link` - Brand/link colors

#### Gradient Colors
- `--pf-gradient-primary-*` - Primary gradients
- `--pf-gradient-secondary-*` - Secondary gradients
- `--pf-gradient-success-*` - Success gradients
- `--pf-gradient-gray-*` - Gray gradients

#### Other States
- `--pf-loading` - Loading color
- `--pf-disabled` - Disabled state color
- `--pf-focus-ring` - Focus ring color
- `--pf-skeleton-bg/bg-alt/accent` - Skeleton animation colors

## Layer 2: Component Utilities

File: `src/styles/components.css`

### Control Base Classes

**`.pf-control-base`** - Base styling for all input-like controls
```css
/* Provides: background, border, text color, focus ring, placeholder, disabled states */
```

### Validation State Classes

**`.pf-control-error`** - Error state styling (red border + light red background)
**`.pf-control-success`** - Success state styling (green border)

### Button Variant Classes

**`.pf-btn-primary`** - Primary button styling
- Bold text, shadow on hover, active state

**`.pf-btn-secondary`** - Secondary button styling
- Medium weight, subtle hover effect

**`.pf-btn-danger`** - Danger button styling
- Bold text, red background, shadow on hover

### Focus Ring Utilities

**`.pf-focus-ring`** - Simple focus ring (no offset)
**`.pf-focus-ring-offset`** - Focus ring with 4px offset background

### State Utilities

**`.pf-disabled`** - Disabled state (opacity, cursor, pointer-events)

## Layer 3: Component Integration

### Existing Components Using Variables

**Button.tsx:**
```typescript
primary: 'bg-pf-accent-bg hover:bg-pf-accent-hover text-white border border-pf-accent-bg'
secondary: 'bg-pf-bg-2 hover:bg-pf-bg-1 text-pf-text-primary border border-pf-border-light'
danger: 'bg-pf-error hover:bg-pf-error-hover text-white border border-pf-error-border'
success: 'bg-pf-success-bg hover:bg-pf-success-hover text-white border border-pf-success'
```

**Input.tsx:**
```typescript
'border rounded p-2 text-sm bg-pf-bg-0 text-pf-text-primary border-pf-border'
'focus:outline-none focus:ring-2 focus:ring-pf-accent'
'invalid && border-pf-error focus:ring-pf-error'
```

**Radio.tsx:**
```typescript
'w-4 h-4 border-pf-border bg-pf-bg-0 text-pf-accent'
'focus:ring-2 focus:ring-pf-accent'
```

## Adding New Theme-Aware Controls

### Step 1: Define Theme Variables
Add variables to both theme files:
```css
[data-theme="github-dark"] {
  --pf-mycontrol-bg: #color;
  --pf-mycontrol-border: #color;
  --pf-mycontrol-hover: #color;
}

[data-theme="printfarmer-dark"] {
  --pf-mycontrol-bg: #color;
  --pf-mycontrol-border: #color;
  --pf-mycontrol-hover: #color;
}
```

### Step 2: Create Utility Class (Optional)
If reusing across multiple components:
```css
.pf-mycontrol {
  background-color: var(--pf-mycontrol-bg);
  border: 1px solid var(--pf-mycontrol-border);
}

.pf-mycontrol:hover {
  background-color: var(--pf-mycontrol-hover);
}
```

### Step 3: Use in Component
```typescript
<input 
  className="pf-control-base"
  // or
  className="border-pf-border bg-pf-control-bg text-pf-text-primary"
/>
```

## Theme Switching

The theme system uses the `data-theme` attribute:

```html
<html data-theme="github-dark">   <!-- GitHub Dark (default) -->
<html data-theme="printfarmer-dark">  <!-- PrintFarmer Dark -->
<html data-theme="light">         <!-- Light (future) -->
```

Change theme via JavaScript:
```javascript
document.documentElement.setAttribute('data-theme', 'printfarmer-dark');
```

## Adding a New Theme

1. Create `src/styles/themes/my-theme.css` with all required variables
2. Import in `src/styles/theme.css`
3. Follow the variable naming convention (`--pf-*`)
4. Include all control variables for consistency
5. Test with all components (buttons, inputs, validation states)

## Best Practices

✅ **DO:**
- Use CSS variables for all colors
- Define variables in theme files, never hardcode colors in components
- Create utility classes for reusable patterns
- Use semantic variable names (`--pf-button-primary-hover` not `--pf-blue-700`)
- Test with both dark and light themes
- Update BOTH theme files when adding new variables

❌ **DON'T:**
- Hardcode colors in component className strings
- Use Tailwind's hardcoded color names (e.g., `bg-blue-700`)
- Create different component styles for different themes
- Forget to update both theme files

## Testing Theme Colors

Use browser DevTools:
```javascript
// Check current theme
getComputedStyle(document.documentElement).getPropertyValue('--pf-text-primary')

// Switch theme
document.documentElement.setAttribute('data-theme', 'printfarmer-dark')

// Inspect controls in both themes to verify color consistency
```

## Current Theme Coverage

- ✅ **GitHub Dark Theme** (default)
- ✅ **PrintFarmer Dark Theme**  
- ✅ **Light Theme** (NEW)
- ✅ Button variants (primary, secondary, danger, success, subtle, tab, toggle)
- ✅ Input/Select/Textarea controls
- ✅ Focus ring states
- ✅ Validation states (error, success)
- ✅ Text colors (primary, secondary, muted)
- ✅ Background colors (bg-0, bg-1, bg-2)
- ✅ Border colors (all variants)
- ✅ Status colors (online, offline)
- ✅ Accent colors and links
- ✅ Skeleton/loading states
- ✅ **High Contrast Mode** (NEW) - Accessibility support
- ✅ **Reduced Motion Support** (NEW) - Respects user preferences

## Accessibility Features

### High Contrast Mode

Automatically activates when user enables high contrast in OS settings (Windows, macOS, Linux).

```css
/* Automatic activation */
@media (prefers-contrast: high) {
  /* Maximum contrast colors applied per theme */
}
```

**Per-Theme High Contrast:**
- **Dark Themes**: White text on dark backgrounds, bright accent colors
- **Light Theme**: Black text on light backgrounds, bright complementary colors

Meets WCAG AAA color contrast requirements (7:1 ratio for normal text).

### Reduced Motion Support

Automatically respects user's motion preferences for accessibility.

```css
/* Automatic activation */
@media (prefers-reduced-motion: reduce) {
  /* All animations/transitions disabled */
  /* Visual feedback (hover, focus, active) preserved */
}
```

**Benefits:**
- Users with vestibular disorders can use interface safely
- Smooth scrolling disabled if requested
- Skeleton shimmer animations removed
- Focus rings remain visible for keyboard navigation
- Button state changes still work

## Theme Usage Guide

### Switching Themes Programmatically

```javascript
// Set theme
document.documentElement.setAttribute('data-theme', 'light');

// Get current theme
const theme = document.documentElement.getAttribute('data-theme');

// Detect system preference
const isDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
if (prefersDark) {
  document.documentElement.setAttribute('data-theme', 'github-dark');
}
```

### Available Themes

| Theme | Value | Best For |
|-------|-------|----------|
| GitHub Dark | `github-dark` | Default, professional, GitHub-like |
| PrintFarmer Dark | `printfarmer-dark` | Custom dark theme, warmer palette |
| Light | `light` | Bright environments, paper-like feel |

### Testing with Different Preferences

```javascript
// Test high contrast mode
window.matchMedia('(prefers-contrast: high)').addEventListener('change', (e) => {
  console.log('High contrast:', e.matches);
});

// Test reduced motion preference
window.matchMedia('(prefers-reduced-motion: reduce)').addEventListener('change', (e) => {
  console.log('Reduced motion:', e.matches);
});

// Test color scheme preference
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
  console.log('System theme:', e.matches ? 'dark' : 'light');
});
```

## Creating Custom Themes

### Template for New Theme

```css
/* src/styles/themes/my-theme.css */

[data-theme="my-theme"] {
  /* Backgrounds */
  --pf-bg-0: #color;
  --pf-bg-1: #color;
  --pf-bg-2: #color;
  --pf-panel: #color;

  /* Borders */
  --pf-border: #color;
  --pf-border-light: #color;
  --pf-border-medium: #color;

  /* Text */
  --pf-text-primary: #color;
  --pf-text-secondary: #color;
  --pf-text-muted: #color;

  /* Accents & Status */
  --pf-accent: #color;
  --pf-accent-bg: #color;
  --pf-success: #color;
  --pf-error: #color;

  /* Control Variables */
  --pf-control-bg: #color;
  --pf-control-border: #color;
  --pf-control-text: #color;

  /* Button States */
  --pf-button-primary-bg: #color;
  --pf-button-primary-hover: #color;
  --pf-button-secondary-bg: #color;
  --pf-button-danger-bg: #color;

  /* Validation */
  --pf-validation-error-border: #color;
  --pf-validation-success-border: #color;

  color-scheme: dark; /* or light */
}
```

### Steps to Add Theme

1. Create `src/styles/themes/my-theme.css` with all variables
2. Import in `src/styles/theme.css`: `@import './themes/my-theme.css';`
3. Add high contrast variant in theme.css media query
4. Add to theme selector UI (e.g., ThemeSelector component)
5. Test with all components in all states
6. Verify accessibility (contrast ratios, focus indicators)

## Future Enhancements

- [x] **High contrast mode support** - IMPLEMENTED
  - Per-theme high contrast overrides
  - WCAG AAA compliant contrast ratios
  - Activated via `prefers-contrast: high` media query
  
- [x] **Light theme variables** - IMPLEMENTED
  - Complete light theme with all CSS variables
  - Proper contrast on light backgrounds
  - All control states (input, button, validation)
  - Usage: `<html data-theme="light">`

- [x] **Theme animation preferences** - IMPLEMENTED
  - Respects `prefers-reduced-motion` media query
  - Disables transitions/animations for accessibility
  - Preserves visual feedback (hover, focus, active states)

- [ ] **Custom theme builder UI** - Future
  - Interactive theme color picker
  - Live preview as user adjusts colors
  - Export/import theme configurations

- [ ] **Per-component theme overrides** - Future
  - Allow specific components to override theme variables
  - Useful for special cases (premium features, warnings, etc.)
  - Scoped CSS variables per component

- [ ] **Automatic contrast checking** - Future
  - Build-time contrast ratio validation
  - Runtime accessibility audit
  - Suggest color adjustments for WCAG compliance
