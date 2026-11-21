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

  it('moves the knob to the selected level and cycles to the next level on click', () => {
    const onChange = vi.fn();
    const levels = ['Simple', 'Basic', 'Advanced'] as const;
    render(<SlicerModeToggle levels={levels} value="Basic" onChange={onChange} />);

    const button = screen.getByRole('button', { name: 'Settings view: Basic' });
    const knob = button.querySelector<HTMLElement>('.shadow-sm');
    expect(knob?.style.transform).toBe('translateX(0.75rem)');

    fireEvent.click(button);
    expect(onChange).toHaveBeenCalledWith('Advanced');
  });

  it('renders nothing when there is only one level to choose from', () => {
    const { container } = render(<SlicerModeToggle levels={['Basic']} value="Basic" onChange={vi.fn()} />);
    expect(container).toBeEmptyDOMElement();
  });
});
