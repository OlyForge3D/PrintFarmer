import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { SlicerModeToggle } from '../SlicerModeToggle';

describe('SlicerModeToggle', () => {
  it.each([
    { levels: ['Basic', 'Advanced'] as const, dots: 2 },
    { levels: ['Simple', 'Basic', 'Advanced'] as const, dots: 3 },
  ])('draws one decorative dot per level ($dots) outside the accessible tree', ({ levels, dots }) => {
    render(<SlicerModeToggle levels={levels} value={levels[0]} onChange={vi.fn()} />);

    const button = screen.getByRole('button', { name: `Settings view: ${levels[0]}` });
    const track = button.querySelector('[aria-hidden="true"]');
    expect(track).not.toBeNull();
    expect(track!.querySelectorAll('.rounded-full.h-1.w-1')).toHaveLength(dots);
    expect(button).toHaveTextContent('');
  });

  const pair = ['Basic', 'Advanced'] as const;
  const trio = ['Simple', 'Basic', 'Advanced'] as const;
  it.each([
    { levels: pair, value: 'Basic', offset: '0rem', next: 'Advanced' },
    { levels: pair, value: 'Advanced', offset: '0.75rem', next: 'Basic' },
    { levels: trio, value: 'Simple', offset: '0rem', next: 'Basic' },
    { levels: trio, value: 'Basic', offset: '0.75rem', next: 'Advanced' },
    { levels: trio, value: 'Advanced', offset: '1.5rem', next: 'Simple' },
  ] as const)('places $value at $offset and cycles to $next ($levels)', ({ levels, value, offset, next }) => {
    const onChange = vi.fn();
    render(<SlicerModeToggle levels={levels} value={value} onChange={onChange} />);

    const button = screen.getByRole('button', { name: `Settings view: ${value}` });
    const knob = button.querySelector<HTMLElement>('.shadow-sm');
    expect(knob?.style.transform).toBe(`translateX(${offset})`);

    fireEvent.click(button);
    expect(onChange).toHaveBeenCalledWith(next);
  });

  it('renders nothing when there is only one level to choose from', () => {
    const { container } = render(<SlicerModeToggle levels={['Basic']} value="Basic" onChange={vi.fn()} />);
    expect(container).toBeEmptyDOMElement();
  });
});
