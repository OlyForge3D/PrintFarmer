# UI & Styling Documentation Index

Navigation guide for PrintFarmer UI component styling, color system, and theme architecture.

## 🎨 Frontend Design System (Start Here)

Comprehensive guide to the complete design system with unified documentation:

**[FRONTEND_DESIGN_SYSTEM.md](./FRONTEND_DESIGN_SYSTEM.md)** - Master index for all UI/styling documentation
- Quick navigation by role and task
- Architecture overview of three-layer system
- Common workflows and best practices
- Links to detailed guides

## 📚 Detailed Documentation

| Document | Purpose | Read Time |
|----------|---------|-----------|
| **[`FRONTEND_DESIGN_SYSTEM.md`](./FRONTEND_DESIGN_SYSTEM.md)** | Master index for design system | 10 min |
| **[`FRONTEND_THEME_ARCHITECTURE.md`](./FRONTEND_THEME_ARCHITECTURE.md)** | Theme system, CSS variables, customization | 20 min |
| **[`FRONTEND_COLOR_SYSTEM.md`](./FRONTEND_COLOR_SYSTEM.md)** | Color tokens, accessibility compliance | 15 min |
| **[`FRONTEND_UI_COMPONENTS.md`](./FRONTEND_UI_COMPONENTS.md)** | React component library reference | 25 min |
| **[`CONTROLS_GUIDE.md`](./CONTROLS_GUIDE.md)** | Legacy control styling (being migrated) | 20 min |

## 🎨 What You Get

The PrintFarmer app has a **three-layer design system** ensuring consistency and ease of maintenance:

**Layer 1: CSS Custom Properties (Theme Variables)**
- `src/styles/theme.css` - Main orchestrator
- `src/styles/themes/github-dark.css`, `printfarmer-dark.css`, `light.css` - Per-theme variables
- 40+ color variables per theme

**Layer 2: CSS Utility Classes**
- `src/styles/components.css` - Reusable component classes (`.pf-control-base`, `.pf-btn-primary`, etc.)
- `src/styles/controls.css` - Legacy control styles (being migrated)

**Layer 3: React Components**
- `src/components/ui/` - Button, Input, Select, Card, Modal, and more

## 🚀 Quick Start

**New to the design system?**

Start with: [`FRONTEND_DESIGN_SYSTEM.md`](./FRONTEND_DESIGN_SYSTEM.md)

Then choose a detailed guide:
- **Using components**: [`FRONTEND_UI_COMPONENTS.md`](./FRONTEND_UI_COMPONENTS.md)
- **Choosing colors**: [`FRONTEND_COLOR_SYSTEM.md`](./FRONTEND_COLOR_SYSTEM.md)
- **Adding themes**: [`FRONTEND_THEME_ARCHITECTURE.md`](./FRONTEND_THEME_ARCHITECTURE.md)

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
- `FRONTEND_DESIGN_SYSTEM.md` - Master index (start here)
- `FRONTEND_THEME_ARCHITECTURE.md` - Theme system and CSS variables
- `FRONTEND_COLOR_SYSTEM.md` - Color tokens and accessibility
- `FRONTEND_UI_COMPONENTS.md` - React component reference
- `CONTROLS_GUIDE.md` - Legacy control styling

**Source Code:**
- `src/styles/theme.css` - Theme system orchestrator
- `src/styles/themes/` - Per-theme CSS variable definitions
- `src/styles/components.css` - Reusable CSS utility classes
- `src/styles/controls.css` - Legacy control styles
- `src/components/ui/` - React component implementations

---

**Get started**: Read [`CONTROLS_GUIDE.md`](./CONTROLS_GUIDE.md)
