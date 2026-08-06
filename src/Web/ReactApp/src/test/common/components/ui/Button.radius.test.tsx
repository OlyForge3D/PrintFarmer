import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Button } from '@/common/components/ui/Button';

const radiusClasses = (element: Element): string[] =>
  Array.from(element.classList).filter((name) => /^(?:\S+:)*!?rounded(?:-|$)/.test(name));

describe('Button radius contract', () => {
  it('defaults to exactly one 2px radius class', () => {
    render(<Button>Default</Button>);
    expect(radiusClasses(screen.getByRole('button', { name: 'Default' }))).toEqual([
      'rounded-xs',
    ]);
  });

  it('yields to an unconditional caller radius instead of emitting two', () => {
    render(<Button className="rounded-lg">Round</Button>);
    expect(radiusClasses(screen.getByRole('button', { name: 'Round' }))).toEqual([
      'rounded-lg',
    ]);
  });

  it('keeps the default alongside a conditional caller radius', () => {
    render(<Button className="md:rounded-lg">Responsive</Button>);
    expect(radiusClasses(screen.getByRole('button', { name: 'Responsive' }))).toEqual([
      'rounded-xs',
      'md:rounded-lg',
    ]);
  });

  it('keeps the tab variant square unless the caller overrides it', () => {
    const { rerender } = render(<Button variant="tab">Tab</Button>);
    expect(radiusClasses(screen.getByRole('button', { name: 'Tab' }))).toEqual([
      'rounded-none',
    ]);

    rerender(
      <Button variant="tab" className="rounded-md">
        Tab
      </Button>
    );
    expect(radiusClasses(screen.getByRole('button', { name: 'Tab' }))).toEqual([
      'rounded-md',
    ]);
  });

  it('preserves the unstyled contract: no default radius and caller-only styling', () => {
    const { rerender } = render(<Button variant="unstyled">Unstyled</Button>);
    expect(radiusClasses(screen.getByRole('button', { name: 'Unstyled' }))).toEqual([]);

    rerender(
      <Button variant="unstyled" className="rounded-sm">
        Unstyled
      </Button>
    );
    expect(radiusClasses(screen.getByRole('button', { name: 'Unstyled' }))).toEqual([
      'rounded-sm',
    ]);
  });
});
