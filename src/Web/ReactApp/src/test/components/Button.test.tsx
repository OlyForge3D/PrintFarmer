import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Button, type ButtonVariant } from '@/common/components/ui/Button';

const FORBIDDEN_PAINT_PATTERNS: Array<[string, RegExp]> = [
  ['background shorthand', /\[background:/],
  ['background-color utility', /(?:^|\s|:)bg-/],
  ['colour utility', /(?:^|\s|:)text-(?:inherit|white|black|current|pf-|\[)/],
  ['border-colour utility', /(?:^|\s|:)border-(?:transparent|current|pf-|\[)/],
  ['box-shadow utility', /(?:^|\s|:)shadow-/]
];

describe('Button', () => {
  describe('iconCenter prop', () => {
    it('renders icon-only button with iconCenter', () => {
      render(
        <Button 
          iconCenter={<span data-testid="test-icon">Icon</span>} 
          aria-label="Icon button"
        />
      );
      
      const icon = screen.getByTestId('test-icon');
      expect(icon).toBeInTheDocument();
      expect(icon).toHaveTextContent('Icon');
      
      const button = screen.getByRole('button');
      expect(button).toBeInTheDocument();
    });

    it('centers icon properly with iconCenter', () => {
      render(
        <Button 
          iconCenter={<span data-testid="centered-icon">⭐</span>}
          aria-label="Star"
        />
      );
      
      const button = screen.getByRole('button');
      expect(button).toHaveClass('justify-center');
    });

    it('does not render children text when iconCenter is used', () => {
      render(
        <Button 
          iconCenter={<span data-testid="icon">Icon</span>}
          aria-label="Icon button"
        >
          This text should not appear
        </Button>
      );
      
      const icon = screen.getByTestId('icon');
      expect(icon).toBeInTheDocument();
      
      // Text should not be rendered when iconCenter is present
      expect(screen.queryByText('This text should not appear')).not.toBeInTheDocument();
    });

    it('shows loading state for iconCenter button', () => {
      render(
        <Button 
          iconCenter={<span data-testid="icon">Icon</span>}
          loading={true}
          aria-label="Loading"
        />
      );
      
      const button = screen.getByRole('button');
      expect(button).toBeDisabled();
      expect(button).toHaveTextContent('Loading...');
    });
  });

  describe('regular button with iconLeft/iconRight', () => {
    it('renders button with iconLeft and children', () => {
      render(
        <Button iconLeft={<span data-testid="left-icon">←</span>}>
          Click me
        </Button>
      );
      
      const icon = screen.getByTestId('left-icon');
      expect(icon).toBeInTheDocument();
      expect(screen.getByText('Click me')).toBeInTheDocument();
      
      const button = screen.getByRole('button');
      expect(button).toHaveClass('justify-center');
    });

    it('renders button with iconRight and children', () => {
      render(
        <Button iconRight={<span data-testid="right-icon">→</span>}>
          Click me
        </Button>
      );
      
      const icon = screen.getByTestId('right-icon');
      expect(icon).toBeInTheDocument();
      expect(screen.getByText('Click me')).toBeInTheDocument();
    });

    it('renders button with both iconLeft and iconRight', () => {
      render(
        <Button 
          iconLeft={<span data-testid="left-icon">←</span>}
          iconRight={<span data-testid="right-icon">→</span>}
        >
          Both icons
        </Button>
      );
      
      expect(screen.getByTestId('left-icon')).toBeInTheDocument();
      expect(screen.getByTestId('right-icon')).toBeInTheDocument();
      expect(screen.getByText('Both icons')).toBeInTheDocument();
    });

    it('handles loading state with text', () => {
      render(
        <Button loading={true}>
          Submit
        </Button>
      );
      
      const button = screen.getByRole('button');
      expect(button).toBeDisabled();
      expect(button).toHaveTextContent('Please wait…');
      expect(screen.queryByText('Submit')).not.toBeInTheDocument();
    });

    it('does not render empty text span when no children provided', () => {
      render(
        <Button 
          iconLeft={<span data-testid="icon">Icon</span>}
          aria-label="Icon only"
        />
      );
      
      // Check that the button still renders the icon
      const icon = screen.getByTestId('icon');
      expect(icon).toBeInTheDocument();
      
      // The button should have icon wrapper span and conditional children span
      const button = screen.getByRole('button');
      const spans = button.querySelectorAll('span');
      
      // Should have one span for the icon wrapper
      expect(spans.length).toBeGreaterThanOrEqual(1);
      expect(icon.parentElement).toHaveAttribute('aria-hidden', 'true');
    });
  });

  describe('button variants', () => {
    it('applies correct variant classes', () => {
      const { rerender } = render(<Button variant="primary">Primary</Button>);
      let button = screen.getByRole('button');
      expect(button.className).toContain('bg-[var(--pf-button-primary-bg)]');
      expect(button.className).toContain('text-[var(--pf-on-accent)]');
      
      rerender(<Button variant="secondary">Secondary</Button>);
      button = screen.getByRole('button');
      expect(button).toHaveClass('bg-pf-bg-2');
      
      rerender(<Button variant="danger">Danger</Button>);
      button = screen.getByRole('button');
      expect(button).toHaveAttribute('data-pf-variant', 'danger');
    });

    it('uses dedicated semantic tokens for the danger surface and hover state', () => {
      render(<Button variant="danger">Danger</Button>);
      const button = screen.getByRole('button', { name: 'Danger' });

      expect(button).toHaveClass(
        'bg-[var(--pf-button-danger-bg)]',
        'enabled:hover:bg-[var(--pf-button-danger-hover)]',
        'text-[var(--pf-on-danger)]',
        'border-[var(--pf-button-danger-border)]',
      );
      expect(button).not.toHaveClass(
        'bg-pf-error',
        'enabled:hover:bg-pf-error-hover',
        'text-[var(--pf-text-inverse)]',
        'hover:opacity-90',
        'active:opacity-75',
      );
    });

    it('uses dedicated semantic tokens for the success surface and hover state', () => {
      render(<Button variant="success">Success</Button>);
      const button = screen.getByRole('button', { name: 'Success' });

      expect(button).toHaveClass(
        'bg-[var(--pf-button-success-bg)]',
        'enabled:hover:bg-[var(--pf-button-success-hover)]',
        'text-[var(--pf-button-success-text)]',
        'border-[var(--pf-button-success-border)]',
      );
      expect(button).not.toHaveClass(
        'bg-pf-success-bg',
        'enabled:hover:bg-pf-success-hover',
        'text-white',
        'text-pf-success-text',
      );
    });
  });

  describe('button sizes', () => {
    it('applies correct size classes', () => {
      const { rerender } = render(<Button size="sm">Small</Button>);
      let button = screen.getByRole('button');
      expect(button).toHaveClass('text-xs', 'px-2', 'py-1');
      
      rerender(<Button size="md">Medium</Button>);
      button = screen.getByRole('button');
      expect(button).toHaveClass('text-sm', 'px-4', 'py-2');
      
      rerender(<Button size="lg">Large</Button>);
      button = screen.getByRole('button');
      expect(button).toHaveClass('text-base', 'px-6', 'py-3');
    });
  });

  describe('disabled state', () => {
    it('disables button when disabled prop is true', () => {
      render(<Button disabled>Disabled</Button>);
      
      const button = screen.getByRole('button');
      expect(button).toBeDisabled();
      // Note: disabled:opacity-50 is a conditional class that applies only when disabled
      expect(button.className).toContain('disabled:opacity-50');
      expect(button.className).toContain('disabled:cursor-not-allowed');
    });

    it('disables button when loading', () => {
      render(<Button loading>Loading</Button>);
      
      const button = screen.getByRole('button');
      expect(button).toBeDisabled();
    });
  });

  describe('custom className', () => {
    it('applies custom className', () => {
      render(<Button className="custom-class">Custom</Button>);
      
      const button = screen.getByRole('button');
      expect(button).toHaveClass('custom-class');
    });
  });

  describe('accessibility', () => {
    it('supports aria-label for icon-only buttons', () => {
      render(
        <Button 
          iconCenter={<span>Icon</span>}
          aria-label="Delete item"
        />
      );
      
      const button = screen.getByRole('button', { name: 'Delete item' });
      expect(button).toBeInTheDocument();
    });

    it('marks icons as aria-hidden when using iconLeft', () => {
      render(
        <Button iconLeft={<span data-testid="icon">Icon</span>}>
          Text
        </Button>
      );
      
      const iconWrapper = screen.getByTestId('icon').parentElement;
      expect(iconWrapper).toHaveAttribute('aria-hidden', 'true');
    });
    
    it('marks icons as aria-hidden when using iconCenter', () => {
      render(
        <Button 
          iconCenter={<span data-testid="center-icon">Icon</span>}
          aria-label="Icon button"
        />
      );
      
      const iconWrapper = screen.getByTestId('center-icon').parentElement;
      expect(iconWrapper).toHaveAttribute('aria-hidden', 'true');
    });
  });

  // Regression guard for #1087. The ghost variant used to declare its surface as
  // Tailwind utilities: the CSS `background` shorthand set to none, plus colour,
  // border-colour and box-shadow resets. Those sit in `@layer utilities` alongside
  // everything a caller passes through `className`, where they competed on source
  // order and won — silently killing caller backgrounds, gradients, text colours
  // and shadows. The defaults now live in `@layer components`
  // (`styles/controls.css`) keyed off `[data-pf-variant='ghost']`, so caller
  // utilities win on layer order.
  //
  // The class names are described rather than written out on purpose: Tailwind
  // scans this file as source, so naming a utility even in a comment makes it
  // emit the rule again.
  //
  // A unit test cannot observe the cascade, but it can observe the precondition:
  // ghost must contribute no paint utility of its own.
  describe('ghost variant does not emit paint utilities (#1087)', () => {
    it('contributes no background, colour, border-colour or shadow class', () => {
      render(<Button variant="ghost">Ghost</Button>);
      const className = screen.getByRole('button', { name: 'Ghost' }).className;

      for (const [label, pattern] of FORBIDDEN_PAINT_PATTERNS) {
        expect(
          pattern.test(className),
          `ghost variant emitted a ${label} (${className}); it would defeat caller ` +
            'classes on source order — see #1087'
        ).toBe(false);
      }
    });

    it('exposes the variant so the components layer can style it', () => {
      render(<Button variant="ghost">Ghost</Button>);
      expect(screen.getByRole('button', { name: 'Ghost' })).toHaveAttribute(
        'data-pf-variant',
        'ghost'
      );
    });

    it('preserves caller paint classes verbatim', () => {
      render(
        <Button variant="ghost" className="bg-pf-accent-bg text-[var(--pf-on-accent)]">
          Active
        </Button>
      );
      const button = screen.getByRole('button', { name: 'Active' });
      expect(button).toHaveClass('bg-pf-accent-bg');
      expect(button).toHaveClass('text-[var(--pf-on-accent)]');
    });
  });

  // Regression guard for #1102 — the same defect as #1087, in the four variants
  // that were left behind when ghost was fixed. Each declared its surface as
  // Tailwind utilities (transparent background, a secondary text colour, a
  // transparent border colour, and — worse — an `enabled:hover:` background).
  // Those live in `@layer utilities` next to everything a caller passes via
  // `className`, so they were decided by raw stylesheet source order and won:
  // Tailwind v4 sorts colour utilities alphabetically, which puts the
  // transparent background after every plain palette background, and puts the
  // `enabled:hover:` variant after plain `hover:`. Caller fills AND caller
  // hovers were both suppressed. Defaults now live in `@layer components`
  // (`styles/controls.css`) keyed off `[data-pf-variant]`, where layer order
  // beats specificity and source order in every state at once.
  //
  // As in the ghost block above, the forbidden class names are described rather
  // than written out: Tailwind scans this file as source, so naming a utility
  // even inside a comment re-emits its rule.
  //
  // A unit test cannot observe the cascade — that gap is tracked as #1122 — but
  // it can observe the precondition: these variants must contribute no paint
  // utility of their own, in any state.
  describe('subtle, tab, toggle and link emit no paint utilities (#1102)', () => {
    const VARIANTS = ['subtle', 'tab', 'toggle', 'link'] as const;

    // The shared base string contributes one box-shadow utility of its own —
    // but only to variants that opt in. `applyShadow` in Button.tsx excludes
    // ghost, link and unstyled, so filtering the token unconditionally would
    // blind this guard to a real shadow utility declared by `link`. The set
    // below must track `applyShadow`.
    //
    // That base shadow is a base-level concern affecting all opted-in variants,
    // not the four this issue is about, so it is filtered rather than silently
    // widening #1102. Tracked separately as #1127.
    const BASE_SHADOW_VARIANTS = new Set<ButtonVariant>(['subtle', 'tab', 'toggle']);

    // Exactly one box-shadow utility comes from the base, so exactly one is
    // dropped. `clsx` does not deduplicate — that is the very property that
    // makes #1102 possible — so a variant that declares the same utility shows
    // up as a second occurrence. Filtering every occurrence would swallow it
    // and blind this guard to the variants most likely to regress.
    const variantContributed = (variant: ButtonVariant, className: string) => {
      const tokens = className.split(/\s+/).filter(Boolean);
      if (!BASE_SHADOW_VARIANTS.has(variant)) return tokens;

      const baseShadow = tokens.indexOf('shadow-xs');
      return baseShadow === -1
        ? tokens
        : [...tokens.slice(0, baseShadow), ...tokens.slice(baseShadow + 1)];
    };

    it.each(VARIANTS)(
      '%s contributes no background, colour, border-colour or shadow class',
      (variant) => {
        render(<Button variant={variant}>Label</Button>);
        const tokens = variantContributed(
          variant,
          screen.getByRole('button', { name: 'Label' }).className
        );

        for (const [label, pattern] of FORBIDDEN_PAINT_PATTERNS) {
          const offender = tokens.find((token) => pattern.test(token));
          expect(
            offender,
            `${variant} variant emitted a ${label} ("${offender}"); it would defeat ` +
              'caller classes on source order — see #1102'
          ).toBeUndefined();
        }
      }
    );

    // Keeps BASE_SHADOW_VARIANTS honest in both directions: if the base stops
    // giving these variants a shadow the filter becomes a silent no-op, and if
    // it starts giving `link` one the filter would need to grow. Either drift
    // reopens the hole this filter could otherwise hide. The count is asserted
    // exactly, because the filter drops precisely one occurrence: were the base
    // to contribute two, one would leak through and read as variant-declared.
    it.each([...BASE_SHADOW_VARIANTS])(
      '%s really does receive the base shadow the filter assumes, exactly once',
      (variant) => {
        render(<Button variant={variant}>Label</Button>);
        const shadows = screen
          .getByRole('button', { name: 'Label' })
          .className.split(/\s+/)
          .filter((token) => token === 'shadow-xs');

        expect(shadows).toHaveLength(1);
      }
    );

    it('link receives no base shadow, so nothing is filtered for it', () => {
      render(<Button variant="link">Label</Button>);
      expect(
        screen.getByRole('button', { name: 'Label' }).className.split(/\s+/)
      ).not.toContain('shadow-xs');
      expect(BASE_SHADOW_VARIANTS.has('link')).toBe(false);
    });

    it.each(VARIANTS)('%s exposes its variant to the components layer', (variant) => {
      render(<Button variant={variant}>Label</Button>);
      expect(screen.getByRole('button', { name: 'Label' })).toHaveAttribute(
        'data-pf-variant',
        variant
      );
    });

    // The hover half of #1102: a state-scoped paint utility is just as fatal as
    // a resting one, and is easier to miss because it only shows up under a
    // state prefix.
    it.each(VARIANTS)('%s declares no state-scoped paint utility', (variant) => {
      render(<Button variant={variant}>Label</Button>);
      const className = screen.getByRole('button', { name: 'Label' }).className;

      for (const token of className.split(/\s+/).filter(Boolean)) {
        const stateScoped = /^(?:[a-z0-9-]+:)+/.exec(token);
        if (!stateScoped) continue;
        const utility = token.slice(stateScoped[0].length);
        expect(
          /^(?:bg-|text-(?:inherit|white|black|current|pf-|\[)|border-(?:transparent|current|pf-|\[)|shadow-)/.test(
            utility
          ),
          `${variant} variant emitted the state-scoped paint utility "${token}"; ` +
            'state-prefixed utilities sort after their unprefixed form, so it ' +
            'would defeat a caller hover — see #1102'
        ).toBe(false);
      }
    });

    // The active tab exposes a hook instead of paint utilities, so the
    // components layer can style it without competing with caller classes.
    it('marks the active tab with data-pf-active and no paint utility', () => {
      render(
        <Button variant="tab" active>
          Active tab
        </Button>
      );
      const button = screen.getByRole('button', { name: 'Active tab' });
      expect(button).toHaveAttribute('data-pf-active');

      const tokens = variantContributed('tab', button.className);
      for (const [label, pattern] of FORBIDDEN_PAINT_PATTERNS) {
        const offender = tokens.find((token) => pattern.test(token));
        expect(
          offender,
          `active tab emitted a ${label} ("${offender}") — see #1102`
        ).toBeUndefined();
      }
    });

    it('does not mark inactive tabs, or any other variant, as active', () => {
      render(<Button variant="tab">Idle tab</Button>);
      expect(screen.getByRole('button', { name: 'Idle tab' })).not.toHaveAttribute(
        'data-pf-active'
      );

      render(
        <Button variant="subtle" active>
          Subtle
        </Button>
      );
      expect(screen.getByRole('button', { name: 'Subtle' })).not.toHaveAttribute(
        'data-pf-active'
      );
    });

    it.each(VARIANTS)('%s preserves caller paint classes verbatim', (variant) => {
      render(
        <Button
          variant={variant}
          className="bg-pf-accent hover:bg-pf-accent/90 text-pf-error border-pf-accent shadow-md"
        >
          Label
        </Button>
      );
      const button = screen.getByRole('button', { name: 'Label' });

      expect(button).toHaveClass(
        'bg-pf-accent',
        'hover:bg-pf-accent/90',
        'text-pf-error',
        'border-pf-accent',
        'shadow-md'
      );
    });
  });
});
