---
name: "icon-only-badges"
description: "Icon-only badges lose implicit affordance; compensate by layering tooltip, aria-label, and focus indicators to maintain semantic clarity and accessibility."
domain: "frontend-a11y"
confidence: "high"
source: "earned"
---

## Context
Use this when refactoring a text + icon badge to icon-only form (e.g., shield icon removing "Guarding" label). Icon-only designs improve visual density but remove the explicit signal that users expect. Accessible implementations must provide alternative information pathways.

## Patterns

### Information Hierarchy for Icon-Only Badges
1. **Visual**: Icon shape + background color/pattern (primary signal for sighted users)
2. **Tooltip (title attribute)**: Full semantic label + optional hint (secondary, on hover/focus)
3. **aria-label**: Concise label for screen reader users (tertiary, navigation aid)
4. **Focus ring**: Clear keyboard focus indicator (essential for keyboard navigation)

### Tooltip as Contract
- Icon-only design makes tooltip THE information source, not a nice-to-have
- Tooltip must include state label (e.g., "Guarding", "Checking", "Ready")
- Tooltip should include action hint if clickable (e.g., "open spaghetti detection details")
- Format: `title={`${stateLabel} • ${actionHint}`}`

### Accessibility Verification
- **Screen reader**: aria-label announces on focus; title may or may not be announced (test with NVDA, JAWS)
- **Keyboard**: Focus ring must be visible; title is typically announced on hover/focus
- **Color-blind users**: Icon shape or pattern alone must differentiate states; don't rely on color alone
- **Sighted users without hover**: Desktop users may never see tooltip; aria-label + focus ring carry weight

### Integration Testing
- Icon-only badges in crowded layouts (e.g., card headers with multiple badges) can cause alignment shifts
- Test that removing label text doesn't cause sibling badges or text to wrap or reflow unexpectedly
- Verify tooltip doesn't get clipped or cut off at edge of viewport

## Examples
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringBadge.tsx` (shield icon badge)
- Upcoming icon-only refactor: Guarding/Checking/Ready states in tooltip only

## Anti-Patterns
- Removing label text without adding tooltip (users lose context)
- Icon-only badge with no focus ring (keyboard users can't tell if it's interactive)
- Tooltip that doesn't include state label (tooltip becomes unhelpful hint instead of information source)
- Color-only differentiation for states (color-blind users can't distinguish "guarding" from "checking")
- Assuming title attribute is always announced by screen readers (it's not; vary by user agent)

## Test Sketch
```typescript
// Tooltip Content Assertion
it('icon-only badge shows state label in tooltip', () => {
  render(<IconOnlyBadge state="guarding" />);
  const button = screen.getByRole('button');
  expect(button).toHaveAttribute('title', /Guarding/);
});

// aria-label for Screen Readers
it('announces state via aria-label for screen reader users', () => {
  render(<IconOnlyBadge state="guarding" />);
  expect(screen.getByLabelText(/guarding/i)).toBeInTheDocument();
});

// Focus Visibility
it('shows visible focus ring on keyboard focus', () => {
  render(<IconOnlyBadge state="guarding" />);
  const button = screen.getByRole('button');
  fireEvent.focus(button);
  // Check for focus-visible class or outline
  expect(button).toHaveClass('focus-visible:ring-2');
});

// Layout Regression
it('icon-only badge does not cause sibling text to wrap in compact header', () => {
  render(
    <div className="flex gap-2">
      <span>Printer Name</span>
      <IconOnlyBadge state="guarding" />
      <span>Another Badge</span>
    </div>
  );
  // Assert no unexpected layout shift (may require visual snapshot test)
});
```
