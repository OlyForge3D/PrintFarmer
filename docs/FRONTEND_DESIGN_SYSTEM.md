# PrintFarmer Frontend Design System

Comprehensive guide to PrintFarmer's React component library, styling system, color tokens, and theme architecture.

## 📚 Documentation Structure

This design system is documented across three complementary guides:

### 1. [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md)
**Comprehensive theme system implementation and customization guide**

- Three-layer architecture (variables → utilities → components)
- Theme definition and switching
- CSS custom properties reference
- Theme variable categories and naming conventions
- Adding new themes and controls
- Accessibility features (high contrast mode, reduced motion)
- Best practices for theme-aware development

**Read this for:**
- Understanding how the theme system works
- Adding new themes or theme variables
- Making components theme-aware
- Implementing accessibility preferences
- Customizing theme colors

### 2. [Color System](./FRONTEND_COLOR_SYSTEM.md)
**Color token reference and accessibility compliance**

- Semantic color categories (backgrounds, text, accents, status)
- WCAG 2.1 AA compliance verification
- Color contrast ratio documentation
- Usage guidelines and examples
- Color blindness considerations
- Component integration patterns
- Tailwind class mapping

**Read this for:**
- Understanding available color tokens
- Verifying color accessibility compliance
- Choosing appropriate colors for new components
- Using theme colors in components
- Accessibility testing guidelines

### 3. [UI Components Guide](./FRONTEND_UI_COMPONENTS.md)
**Complete reference for shared React UI components**

- Component library overview (Button, Input, Select, Card, Modal, etc.)
- Component usage examples and props
- Styling patterns and customization
- Integration with the design system
- Accessibility features per component
- Type definitions and interfaces

**Read this for:**
- Using existing UI components
- Component API reference
- Understanding component capabilities
- Accessibility features of components
- Customization options

## 🎯 Quick Navigation

### By Role

**Frontend Developer:**
1. Start with [UI Components Guide](./FRONTEND_UI_COMPONENTS.md)
2. Reference [Color System](./FRONTEND_COLOR_SYSTEM.md) for colors
3. Consult [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md) for advanced customization

**Designer/Stylist:**
1. Review [Color System](./FRONTEND_COLOR_SYSTEM.md) for color palette
2. Check [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md) for design tokens
3. Use [UI Components Guide](./FRONTEND_UI_COMPONENTS.md) for component styling

**Maintainer:**
1. Understand [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md) for system design
2. Reference all three guides when making changes
3. Verify accessibility in [Color System](./FRONTEND_COLOR_SYSTEM.md)

### By Task

**I want to use a button**
→ [UI Components Guide - Button](./FRONTEND_UI_COMPONENTS.md#button)

**I need a color for my component**
→ [Color System - Color Selection Guidelines](./FRONTEND_COLOR_SYSTEM.md#color-selection-guidelines)

**I'm adding a new theme**
→ [Theme Architecture - Adding a New Theme](./FRONTEND_THEME_ARCHITECTURE.md#adding-a-new-theme)

**I need to make something accessible**
→ [Color System - WCAG Compliance](./FRONTEND_COLOR_SYSTEM.md#wcag-21-aa-standards)

**I want to support dark/light modes**
→ [Theme Architecture - Theme Switching](./FRONTEND_THEME_ARCHITECTURE.md#theme-switching)

**I need form controls**
→ [UI Components Guide - Form Controls](./FRONTEND_UI_COMPONENTS.md#input), [UI Components Guide - FormField](./FRONTEND_UI_COMPONENTS.md#formfield)

## 🏗️ Architecture Overview

### Three-Layer System

```
┌─────────────────────────────────────────────────────┐
│ Layer 3: React Components (Button, Input, etc.)      │
│ ├─ Props-based API                                   │
│ └─ Integrates Layer 2 classes                        │
├─────────────────────────────────────────────────────┤
│ Layer 2: CSS Utility Classes                         │
│ ├─ `.pf-btn-primary`, `.pf-control-base`            │
│ ├─ `.pf-focus-ring`, `.pf-validation-error`         │
│ └─ Uses Layer 1 variables                            │
├─────────────────────────────────────────────────────┤
│ Layer 1: CSS Custom Properties (Theme Variables)     │
│ ├─ `--pf-bg-0`, `--pf-text-primary`                 │
│ ├─ `--pf-button-primary-bg`, etc.                   │
│ └─ Defined per theme file                            │
└─────────────────────────────────────────────────────┘
```

### Key Files

**Theme Variables:**
- `src/styles/theme.css` - Main orchestrator and media queries
- `src/styles/themes/github-dark.css` - GitHub Dark theme (default)
- `src/styles/themes/printfarmer-dark.css` - PrintFarmer Dark theme
- `src/styles/themes/light.css` - Light theme

**Component Styles:**
- `src/styles/components.css` - Reusable utility classes
- `src/styles/index.css` - Imports all stylesheets

**React Components:**
- `src/components/ui/` - Shared UI components (Button, Input, Select, etc.)

## 🎨 Available Themes

| Theme | Value | Description |
|-------|-------|-------------|
| GitHub Dark | `github-dark` | Professional GitHub-like dark theme (default) |
| PrintFarmer Dark | `printfarmer-dark` | Custom dark theme with warmer palette |
| Light | `light` | Bright theme for daylight environments |

All themes support:
- ✅ WCAG 2.1 AA color contrast
- ✅ High contrast mode (`prefers-contrast: high`)
- ✅ Reduced motion (`prefers-reduced-motion: reduce`)

## 🔄 Common Workflows

### Create a New Component

1. **Check Components Guide** - Is a similar component already available?
2. **Reference Color System** - Choose appropriate colors using `--pf-*` variables
3. **Build with UI Components** - Compose from existing components where possible
4. **Use Theme Variables** - Never hardcode colors; use CSS custom properties
5. **Test Accessibility** - Verify contrast ratios and focus indicators
6. **Document in Guide** - Add to [UI Components Guide](./FRONTEND_UI_COMPONENTS.md)

### Add Support for New Theme

1. **Create Theme File** - `src/styles/themes/my-theme.css`
2. **Define All Variables** - Follow template in [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md#step-1-define-theme-variables)
3. **Import in theme.css** - Add `@import` for new theme
4. **Add High Contrast Variant** - Per-theme overrides in media query
5. **Test Components** - Verify all buttons, inputs, validation states
6. **Test Accessibility** - Contrast ratios, focus indicators, reduced motion

### Make Component Theme-Aware

1. **Use CSS Variables** - Replace hardcoded colors with `var(--pf-*)`
2. **Leverage Existing Classes** - Use `.pf-control-base`, `.pf-btn-primary`, etc.
3. **Test in All Themes** - Verify appearance in all available themes
4. **No Component Conditionals** - Don't check theme in React; let CSS handle it
5. **Verify Accessibility** - Check contrast and focus states

## 📖 Detailed References

For complete reference documentation, see:

- [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md) - CSS variables, theme system
- [Color System](./FRONTEND_COLOR_SYSTEM.md) - Color tokens, accessibility compliance
- [UI Components Guide](./FRONTEND_UI_COMPONENTS.md) - Component API reference
- [Controls Guide](./CONTROLS_GUIDE.md) - Legacy control styling (being migrated)
- [UI Styling Index](./UI_STYLING_INDEX.md) - Navigation index

## 🚀 Getting Started

**New to the design system?**

1. Read [UI Components Guide](./FRONTEND_UI_COMPONENTS.md) introduction
2. Check [Color System](./FRONTEND_COLOR_SYSTEM.md#usage-guidelines) usage guidelines
3. Review [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md#layer-3-component-integration) integration examples
4. Start building with provided components!

**Need to customize something?**

1. Reference [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md#adding-new-theme-aware-controls) customization guide
2. Check [Color System](./FRONTEND_COLOR_SYSTEM.md#wcag-21-aa-standards) for color compliance
3. Test in [UI Components Guide](./FRONTEND_UI_COMPONENTS.md) examples
4. Update documentation when adding new patterns

## 🧪 Testing & Validation

### Color Accessibility
Use [Color System](./FRONTEND_COLOR_SYSTEM.md#testing--validation) accessibility testing section.

### Theme Switching
See [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md#testing-theme-colors) testing procedures.

### Component Coverage
Reference [Theme Architecture](./FRONTEND_THEME_ARCHITECTURE.md#current-theme-coverage) coverage status.

## 📋 Design System Status

**Current Features:**
- ✅ 3 complete themes (GitHub Dark, PrintFarmer Dark, Light)
- ✅ 40+ CSS color variables per theme
- ✅ 30+ control styling variables
- ✅ High contrast mode support (WCAG AAA)
- ✅ Reduced motion support (accessibility)
- ✅ 15+ shared React components
- ✅ Complete accessibility compliance

**Future Enhancements:**
- [ ] Custom theme builder UI
- [ ] Per-component theme overrides
- [ ] Automatic contrast checking (build-time + runtime)

---

**Start here:** Pick a guide above based on your needs. All three are cross-referenced and work together as one comprehensive system.
