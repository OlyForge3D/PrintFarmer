# Form Control Gating Pattern

**Domain:** Frontend UX  
**Context:** React forms with prerequisites  
**Last Updated:** 2026-03-29

## When to Use

Use this pattern when a form control has a **prerequisite** that:
1. Can be satisfied within the same form
2. Should be visually communicated to the user
3. Prevents invalid form states

## Pattern

Gate the control with:
1. **Disabled state** when prerequisite not met
2. **Conditional helper text** that directs user to solution
3. **Defensive validation** as backup

## Implementation

### Structure

```tsx
<FormField
  label="Feature Name"
  htmlFor="feature-toggle"
  helper={
    !prerequisitesMet
      ? "Short, action-oriented instruction to satisfy prerequisite."
      : "Normal helper text explaining the feature."
  }
>
  <Checkbox
    id="feature-toggle"
    checked={enabled}
    disabled={!prerequisitesMet}
    onChange={handleChange}
    label="Enable this feature"
  />
</FormField>
```

### Helper Text Guidelines

**When disabled:**
- Short (under 60 characters)
- Action-oriented (verb + what to do)
- Points to solution location ("above", "below", "in settings")

**Examples:**
- ✅ "Configure a camera URL above to enable failure detection."
- ✅ "Add at least one material below to enable auto-assignment."
- ❌ "This feature requires a camera." (no action)
- ❌ "You must configure a camera URL before you can enable failure detection." (verbose)

**When enabled:**
- Full feature description
- Prerequisites already explained elsewhere

### Defensive Validation

Keep backup validation in `onChange`:

```tsx
onChange={e => {
  const enabling = e.target.checked;
  if (enabling && !prerequisitesMet) {
    toast.error('Feature requires prerequisite. Action first.');
    return;
  }
  handleChange(enabling);
}}
```

**Why?** Handles edge cases (form state changes, race conditions).

## Testing Pattern

Test 3 states:
1. Control disabled when prerequisite missing
2. Control enabled when prerequisite satisfied (variation A)
3. Control enabled when prerequisite satisfied (variation B, if multiple ways)

```tsx
it('disables control when prerequisite missing', async () => {
  // Mock data with missing prerequisite
  render(<Component />);
  
  const control = await screen.findByLabelText(/feature toggle/i);
  expect(control).toBeDisabled();
  expect(screen.getByText(/action instruction/i)).toBeInTheDocument();
});

it('enables control when prerequisite satisfied', async () => {
  // Mock data with prerequisite present
  render(<Component />);
  
  const control = await screen.findByLabelText(/feature toggle/i);
  expect(control).not.toBeDisabled();
});
```

## Real-World Example

**Obico AI Monitoring** in `EditPrinterModal.tsx`:
- **Prerequisite:** Camera URL (stream OR snapshot)
- **Disabled text:** "Configure a camera URL above to enable failure detection."
- **Enabled text:** Full Obico feature description
- **Defensive:** Toast error in onChange

## Related Patterns

- **Progressive Disclosure** — Show controls as prerequisites are met
- **Inline Validation** — Real-time feedback on form state
- **Contextual Help** — Adapt help text to user's current state

## Anti-Patterns

❌ **Hiding the control** — Less discoverable, users don't know feature exists  
❌ **Toast-only validation** — Reactive, not proactive; users click first  
❌ **Modal dialogs** — Interrupts flow; inline guidance better  
❌ **Verbose helper text** — Clutters UI; keep it short

## Exceptions

**Don't use this pattern when:**
- Prerequisites are complex (multiple steps) → Use wizard/multi-step form
- Prerequisites are in different UI area → Use inline alert/banner
- Control is rarely needed → Hide it entirely until relevant
