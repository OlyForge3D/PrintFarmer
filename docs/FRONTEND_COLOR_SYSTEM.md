# PrintFarmer Color System & Accessibility Guide

## Overview

PrintFarmer implements a comprehensive, accessible color theme system that supports both dark and light modes while meeting WCAG 2.1 AA accessibility standards. The system uses CSS custom properties for dynamic theming and semantic color naming for developer-friendly usage.

## Color System Architecture

### CSS Custom Properties Foundation

The color system is built on CSS custom properties (CSS variables) defined in `src/styles/theme.css`. This enables:

- **Dynamic theme switching** without page reload
- **System preference detection** for automatic dark/light mode
- **Accessibility compliance** with verified contrast ratios
- **Future extensibility** for additional themes

### Semantic Color Categories

#### 1. Background Colors
```css
--pf-bg-0      /* Main page background */
--pf-bg-1      /* Secondary backgrounds (cards, panels) */
--pf-bg-2      /* Tertiary backgrounds */
--pf-panel     /* Panel backgrounds */
```

#### 2. Text Colors
```css
--pf-text-primary    /* Primary text (4.5:1 contrast on dark) */
--pf-text-secondary  /* Secondary text (3:1 contrast minimum) */
--pf-text-tertiary   /* Muted/helper text */
--pf-text-light      /* Light emphasis text */
--pf-text-muted      /* Most subtle text */
```

#### 3. Accent & Interactive Colors
```css
--pf-accent        /* Bright accent for text on dark backgrounds */
--pf-accent-bg     /* Dark accent for button backgrounds */
--pf-accent-2      /* Secondary accent (blue) */
--pf-success       /* Success text color */
--pf-success-bg    /* Success button background */
--pf-link          /* Link color on dark backgrounds */
```

#### 4. Status Indicators
```css
--pf-status-online-bg     /* Online status background */
--pf-status-online-text   /* Online status text */
--pf-status-offline-bg    /* Offline status background */
--pf-status-offline-text  /* Offline status text */
--pf-error               /* Error text/borders */
--pf-error-bg            /* Error backgrounds */
--pf-warning             /* Warning text/borders */
```

#### 5. Border & Divider Colors
```css
--pf-border         /* Primary borders */
--pf-border-light   /* Light borders */
--pf-border-medium  /* Medium emphasis borders */
--pf-border-dark    /* Subtle borders */
```

## Accessibility Compliance

### WCAG 2.1 AA Standards

All color combinations have been tested and verified to meet WCAG 2.1 AA requirements:

- **Normal text**: Minimum 4.5:1 contrast ratio
- **Large text**: Minimum 3:1 contrast ratio 
- **UI components**: Minimum 3:1 contrast ratio
- **Focus indicators**: Clearly visible with sufficient contrast

### Verified Color Combinations

✅ **Text on Backgrounds**
- Primary text on main background: **13.49:1** (exceeds AAA)
- Primary text on card background: **12.12:1** (exceeds AAA)
- Secondary text on main background: **5.37:1** (exceeds AA)

✅ **Status Indicators**
- Online status: **6.74:1** contrast
- Offline status: **9.56:1** contrast
- Error indicators: **4.83:1** contrast

✅ **Interactive Elements**
- Accent text on dark: **7.46:1** contrast
- White text on accent buttons: **5.48:1** contrast
- White text on error buttons: **4.83:1** contrast

### Color Blindness Considerations

The accessibility utility includes color blindness simulation functions:
- `ColorBlindnessSimulation.protanopia()` - Red-blind simulation
- `ColorBlindnessSimulation.deuteranopia()` - Green-blind simulation  
- `ColorBlindnessSimulation.tritanopia()` - Blue-blind simulation

Status indicators combine color with icons to ensure information is not conveyed by color alone.

## Usage Guidelines

### For Developers

#### Using Tailwind Classes
```tsx
// Backgrounds
<div className="bg-pf-bg-1">Card background</div>

// Text colors
<h1 className="text-pf-text-primary">Primary heading</h1>
<p className="text-pf-text-secondary">Secondary text</p>

// Interactive elements
<button className="bg-pf-accent-bg text-white">Accessible button</button>
<a className="text-pf-link">Accessible link</a>

// Status indicators
<span className="bg-pf-status-online-bg text-pf-status-online-text">
  Online
</span>
```

#### Using CSS Custom Properties Directly
```css
.custom-component {
  background: var(--pf-bg-1);
  color: var(--pf-text-primary);
  border: 1px solid var(--pf-border);
}

.custom-button {
  background: var(--pf-accent-bg);
  color: white;
}

.custom-button:hover {
  background: var(--pf-success-hover);
}
```

#### Theme Context in React
```tsx
import { useTheme } from '@/contexts/ThemeContext';

function MyComponent() {
  const { theme, setTheme, computedTheme } = useTheme();
  
  return (
    <div>
      <p>Current theme: {theme}</p>
      <p>Computed theme: {computedTheme}</p>
      <button onClick={() => setTheme('light')}>Light mode</button>
    </div>
  );
}
```

### Color Selection Guidelines

#### DO ✅
- Use `--pf-accent` for text on dark backgrounds
- Use `--pf-accent-bg` for button backgrounds with white text
- Combine color with icons for status indicators
- Test color combinations with the accessibility utilities
- Use semantic color names instead of hardcoded hex values

#### DON'T ❌
- Use accent colors without checking contrast ratios
- Rely on color alone to convey information
- Hardcode hex values in component styles
- Mix light and dark theme colors arbitrarily

## Theme System Implementation

### Theme Provider Setup
```tsx
import { ThemeProvider } from '@/contexts/ThemeContext';

function App() {
  return (
    <ThemeProvider defaultTheme="system">
      {/* Your app content */}
    </ThemeProvider>
  );
}
```

### Theme Toggle Component
```tsx
import { ThemeToggle } from '@/components/ThemeToggle';

// Compact toggle button
<ThemeToggle />

// Button group with labels
<ThemeToggle variant="buttons" showLabels />

// Dropdown selector
<ThemeToggle variant="dropdown" />
```

### System Features

#### Automatic System Preference Detection
The theme system automatically detects and responds to:
- `prefers-color-scheme: dark/light`
- `prefers-reduced-motion: reduce`
- `prefers-contrast: high`

#### Theme Persistence
User theme preferences are automatically saved to localStorage and restored on page load.

#### Custom Events
The theme system dispatches `themeChange` events when themes are switched:
```tsx
window.addEventListener('themeChange', (event) => {
  console.log('Theme changed to:', event.detail.theme);
});
```

## Testing & Validation

### Accessibility Testing

Use the built-in accessibility utilities to test color combinations:

```tsx
import { checkWCAGCompliance, testThemeCompliance } from '@/utils/accessibility';

// Test specific color combinations
const result = checkWCAGCompliance('#ffffff', '#047857');
console.log('Passes WCAG AA:', result.passes);
console.log('Contrast ratio:', result.ratio);

// Test all theme colors at once
const results = testThemeCompliance();
results.forEach(test => {
  if (!test.result.passes) {
    console.warn(`Failed: ${test.name}`, test.result);
  }
});
```

### Running Accessibility Tests
```bash
# Run accessibility test suite
npm test src/test/utils/accessibility.test.ts

# Full test suite
npm test
```

## Browser Support

The color system supports all modern browsers with CSS custom properties support:
- Chrome 49+
- Firefox 31+
- Safari 9.1+
- Edge 16+

Fallback colors are provided for older browsers through the Tailwind configuration.

## Future Enhancements

### Planned Features
- [ ] High contrast mode theme variant
- [ ] Additional brand color themes
- [ ] Automated contrast checking in CI/CD
- [ ] Color blindness preview mode
- [ ] Theme designer tool

### Extension Points
The system is designed to be easily extended with:
- Additional theme variants
- Custom color properties
- Integration with design tokens
- Advanced accessibility features

## Performance Considerations

- CSS custom properties have minimal performance impact
- Theme switching is instant (no page reload required)
- Color utilities are tree-shaken in production builds
- Theme persistence uses efficient localStorage caching

## Maintenance

### Adding New Colors
1. Define CSS custom property in `theme.css`
2. Add to Tailwind config in `tailwind.config.js`
3. Update accessibility tests
4. Document usage guidelines

### Updating Existing Colors
1. Verify WCAG compliance with new values
2. Update tests with new expected values
3. Test across all theme variants
4. Update documentation if needed

This color system provides a solid foundation for accessible, maintainable theming in PrintFarmer while ensuring all users can effectively interact with the interface regardless of their visual abilities or preferences.