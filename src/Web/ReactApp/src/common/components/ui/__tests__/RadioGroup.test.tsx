import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { RadioGroup } from '../RadioGroup';

const mockOptions = [
  { value: 'option1', label: 'Option 1' },
  { value: 'option2', label: 'Option 2' },
  { value: 'option3', label: 'Option 3' },
];

describe('RadioGroup', () => {
  it('should render all options', () => {
    render(<RadioGroup name="test" options={mockOptions} />);
    
    expect(screen.getByText('Option 1')).toBeInTheDocument();
    expect(screen.getByText('Option 2')).toBeInTheDocument();
    expect(screen.getByText('Option 3')).toBeInTheDocument();
  });

  it('should render with role radiogroup', () => {
    render(<RadioGroup name="test" options={mockOptions} />);
    
    expect(screen.getByRole('radiogroup')).toBeInTheDocument();
  });

  it('should render radio inputs for each option', () => {
    render(<RadioGroup name="test" options={mockOptions} />);
    
    const radios = screen.getAllByRole('radio');
    expect(radios).toHaveLength(3);
  });

  it('should check the correct option when value is provided', () => {
    render(<RadioGroup name="test" options={mockOptions} value="option2" />);
    
    const radios = screen.getAllByRole('radio');
    expect(radios[0]).not.toBeChecked();
    expect(radios[1]).toBeChecked();
    expect(radios[2]).not.toBeChecked();
  });

  it('should call onChange when option is clicked', () => {
    const handleChange = vi.fn();
    render(<RadioGroup name="test" options={mockOptions} onChange={handleChange} />);
    
    fireEvent.click(screen.getByText('Option 2'));
    expect(handleChange).toHaveBeenCalledWith('option2');
  });

  it('should disable all options when disabled is true', () => {
    render(<RadioGroup name="test" options={mockOptions} disabled />);
    
    const radios = screen.getAllByRole('radio');
    radios.forEach(radio => {
      expect(radio).toBeDisabled();
    });
  });

  it('should disable individual option when option.disabled is true', () => {
    const optionsWithDisabled = [
      { value: 'option1', label: 'Option 1' },
      { value: 'option2', label: 'Option 2', disabled: true },
      { value: 'option3', label: 'Option 3' },
    ];
    render(<RadioGroup name="test" options={optionsWithDisabled} />);
    
    const radios = screen.getAllByRole('radio');
    expect(radios[0]).not.toBeDisabled();
    expect(radios[1]).toBeDisabled();
    expect(radios[2]).not.toBeDisabled();
  });

  it('should render vertical layout by default', () => {
    render(<RadioGroup name="test" options={mockOptions} />);
    
    const group = screen.getByRole('radiogroup');
    expect(group).toHaveClass('flex-col');
  });

  it('should render horizontal layout when direction is horizontal', () => {
    render(<RadioGroup name="test" options={mockOptions} direction="horizontal" />);
    
    const group = screen.getByRole('radiogroup');
    expect(group).toHaveClass('flex-row');
  });

  it('should apply custom className', () => {
    render(<RadioGroup name="test" options={mockOptions} className="custom-class" />);
    
    const group = screen.getByRole('radiogroup');
    expect(group).toHaveClass('custom-class');
  });

  it('should set name attribute on all radio inputs', () => {
    render(<RadioGroup name="favorite-color" options={mockOptions} />);
    
    const radios = screen.getAllByRole('radio');
    radios.forEach(radio => {
      expect(radio).toHaveAttribute('name', 'favorite-color');
    });
  });

  it('should set value attribute on radio inputs', () => {
    render(<RadioGroup name="test" options={mockOptions} />);
    
    const radios = screen.getAllByRole('radio');
    expect(radios[0]).toHaveAttribute('value', 'option1');
    expect(radios[1]).toHaveAttribute('value', 'option2');
    expect(radios[2]).toHaveAttribute('value', 'option3');
  });

  it('should handle empty options array', () => {
    render(<RadioGroup name="test" options={[]} />);
    
    const group = screen.getByRole('radiogroup');
    expect(group).toBeInTheDocument();
    expect(screen.queryAllByRole('radio')).toHaveLength(0);
  });

  it('should handle onChange not provided', () => {
    render(<RadioGroup name="test" options={mockOptions} />);
    
    // Should not throw when clicking without onChange handler
    expect(() => fireEvent.click(screen.getByText('Option 1'))).not.toThrow();
  });
});
