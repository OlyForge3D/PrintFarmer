import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import { Card } from '@/common/components/ui/Card';

const radiusClasses = (element: Element): string[] =>
  Array.from(element.classList).filter((name) => /^(?:\S+:)*!?rounded(?:-|$)/.test(name));

describe('Card radius contract', () => {
  it('defaults to exactly one 8px radius class', () => {
    const { container } = render(<Card>Default</Card>);
    expect(radiusClasses(container.firstElementChild!)).toEqual(['rounded-lg']);
  });

  it('yields to an unconditional caller radius instead of emitting two', () => {
    const { container } = render(<Card className="rounded-sm">Compact</Card>);
    expect(radiusClasses(container.firstElementChild!)).toEqual(['rounded-sm']);
  });

  it('keeps the default alongside a conditional caller radius', () => {
    const { container } = render(<Card className="md:rounded-sm">Responsive</Card>);
    expect(radiusClasses(container.firstElementChild!)).toEqual([
      'rounded-lg',
      'md:rounded-sm',
    ]);
  });

  it('yields to an important or arbitrary-property caller radius', () => {
    const important = render(<Card className="!rounded-md">Important</Card>);
    expect(radiusClasses(important.container.firstElementChild!)).toEqual(['!rounded-md']);

    const arbitrary = render(<Card className="[border-radius:6px]">Arbitrary</Card>);
    expect(radiusClasses(arbitrary.container.firstElementChild!)).toEqual([]);
    expect(arbitrary.container.firstElementChild).toHaveClass('[border-radius:6px]');
  });
});
