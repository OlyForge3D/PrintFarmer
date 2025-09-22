import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render } from '@testing-library/react';
import { screen, fireEvent } from '@testing-library/dom'; // eslint-disable-line import/no-unresolved
import { ColorFamilySelect } from '@/components/ColorFamilySelect';

// Basic options sample
const options = ['Red','Green','Blue'];

describe('ColorFamilySelect', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it('renders placeholder when no value selected', () => {
    render(<ColorFamilySelect value="" onChange={() => {}} options={options} placeholder="All Colors" />);
    expect(screen.getByRole('button', { name: /All Colors/i })).toBeTruthy();
  });

  it('opens list and selects a value', () => {
    const onChange = vi.fn();
    render(<ColorFamilySelect value="" onChange={onChange} options={options} placeholder="All Colors" />);
    const btn = screen.getByRole('button');
    fireEvent.click(btn);
    const opt = screen.getByText('Green');
    fireEvent.click(opt);
    expect(onChange).toHaveBeenCalledWith('Green');
  });

  it('keyboard navigation selects option with Enter', () => {
    const onChange = vi.fn();
    render(<ColorFamilySelect value="" onChange={onChange} options={options} placeholder="All Colors" />);
    const btn = screen.getByRole('button');
    fireEvent.keyDown(btn, { key: 'ArrowDown' }); // open + focus first (All)
    fireEvent.keyDown(btn, { key: 'ArrowDown' }); // move to Red
    fireEvent.keyDown(btn, { key: 'ArrowDown' }); // move to Green
    fireEvent.keyDown(btn, { key: 'Enter' });
    expect(onChange).toHaveBeenCalledWith('Green');
  });

  it('shows active styling while navigating', () => {
    render(<ColorFamilySelect value="" onChange={() => {}} options={options} placeholder="All Colors" id="test-color" />);
    const btn = screen.getByRole('button');
    fireEvent.keyDown(btn, { key: 'ArrowDown' }); // open
    // Active should be All (index 0)
    let active = screen.getByRole('option', { name: /All Colors/i });
    expect(active.getAttribute('data-active')).toBe('true');
    fireEvent.keyDown(btn, { key: 'ArrowDown' }); // move to Red
    active = screen.getByText('Red').parentElement as HTMLElement;
    expect(active.getAttribute('data-active')).toBe('true');
  });
});
