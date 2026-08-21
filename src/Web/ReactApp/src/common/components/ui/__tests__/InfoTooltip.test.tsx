import { act, render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { InfoTooltip } from '../InfoTooltip';

describe('InfoTooltip', () => {
  it('renders a trigger button and keeps the tooltip content in the document but hidden', () => {
    render(<InfoTooltip content="Helpful guidance text" />);

    const button = screen.getByRole('button', { name: 'More information' });
    expect(button).toBeInTheDocument();

    const tooltip = screen.getByRole('tooltip', { hidden: true });
    expect(tooltip).toHaveTextContent('Helpful guidance text');
    expect(tooltip.className).toContain('hidden');
    expect(button).toHaveAttribute('aria-expanded', 'false');
  });

  it('wires the trigger to the tooltip content via aria-describedby', () => {
    render(<InfoTooltip content="Helpful guidance text" />);

    const button = screen.getByRole('button', { name: 'More information' });
    const describedBy = button.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();

    const tooltip = screen.getByRole('tooltip', { hidden: true });
    expect(tooltip).toHaveAttribute('id', describedBy);
  });

  it('shows the tooltip on keyboard focus (keyboard-operable)', () => {
    render(<InfoTooltip content="Helpful guidance text" />);

    const button = screen.getByRole('button', { name: 'More information' });
    act(() => {
      button.focus();
    });

    const tooltip = screen.getByRole('tooltip');
    expect(tooltip.className).not.toContain('hidden');
    expect(button).toHaveAttribute('aria-expanded', 'true');
  });

  it('hides the tooltip on blur', () => {
    render(<InfoTooltip content="Helpful guidance text" />);

    const button = screen.getByRole('button', { name: 'More information' });
    act(() => {
      button.focus();
    });
    expect(screen.getByRole('tooltip').className).not.toContain('hidden');

    act(() => {
      button.blur();
    });
    expect(screen.getByRole('tooltip', { hidden: true }).className).toContain('hidden');
  });

  it('dismisses on Escape and returns focus to the trigger button', () => {
    render(<InfoTooltip content="Helpful guidance text" />);

    const button = screen.getByRole('button', { name: 'More information' });
    act(() => {
      button.focus();
    });
    expect(screen.getByRole('tooltip').className).not.toContain('hidden');

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(screen.getByRole('tooltip', { hidden: true }).className).toContain('hidden');
    expect(document.activeElement).toBe(button);
  });

  it('shows the tooltip on mouse hover and hides on mouse leave', () => {
    render(<InfoTooltip content="Helpful guidance text" />);

    const button = screen.getByRole('button', { name: 'More information' });
    const wrapper = button.parentElement!;

    fireEvent.mouseEnter(wrapper);
    expect(screen.getByRole('tooltip').className).not.toContain('hidden');

    fireEvent.mouseLeave(wrapper);
    expect(screen.getByRole('tooltip', { hidden: true }).className).toContain('hidden');
  });

  it('supports a custom accessible label', () => {
    render(<InfoTooltip content="Helpful guidance text" label="More information about engine version" />);

    expect(screen.getByRole('button', { name: 'More information about engine version' })).toBeInTheDocument();
  });
});
