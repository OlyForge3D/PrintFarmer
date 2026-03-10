# Button Icon Prop Audit Report

**Date:** 2025-07-17
**Auditor:** Ripley (Frontend Dev)
**Scope:** All `.tsx` files under `src/Web/ReactApp/src/` (excluding tests)

## Summary

| Metric | Count |
|--------|-------|
| Total `<Button>` instances found | ~805 |
| Buttons using `iconLeft`/`iconRight`/`iconCenter` correctly | ✅ Many |
| Icon-only buttons (no text, icon as child) — acceptable | ✅ ~171 |
| **Violations: icon + text as inline children** | ❌ **27** |

### What counts as a violation?

The project's `Button` component supports `iconLeft`, `iconRight`, and `iconCenter` props for icon placement. When a Button has **both** an icon element **and** text content, the icon should be passed via these props — not rendered as an inline child alongside text.

```tsx
// ✅ CORRECT — icon via prop
<Button iconLeft={<PlusIcon />}>Add Item</Button>

// ❌ VIOLATION — icon as inline child with text
<Button><PlusIcon /> Add Item</Button>
```

**Icon-only buttons** (icon child, no text) are **acceptable** — `iconCenter` or inline icon children both work for icon-only buttons.

---

## Violations

### 1. `common/components/Layout.tsx` — 3 violations

**Line 513** — Profile menu button
```tsx
// ❌ CURRENT
<Button variant="subtle" size="sm" className="flex items-center gap-2 w-full !justify-start">
  <SettingsIcon className="h-4 w-4" />
  <span>Profile</span>
</Button>

// ✅ FIX
<Button variant="subtle" size="sm" className="w-full !justify-start" iconLeft={<SettingsIcon className="h-4 w-4" />}>
  Profile
</Button>
```

**Line 523** — Sign out button
```tsx
// ❌ CURRENT
<Button variant="subtle" size="sm" className="flex items-center gap-2 w-full !justify-start">
  <LogoutIcon className="h-4 w-4" />
  <span>Sign out</span>
</Button>

// ✅ FIX
<Button variant="subtle" size="sm" className="w-full !justify-start" iconLeft={<LogoutIcon className="h-4 w-4" />}>
  Sign out
</Button>
```

**Line 536** — Sign In button
```tsx
// ❌ CURRENT
<Button variant="subtle" size="sm" className="flex items-center gap-2 w-full !justify-start">
  <LoginIcon className="h-4 w-4" />
  <span>Sign In</span>
</Button>

// ✅ FIX
<Button variant="subtle" size="sm" className="w-full !justify-start" iconLeft={<LoginIcon className="h-4 w-4" />}>
  Sign In
</Button>
```

---

### 2. `common/components/UploadProgressButton.tsx` — 1 violation

**Line 35** — Upload button with conditional icon states
```tsx
// ❌ CURRENT (3 states, all with inline icon + text)
<Button className="flex items-center gap-2" ...>
  {isUploading ? (
    <>
      <div className="w-4 h-4 border-2 ... animate-spin" />
      Uploading ({progress}%)...
    </>
  ) : error ? (
    <>
      <UploadIcon className="w-4 h-4" />
      Upload Failed
    </>
  ) : (
    <>
      <UploadIcon className="w-4 h-4" />
      {label}
    </>
  )}
</Button>

// ✅ FIX — Use iconLeft prop with conditional icon
<Button
  iconLeft={isUploading
    ? <div className="w-4 h-4 border-2 border-transparent border-t-current rounded-full animate-spin" />
    : <UploadIcon className="w-4 h-4" />
  }
  ...
>
  {isUploading ? `Uploading (${progress}%)...` : error ? 'Upload Failed' : label}
</Button>
```

---

### 3. `features/admin/pages/DataManagementPage.tsx` — 4 violations

**Lines 186, 195, 204** — Export buttons
```tsx
// ❌ CURRENT (same pattern x3)
<Button variant="secondary" className="w-full">
  <DownloadIcon className="w-4 h-4 mr-2" />
  Export Catalog
</Button>

// ✅ FIX
<Button variant="secondary" className="w-full" iconLeft={<DownloadIcon className="w-4 h-4" />}>
  Export Catalog
</Button>
```

**Line 373** — Reload button
```tsx
// ❌ CURRENT
<Button variant="secondary">
  <RefreshIcon className="w-4 h-4 mr-2" />
  Reload Seed Data
</Button>

// ✅ FIX
<Button variant="secondary" iconLeft={<RefreshIcon className="w-4 h-4" />}>
  Reload Seed Data
</Button>
```

---

### 4. `features/admin/pages/TagAdminPage.tsx` — 1 violation

**Line 684** — Create Tag button with conditional loading icon
```tsx
// ❌ CURRENT
<Button variant="primary" ...>
  {createTagMutation.isPending && (
    <LoadingIcon className="w-4 h-4 mr-2 animate-spin" />
  )}
  Create Tag
</Button>

// ✅ FIX
<Button
  variant="primary"
  loading={createTagMutation.isPending}
  ...
>
  Create Tag
</Button>
```
> Note: The `Button` component already has a `loading` prop that handles this pattern natively.

---

### 5. `features/cameras/components/CameraManagementPanel.tsx` — 1 violation

**Line 495** — Printer import button (complex children with icon)
```tsx
// ❌ CURRENT
<Button variant="unstyled" className="w-full p-4 text-left ...">
  <div className="flex items-center gap-3">
    <PrinterIcon className="w-8 h-8 text-pf-text-tertiary shrink-0" />
    <div className="flex-1 min-w-0">
      <div className="font-medium">{printer.name}</div>
      ...
    </div>
  </div>
</Button>

// ✅ FIX — This is an unstyled card-like button with complex layout.
// iconLeft won't work well here since the layout is custom.
// RECOMMENDATION: Keep as-is. This is an intentional use of unstyled variant
// with custom internal layout — not a standard icon+text button.
```
> **Verdict:** FALSE POSITIVE — complex card-like button layout is acceptable with `variant="unstyled"`.

---

### 6. `features/gcode/components/GcodeFileBrowser.tsx` — 2 violations

**Line 571** — Tag selection button
```tsx
// ❌ CURRENT
<Button variant="secondary" size="sm">
  <TagIcon className="h-4 w-4 mr-1" />
  Tag ({selection.length})
</Button>

// ✅ FIX
<Button variant="secondary" size="sm" iconLeft={<TagIcon className="h-4 w-4" />}>
  Tag ({selection.length})
</Button>
```

**Line 583** — Delete selection button
```tsx
// ❌ CURRENT
<Button variant="secondary" size="sm" className="text-pf-error ...">
  <DeleteIcon className="h-4 w-4 mr-1" />
  Delete ({selection.length})
</Button>

// ✅ FIX
<Button variant="secondary" size="sm" className="text-pf-error ..." iconLeft={<DeleteIcon className="h-4 w-4" />}>
  Delete ({selection.length})
</Button>
```

---

### 7. `features/gcode/pages/HarvestPage.tsx` — 2 violations

**Lines 190, 367** — Start Harvest buttons (duplicate pattern)
```tsx
// ❌ CURRENT
<Button variant="primary">
  <PlusIcon className="w-4 h-4 mr-2" />
  Start Harvest
</Button>

// ✅ FIX
<Button variant="primary" iconLeft={<PlusIcon className="w-4 h-4" />}>
  Start Harvest
</Button>
```

---

### 8. `features/gcode/components/harvest/HarvestOperationCard.tsx` — 1 violation

**Line 157** — Cancel button with conditional icon states
```tsx
// ❌ CURRENT
<Button variant="danger">
  {cancelMutation.isPending ? (
    <span className="flex items-center">
      <StopIcon className="w-4 h-4 mr-1 animate-spin" />
      Cancelling...
    </span>
  ) : (
    <span className="flex items-center">
      <StopIcon className="w-4 h-4 mr-1" />
      Cancel
    </span>
  )}
</Button>

// ✅ FIX
<Button
  variant="danger"
  iconLeft={<StopIcon className={`w-4 h-4 ${cancelMutation.isPending ? 'animate-spin' : ''}`} />}
  loading={cancelMutation.isPending}
>
  {cancelMutation.isPending ? 'Cancelling...' : 'Cancel'}
</Button>
```

---

### 9. `features/maintenance/components/ComponentReplacementHistory.tsx` — 1 violation

**Line 141** — Sort by Date button
```tsx
// ❌ CURRENT
<Button variant="subtle" size="sm">
  <SortIcon className="h-4 w-4 mr-1" />
  Date
</Button>

// ✅ FIX
<Button variant="subtle" size="sm" iconLeft={<SortIcon className="h-4 w-4" />}>
  Date
</Button>
```

---

### 10. `features/maintenance/components/MaintenancePlansTabV2.tsx` — 1 violation

**Line 403** — Plan expand/collapse button (complex children)
```tsx
// ❌ CURRENT
<Button variant="unstyled" className="flex items-center gap-3 flex-1 ...">
  <span className="shrink-0 text-pf-text-tertiary">
    {isExpanded ? <ChevronDownIcon /> : <ChevronRightIcon />}
  </span>
  <span className="flex-1 min-w-0">
    <span>{plan.name}</span>
    ...
  </span>
</Button>

// ✅ FIX — Complex card-like layout with unstyled variant.
// RECOMMENDATION: Keep as-is. This is an intentional accordion toggle with
// complex multi-line content — not a standard icon+text button.
```
> **Verdict:** FALSE POSITIVE — accordion-style button with complex layout is acceptable with `variant="unstyled"`.

---

### 11. `features/maintenance/pages/PrinterMaintenancePage.tsx` — 2 violations

**Line 137** — Back button
```tsx
// ❌ CURRENT
<Button variant="ghost" className="gap-2">
  <ArrowLeftIcon className="h-4 w-4" />
  Back
</Button>

// ✅ FIX
<Button variant="ghost" iconLeft={<ArrowLeftIcon className="h-4 w-4" />}>
  Back
</Button>
```

**Line 145** — Log Maintenance button
```tsx
// ❌ CURRENT
<Button variant="primary" className="gap-2">
  <PlusIcon className="h-4 w-4" />
  Log Maintenance
</Button>

// ✅ FIX
<Button variant="primary" iconLeft={<PlusIcon className="h-4 w-4" />}>
  Log Maintenance
</Button>
```

---

### 12. `features/models3d/components/ModelsFileBrowser.tsx` — 1 violation

**Line 331** — Tag selected models button
```tsx
// ❌ CURRENT
<Button variant="secondary" size="sm">
  <TagIcon className="mr-1 h-4 w-4" />
  ({selection.length})
</Button>

// ✅ FIX
<Button variant="secondary" size="sm" iconLeft={<TagIcon className="h-4 w-4" />}>
  ({selection.length})
</Button>
```

---

### 13. `features/printers/components/MmuControlBox.tsx` — 1 violation

**Line 428** — Eject button
```tsx
// ❌ CURRENT
<Button variant="secondary" size="sm" className="flex-1">
  <EjectIcon className="w-4 h-4 mr-1" ariaLabel="" />
  Eject
</Button>

// ✅ FIX
<Button variant="secondary" size="sm" className="flex-1" iconLeft={<EjectIcon className="w-4 h-4" ariaLabel="" />}>
  Eject
</Button>
```

---

### 14. `features/printers/components/SpoolPickerModal.tsx` — 2 violations

**Line 314** — Back to materials button
```tsx
// ❌ CURRENT
<Button variant="unstyled" className="flex items-center gap-1 text-xs ...">
  <ChevronLeftIcon className="w-4 h-4" />
  Back
</Button>

// ✅ FIX
<Button variant="unstyled" className="text-xs ..." iconLeft={<ChevronLeftIcon className="w-4 h-4" />}>
  Back
</Button>
```

**Line 394** — Clear filters button
```tsx
// ❌ CURRENT
<Button variant="unstyled" className="flex items-center gap-1 text-[10px] ...">
  <CloseIcon className="w-3 h-3" />
  Clear filters
</Button>

// ✅ FIX
<Button variant="unstyled" className="text-[10px] ..." iconLeft={<CloseIcon className="w-3 h-3" />}>
  Clear filters
</Button>
```

---

### 15. `features/slicer/components/viewer/SlicerToolbar.tsx` — 2 violations

**Line 41** — ToolbarButton wrapper component (affects ~14 usages)
```tsx
// ❌ CURRENT
<Button variant={active ? 'primary' : 'subtle'} ...>
  {icon}
  {label && <span className="ml-2 text-sm hidden xl:inline">{label}</span>}
</Button>

// ✅ FIX
<Button
  variant={active ? 'primary' : 'subtle'}
  iconLeft={icon}
  ...
>
  {label && <span className="text-sm hidden xl:inline">{label}</span>}
</Button>
```
> Note: When no `label` is provided, this becomes an icon-only button (acceptable).
> When `label` IS provided, this is a violation. The `iconLeft` prop handles the gap automatically,
> so the `ml-2` class can be removed.

**Line 202** — Settings & Profiles button
```tsx
// ❌ CURRENT
<Button variant="primary" className="flex items-center gap-2 px-3 py-1.5">
  <SettingsProfilesIcon className="w-4 h-4" />
  <span className="text-sm font-medium">SETTINGS & PROFILES</span>
</Button>

// ✅ FIX
<Button variant="primary" className="px-3 py-1.5" iconLeft={<SettingsProfilesIcon className="w-4 h-4" />}>
  <span className="text-sm font-medium">SETTINGS & PROFILES</span>
</Button>
```

---

### 16. `features/slicer/pages/NewSliceJobPage.tsx` — 1 violation

**Line 1017** — Preview 3D Model button
```tsx
// ❌ CURRENT
<Button variant="secondary" size="sm" className="w-full flex items-center justify-center gap-2">
  <EyeIcon className="w-4 h-4" />
  Preview 3D Model
</Button>

// ✅ FIX
<Button variant="secondary" size="sm" className="w-full" iconLeft={<EyeIcon className="w-4 h-4" />}>
  Preview 3D Model
</Button>
```

---

### 17. `features/webhooks/pages/WebhooksAdminPage.tsx` — 4 violations

**Lines 81, 93** — Add Webhook buttons (duplicate pattern)
```tsx
// ❌ CURRENT
<Button variant="primary" onClick={() => setShowCreateModal(true)}>
  <PlusIcon className="w-4 h-4" />
  <span>Add Webhook</span>
</Button>

// ✅ FIX
<Button variant="primary" onClick={() => setShowCreateModal(true)} iconLeft={<PlusIcon className="w-4 h-4" />}>
  Add Webhook
</Button>
```

**Line 159** — Delete button with conditional loading icon
```tsx
// ❌ CURRENT
<Button variant="danger" disabled={deleteMutation.isPending}>
  {deleteMutation.isPending ? <LoadingIcon className="w-4 h-4 pf-animate-spin" /> : 'Delete'}
</Button>

// ✅ FIX
<Button variant="danger" loading={deleteMutation.isPending}>
  Delete
</Button>
```
> Note: The `Button` component's `loading` prop handles this natively with "Please wait…" text.

**Line 360** — Submit button with conditional loading icon
```tsx
// ❌ CURRENT
<Button variant="primary" type="submit" disabled={isSubmitting}>
  {isSubmitting ? <LoadingIcon className="w-4 h-4 pf-animate-spin" /> : isEdit ? 'Save' : 'Create'}
</Button>

// ✅ FIX
<Button variant="primary" type="submit" loading={isSubmitting}>
  {isEdit ? 'Save' : 'Create'}
</Button>
```

---

## False Positives (Acceptable Patterns)

These were flagged by automated scanning but are **not violations**:

1. **Icon-only buttons** (~171 instances) — Buttons with only an icon child and no text. These are fine; `iconCenter` is preferred but inline icon child works.
2. **`variant="unstyled"` with complex card-like layouts** — Buttons used as clickable cards with custom multi-element layouts (e.g., `CameraManagementPanel.tsx:495`, `MaintenancePlansTabV2.tsx:403`). The `iconLeft`/`iconRight` pattern doesn't apply to these.
3. **Conditional icon-OR-text patterns** — Buttons where a ternary renders either an icon (loading) or text (not loading), but never both simultaneously (e.g., `WebhooksAdminPage.tsx:159`). These are better served by the `loading` prop.

## Patterns Observed

### Most Common Violation Pattern
```tsx
// Icon with mr-2 margin class + text child
<Button><SomeIcon className="w-4 h-4 mr-2" />Some Text</Button>
```
The `mr-2` is a manual spacing hack that `iconLeft` handles automatically via the Button's built-in `gap-2` class.

### Loading State Anti-Pattern
```tsx
// Manual loading icon conditional
{isPending && <LoadingIcon className="animate-spin" />}
```
The Button's `loading` prop does this natively and shows "Please wait…" as the text.

### Redundant `className` Additions
Many violations include `className="flex items-center gap-2"` — but the Button component already applies `inline-flex items-center gap-2` by default. Using `iconLeft`/`iconRight` eliminates the need for these manual class additions.

## Recommendations

1. **Fix all 25 true violations** (excluding 2 false positives) to use `iconLeft`/`iconRight` props.
2. **Replace manual loading patterns** with the `loading` prop where applicable.
3. **Remove redundant `className` overrides** like `flex items-center gap-2` and manual `mr-2` on icons.
4. **Consider an ESLint rule** to catch future violations (e.g., detecting `<Icon>` elements as direct children of `<Button>` when text is also present).
