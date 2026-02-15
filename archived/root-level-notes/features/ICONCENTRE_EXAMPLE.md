# iconCenter Property Example

This document demonstrates the new `iconCenter` property added to the Button component.

## Problem

Before this change, icon-only buttons had to use `iconLeft` or `iconRight` with empty children, which caused:
- Unnecessary empty `<span>` elements in the DOM
- Extra whitespace/padding due to the gap-2 class
- Less clear intent in the code

## Solution

The new `iconCenter` property provides a dedicated way to create icon-only buttons with:
- Proper centering via `justify-center` class
- No empty text spans
- Clear semantic meaning in code
- Better accessibility when combined with `aria-label`

## Before

```tsx
// Icon-only button with empty children
<Button 
  size="sm" 
  variant="secondary" 
  iconLeft={<CheckCircleIcon className="w-4 h-4" />}
></Button>
```

This renders:
```html
<button class="... gap-2 ...">
  <span aria-hidden="true">
    <CheckCircleIcon />
  </span>
  <span></span> <!-- Empty span! -->
</button>
```

## After

```tsx
// Icon-only button using iconCenter
<Button 
  size="sm" 
  variant="secondary" 
  iconCenter={<CheckCircleIcon className="w-4 h-4" />}
  aria-label="Select all"
/>
```

This renders:
```html
<button class="... justify-center ..." aria-label="Select all">
  <span aria-hidden="true">
    <CheckCircleIcon />
  </span>
  <!-- No empty span! -->
</button>
```

## Usage Examples

### Icon-only buttons
```tsx
// Delete button
<Button 
  iconCenter={<DeleteIcon />} 
  variant="danger" 
  aria-label="Delete item"
/>

// Edit button
<Button 
  iconCenter={<EditIcon />} 
  variant="subtle" 
  aria-label="Edit"
/>
```

### Regular buttons with icons (use iconLeft/iconRight)
```tsx
// Button with text and left icon
<Button iconLeft={<SaveIcon />}>
  Save Changes
</Button>

// Button with text and right icon
<Button iconRight={<ArrowRightIcon />}>
  Next
</Button>
```

## Accessibility

When using `iconCenter`, always provide an `aria-label` so screen reader users understand the button's purpose:

```tsx
<Button 
  iconCenter={<CloseIcon />} 
  aria-label="Close dialog"
/>
```

## Files Changed

1. `src/common/components/ui/Button.tsx` - Added iconCenter prop and logic
2. `src/features/printers/pages/admin/PrintersAdminPage.tsx` - Updated icon-only buttons
3. `src/test/components/Button.test.tsx` - Added comprehensive tests
4. `UI_COMPONENTS_GUIDE.md` - Updated documentation
