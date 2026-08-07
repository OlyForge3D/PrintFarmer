# UI & Styling Documentation Index

Navigation guide for PrintFarmer UI component styling, color system, and theme architecture.

## 🎨 Frontend Design System (Start Here)

Comprehensive guide to the complete design system with unified documentation:

**[DESIGN_SYSTEM.md](./DESIGN_SYSTEM.md)** - Canonical UI and styling guide
- Quick navigation by role and task
- Architecture overview of three-layer system
- Common workflows and best practices
- Links to detailed guides

## 📚 Detailed Documentation

| Document | Purpose | Read Time |
|----------|---------|-----------|
| **[`DESIGN_SYSTEM.md`](./DESIGN_SYSTEM.md)** | Design system, themes, tokens, and components | 30 min |
| **[Available Themes](./DESIGN_SYSTEM.md#available-themes)** | Theme registry and customization | 10 min |
| **[Color Palette](./DESIGN_SYSTEM.md#color-palette)** | Color tokens and accessibility | 10 min |
| **[`FRONTEND_UI_COMPONENTS.md`](./FRONTEND_UI_COMPONENTS.md)** | React component library reference | 25 min |
| **[`CONTROLS_GUIDE.md`](./CONTROLS_GUIDE.md)** | Legacy control styling (being migrated) | 20 min |

## 🎨 What You Get

The PrintFarmer app has a **three-layer design system** ensuring consistency and ease of maintenance:

**Layer 1: CSS Custom Properties (Theme Variables)**
- `src/design-system/themes/base.css` - Theme-independent tokens (fonts, spacing, z-index)
- `src/design-system/themes/<theme>.css` - Per-theme variables, one file per selectable theme
- 142 `--pf-*` tokens per theme, identical key set across all of them
- `src/design-system/themes/registry.ts` - The single source of truth for which themes exist

**Layer 2: CSS Utility Classes**
- `src/styles/controls.css` - Control styles
- Tailwind utilities generated from the `--pf-*` tokens (e.g. `bg-pf-accent`)

**Layer 3: React Components**
- `src/components/ui/` - Button, Input, Select, Card, Modal, and more

## 🚀 Quick Start

**New to the design system?**

Start with: [`DESIGN_SYSTEM.md`](./DESIGN_SYSTEM.md)

Then choose a detailed guide:
- **Using components**: [`FRONTEND_UI_COMPONENTS.md`](./FRONTEND_UI_COMPONENTS.md)
- **Choosing colors**: [Color Palette](./DESIGN_SYSTEM.md#color-palette)
- **Adding themes**: [Available Themes](./DESIGN_SYSTEM.md#available-themes)

**Using existing control styles?**

Read: [`CONTROLS_GUIDE.md`](./CONTROLS_GUIDE.md)

## 💡 Common Tasks

**Need a button?**
```jsx
<button className="btn-primary btn-md">Click me</button>
```

**Need form validation styling?**
```jsx
<input className={errors.email ? 'input-invalid' : 'input-base'} />
<div className="form-error">{errors.email}</div>
```

**Need a status badge?**
```jsx
<span className={`status-badge ${online ? 'online' : 'offline'}`}>
  {online ? 'Online' : 'Offline'}
</span>
```

**Need an alert?**
```jsx
<div className="alert-base alert-success">
  <div className="alert-title">Success!</div>
</div>
```

👉 See [`CONTROLS_GUIDE.md`](./CONTROLS_GUIDE.md) for full reference and more examples.

## 📋 Control Categories

- **Input Controls** - Text inputs, selects, checkboxes, radios, range, file uploads
- **Buttons** - Primary, secondary, success, danger, subtle, ghost, icon, link
- **Form Elements** - Labels, helpers, errors, groups, fieldsets
- **Alerts** - Success, error, warning, info, inline, toast, tooltips
- **Progress & Status** - Bars, badges, spinners
- **Cards & Containers** - Cards, panels with headers/footers
- **Tables & Lists** - Complete table and list styling
- **Modals** - Dialog containers with overlays
- **Loading States** - Skeletons, spinners, shimmer effects
- **Utilities** - Truncation, dividers, spacing, accessibility

## 🔗 Key Files & Directories

**Documentation (in `/docs/`):**
- `DESIGN_SYSTEM.md` - Canonical design system, themes, tokens, and components
- `FRONTEND_UI_COMPONENTS.md` - React component reference
- `CONTROLS_GUIDE.md` - Legacy control styling

**Source Code:**
- `src/design-system/themes/` - Per-theme CSS variable definitions, one file per theme
- `src/design-system/themes/registry.ts` - Which themes exist; everything else derives from it
- `src/styles/theme.css` - Global focus-visible rules only (no tokens; see the file header)
- `src/styles/controls.css` - Control styles
- `src/components/ui/` - React component implementations

---

**Get started**: Read [`CONTROLS_GUIDE.md`](./CONTROLS_GUIDE.md)
