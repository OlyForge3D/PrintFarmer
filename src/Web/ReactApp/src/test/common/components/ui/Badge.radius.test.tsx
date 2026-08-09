import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { Badge } from '@/common/components/ui/Badge';
import { radiusClasses } from './radiusTestUtils';

/**
 * Radius contract for the shared Badge primitive.
 *
 * These assertions exist because the defect they pin was invisible to every
 * other gate. `clsx` concatenates rather than merges, so a caller-supplied
 * `rounded-full` and the component's default `rounded-xs` both landed on the
 * element; Tailwind emits `rounded-xs` last, so at equal specificity it won and
 * the caller's circle silently rendered as a 2px square. Lint excused the line
 * (its `h-8 w-8` proves a circle), types were fine, the build was fine, and
 * jsdom does not compute Tailwind CSS -- so nothing failed.
 *
 * jsdom cannot tell us which rule wins, but it can tell us that only one radius
 * class is present, which removes the ambiguity at the source.
 */

describe('Badge radius contract', () => {
  it('defaults a status badge to the 2px status-pill radius', () => {
    const { container } = render(<Badge>Idle</Badge>);
    expect(radiusClasses(container.firstElementChild!)).toEqual(['rounded-xs']);
  });

  it('renders a tag chip fully round and signs the waiver for it', () => {
    const { container } = render(<Badge shape="tag">resin</Badge>);
    const el = container.firstElementChild!;

    expect(radiusClasses(el)).toEqual(['rounded-full']);
    expect(el.getAttribute('data-pf-radius')).toBe('full');
  });

  it('does not sign a waiver for a status badge', () => {
    const { container } = render(<Badge>Idle</Badge>);
    expect(container.firstElementChild!.hasAttribute('data-pf-radius')).toBe(false);
  });

  it('yields to a caller-supplied radius instead of emitting two', () => {
    const { container } = render(<Badge className="h-8 w-8 rounded-full p-0">3</Badge>);
    expect(radiusClasses(container.firstElementChild!)).toEqual(['rounded-full']);
  });

  it('yields to a caller-supplied radius on a tag chip too', () => {
    const { container } = render(
      <Badge shape="tag" className="rounded-sm">
        squared tag
      </Badge>
    );
    expect(radiusClasses(container.firstElementChild!)).toEqual(['rounded-sm']);
  });

  it('keeps its default alongside a variant-prefixed caller radius', () => {
    // A conditional override must NOT suppress the default: `md:rounded-full`
    // binds only at >=md, so standing down would leave the badge with no radius
    // at all on phones. Emitting both is correct here -- Tailwind orders
    // variants after the base utilities they shadow, so the conditional still
    // wins where it applies. Contrast the unconditional cases above, where
    // emitting both is what produced the original defect.
    // `h-8 w-8` keeps the element an honest circle so the radius rule this PR
    // ships excuses the call site; `radiusClasses()` filters to radius classes,
    // so it does not affect the assertion. A test is not exempt from the design
    // contract it is testing.
    const { container } = render(<Badge className="h-8 w-8 md:rounded-full">responsive</Badge>);
    expect(radiusClasses(container.firstElementChild!)).toEqual(['rounded-xs', 'md:rounded-full']);
  });

  it('yields to an important or arbitrary-property caller radius', () => {
    const bang = render(<Badge className="!rounded-md">important</Badge>);
    expect(radiusClasses(bang.container.firstElementChild!)).toEqual(['!rounded-md']);

    // Arbitrary-property syntax sets border-radius without ever spelling
    // "rounded", so it has to be recognised explicitly or it silently loses to
    // the default. This is about Badge's class merging, not lint:
    // `pf-no-oversized-radius` reads the utility scale and does not judge
    // arbitrary properties at all, so an oversized one here would pass lint and
    // still render wrong. That gap is tracked in #1079.
    const arbitrary = render(<Badge className="[border-radius:8px]">arbitrary</Badge>);
    expect(radiusClasses(arbitrary.container.firstElementChild!)).toEqual([]);
    expect(arbitrary.container.firstElementChild!.className).toContain('[border-radius:8px]');
  });

  it('keeps its default when the caller passes unrelated classes', () => {
    const { container } = render(<Badge className="uppercase tracking-wide">Idle</Badge>);
    expect(radiusClasses(container.firstElementChild!)).toEqual(['rounded-xs']);
  });

  it('applies the same single-radius rule to the dot variant', () => {
    const { container } = render(<Badge dot />);
    expect(radiusClasses(container.firstElementChild!)).toEqual(['rounded-full']);

    const overridden = render(<Badge dot className="rounded-xs" />);
    expect(radiusClasses(overridden.container.firstElementChild!)).toEqual(['rounded-xs']);
  });
});
