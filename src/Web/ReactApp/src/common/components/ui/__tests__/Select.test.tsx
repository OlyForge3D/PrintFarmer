import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { Select } from '../Select';

describe('Select', () => {
  it('should render select with options', () => {
    render(
      <Select>
        <option value="a">Option A</option>
        <option value="b">Option B</option>
      </Select>
    );
    
    expect(screen.getByRole('combobox')).toBeInTheDocument();
    expect(screen.getByText('Option A')).toBeInTheDocument();
    expect(screen.getByText('Option B')).toBeInTheDocument();
  });

  it('should call onChange when selection changes', () => {
    const handleChange = vi.fn();
    render(
      <Select onChange={handleChange}>
        <option value="a">A</option>
        <option value="b">B</option>
      </Select>
    );
    
    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'b' } });
    expect(handleChange).toHaveBeenCalled();
  });

  it('should be disabled when disabled prop is true', () => {
    render(
      <Select disabled>
        <option value="a">A</option>
      </Select>
    );
    
    expect(screen.getByRole('combobox')).toBeDisabled();
  });

  it('should apply invalid styling when invalid prop is true', () => {
    render(
      <Select invalid data-testid="invalid-select">
        <option value="a">A</option>
      </Select>
    );
    
    expect(screen.getByTestId('invalid-select')).toBeInTheDocument();
  });

  it('should apply custom className', () => {
    render(
      <Select className="custom-select">
        <option value="a">A</option>
      </Select>
    );
    
    expect(screen.getByRole('combobox')).toHaveClass('custom-select');
  });

  it('should pass through additional props', () => {
    render(
      <Select name="country" id="country-select" aria-label="Select country">
        <option value="us">USA</option>
        <option value="uk">UK</option>
      </Select>
    );
    
    const select = screen.getByRole('combobox');
    expect(select).toHaveAttribute('name', 'country');
    expect(select).toHaveAttribute('id', 'country-select');
    expect(select).toHaveAttribute('aria-label', 'Select country');
  });

  it('should show selected value', () => {
    render(
      <Select value="b" onChange={() => {}}>
        <option value="a">A</option>
        <option value="b">B</option>
      </Select>
    );
    
    expect(screen.getByRole('combobox')).toHaveValue('b');
  });

  it('should support required attribute', () => {
    render(
      <Select required>
        <option value="">Select...</option>
        <option value="a">A</option>
      </Select>
    );
    
    expect(screen.getByRole('combobox')).toBeRequired();
  });

  it('should render with optgroup', () => {
    render(
      <Select>
        <optgroup label="Group 1">
          <option value="a">A</option>
        </optgroup>
        <optgroup label="Group 2">
          <option value="b">B</option>
        </optgroup>
      </Select>
    );
    
    expect(screen.getByRole('combobox')).toBeInTheDocument();
    expect(screen.getByText('A')).toBeInTheDocument();
    expect(screen.getByText('B')).toBeInTheDocument();
  });

  it('should render a chevron dropdown icon', () => {
    const { container } = render(
      <Select>
        <option value="a">A</option>
      </Select>
    );

    const svg = container.querySelector('svg');
    expect(svg).toBeInTheDocument();
    expect(svg?.getAttribute('class')).toContain('pointer-events-none');
  });

  it('should style chevron with tertiary color by default', () => {
    const { container } = render(
      <Select>
        <option value="a">A</option>
      </Select>
    );

    const svg = container.querySelector('svg');
    expect(svg?.getAttribute('class')).toContain('text-pf-text-tertiary');
    expect(svg?.getAttribute('class')).not.toContain('text-pf-error');
  });

  it('should style chevron with error color when invalid', () => {
    const { container } = render(
      <Select invalid>
        <option value="a">A</option>
      </Select>
    );

    const svg = container.querySelector('svg');
    expect(svg?.getAttribute('class')).toContain('text-pf-error');
  });

  it('should apply opacity to chevron when disabled', () => {
    const { container } = render(
      <Select disabled>
        <option value="a">A</option>
      </Select>
    );

    const svg = container.querySelector('svg');
    expect(svg?.getAttribute('class')).toContain('opacity-50');
  });

  it('should apply containerClassName to wrapper div', () => {
    const { container } = render(
      <Select containerClassName="max-w-xs">
        <option value="a">A</option>
      </Select>
    );

    expect(container.firstElementChild?.className).toContain('max-w-xs');
  });
});
