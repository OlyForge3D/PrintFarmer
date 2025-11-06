# UI & Styling Documentation Index

Navigation guide for PrintFarmer UI component styling and controls.

## 📚 Documentation

| Document | Purpose | Read Time |
|----------|---------|-----------|
| **[`CONTROLS_GUIDE.md`](./CONTROLS_GUIDE.md)** | Complete reference for explicit control styles | 20 min |

## 🎨 What You Get

The PrintFarmer app has centralized control styling to ensure consistency and ease of maintenance.

**Key files:**
- `src/Web/ReactApp/src/styles/controls.css` - 1,401 lines of control styles (10 organized sections)
- `src/Web/ReactApp/src/index.css` - Imports controls.css automatically

## 🚀 Quick Start

**Read**: [`CONTROLS_GUIDE.md`](./CONTROLS_GUIDE.md)

It includes:
- Organization & philosophy
- Section-by-section breakdown with examples
- CSS variables reference
- Best practices & accessibility notes
- Quick reference table
- How to add new controls

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

## 🔗 Related Files

- `src/Web/ReactApp/src/styles/controls.css` - The actual stylesheet
- `src/Web/ReactApp/src/styles/theme.css` - CSS variables and theme
- `src/Web/ReactApp/src/components/ui/` - React component wrappers (Button, Input, Select, etc.)

---

**Get started**: Read [`CONTROLS_GUIDE.md`](./CONTROLS_GUIDE.md)
