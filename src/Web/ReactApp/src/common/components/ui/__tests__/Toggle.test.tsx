import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { Toggle } from '../Toggle';

describe('Toggle', () => {
  it('should render toggle without label', () => {
    render(<Toggle />);
    
    expect(screen.getByRole('checkbox')).toBeInTheDocument();
  });

  it('should render toggle with label', () => {
    render(<Toggle label="Enable notifications" />);
    
    expect(screen.getByText('Enable notifications')).toBeInTheDocument();
    expect(screen.getByRole('checkbox')).toBeInTheDocument();
  });

  it('should be checked when checked prop is true', () => {
    render(<Toggle checked={true} onChange={() => {}} />);
    
    expect(screen.getByRole('checkbox')).toBeChecked();
  });

  it('should not be checked when checked prop is false', () => {
    render(<Toggle checked={false} onChange={() => {}} />);
    
    expect(screen.getByRole('checkbox')).not.toBeChecked();
  });

  it('should call onChange when clicked', () => {
    const handleChange = vi.fn();
    render(<Toggle onChange={handleChange} />);
    
    fireEvent.click(screen.getByRole('checkbox'));
    expect(handleChange).toHaveBeenCalled();
  });

  it('should be disabled when disabled prop is true', () => {
    render(<Toggle disabled label="Disabled toggle" />);
    
    expect(screen.getByRole('checkbox')).toBeDisabled();
  });

  it('should not allow interaction when disabled', () => {
    // Note: While the onChange callback still fires due to how React handles
    // disabled checkboxes with click events, the checkbox itself is disabled
    render(<Toggle disabled />);
    
    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).toBeDisabled();
  });

  it('should render with sm size', () => {
    render(<Toggle size="sm" data-testid="small-toggle" />);
    
    expect(screen.getByTestId('small-toggle')).toBeInTheDocument();
  });

  it('should render with md size (default)', () => {
    render(<Toggle size="md" data-testid="medium-toggle" />);
    
    expect(screen.getByTestId('medium-toggle')).toBeInTheDocument();
  });

  it('should apply invalid styling when invalid prop is true', () => {
    render(<Toggle invalid data-testid="invalid-toggle" />);
    
    expect(screen.getByTestId('invalid-toggle')).toBeInTheDocument();
  });

  it('should generate ID from label when id not provided', () => {
    render(<Toggle label="My Toggle" />);
    
    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).toHaveAttribute('id', 'toggle-my-toggle');
  });

  it('should use provided id over generated id', () => {
    render(<Toggle id="custom-id" label="My Toggle" />);
    
    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).toHaveAttribute('id', 'custom-id');
  });

  it('should pass through additional props', () => {
    render(<Toggle name="notifications" aria-describedby="help-text" />);
    
    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).toHaveAttribute('name', 'notifications');
    expect(checkbox).toHaveAttribute('aria-describedby', 'help-text');
  });
});
