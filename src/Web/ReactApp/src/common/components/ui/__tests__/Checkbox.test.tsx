import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { Checkbox } from '../Checkbox';

describe('Checkbox', () => {
  it('should render checkbox', () => {
    render(<Checkbox />);
    
    expect(screen.getByRole('checkbox')).toBeInTheDocument();
  });

  it('should render with label', () => {
    render(<Checkbox label="Accept terms" />);
    
    expect(screen.getByText('Accept terms')).toBeInTheDocument();
    expect(screen.getByRole('checkbox')).toBeInTheDocument();
  });

  it('should be checked when checked prop is true', () => {
    render(<Checkbox checked={true} onChange={() => {}} />);
    
    expect(screen.getByRole('checkbox')).toBeChecked();
  });

  it('should not be checked when checked prop is false', () => {
    render(<Checkbox checked={false} onChange={() => {}} />);
    
    expect(screen.getByRole('checkbox')).not.toBeChecked();
  });

  it('should call onChange when clicked', () => {
    const handleChange = vi.fn();
    render(<Checkbox onChange={handleChange} />);
    
    fireEvent.click(screen.getByRole('checkbox'));
    expect(handleChange).toHaveBeenCalled();
  });

  it('should be disabled when disabled prop is true', () => {
    render(<Checkbox disabled />);
    
    expect(screen.getByRole('checkbox')).toBeDisabled();
  });

  it('should generate ID from label', () => {
    render(<Checkbox label="My Checkbox" />);
    
    expect(screen.getByRole('checkbox')).toHaveAttribute('id', 'checkbox-my-checkbox');
  });

  it('should use provided id over generated id', () => {
    render(<Checkbox id="custom-cb" label="My Checkbox" />);
    
    expect(screen.getByRole('checkbox')).toHaveAttribute('id', 'custom-cb');
  });

  it('should render without label', () => {
    render(<Checkbox name="agreement" value="yes" />);
    
    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).toHaveAttribute('name', 'agreement');
    expect(checkbox).toHaveAttribute('value', 'yes');
  });

  it('should apply invalid styling when invalid prop is true', () => {
    render(<Checkbox invalid data-testid="invalid-checkbox" />);
    
    expect(screen.getByTestId('invalid-checkbox')).toBeInTheDocument();
  });

  it('should pass through additional props', () => {
    render(<Checkbox name="terms" aria-describedby="help" />);
    
    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).toHaveAttribute('name', 'terms');
    expect(checkbox).toHaveAttribute('aria-describedby', 'help');
  });

  it('should apply custom className', () => {
    render(<Checkbox className="custom-checkbox" />);
    
    expect(screen.getByRole('checkbox')).toHaveClass('custom-checkbox');
  });

  it('should toggle checked state on click', () => {
    const handleChange = vi.fn();
    const { rerender } = render(<Checkbox checked={false} onChange={handleChange} />);
    
    fireEvent.click(screen.getByRole('checkbox'));
    expect(handleChange).toHaveBeenCalled();
    
    rerender(<Checkbox checked={true} onChange={handleChange} />);
    expect(screen.getByRole('checkbox')).toBeChecked();
  });
});
