import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { Radio } from '../Radio';

describe('Radio', () => {
  it('should render radio button', () => {
    render(<Radio />);
    
    expect(screen.getByRole('radio')).toBeInTheDocument();
  });

  it('should render with label', () => {
    render(<Radio label="Option A" />);
    
    expect(screen.getByText('Option A')).toBeInTheDocument();
    expect(screen.getByRole('radio')).toBeInTheDocument();
  });

  it('should be checked when checked prop is true', () => {
    render(<Radio checked={true} onChange={() => {}} />);
    
    expect(screen.getByRole('radio')).toBeChecked();
  });

  it('should not be checked when checked prop is false', () => {
    render(<Radio checked={false} onChange={() => {}} />);
    
    expect(screen.getByRole('radio')).not.toBeChecked();
  });

  it('should call onChange when clicked', () => {
    const handleChange = vi.fn();
    render(<Radio onChange={handleChange} />);
    
    fireEvent.click(screen.getByRole('radio'));
    expect(handleChange).toHaveBeenCalled();
  });

  it('should be disabled when disabled prop is true', () => {
    render(<Radio disabled />);
    
    expect(screen.getByRole('radio')).toBeDisabled();
  });

  it('should generate ID from label', () => {
    render(<Radio label="My Option" />);
    
    expect(screen.getByRole('radio')).toHaveAttribute('id', 'radio-my-option');
  });

  it('should use provided id over generated id', () => {
    render(<Radio id="custom-radio" label="My Option" />);
    
    expect(screen.getByRole('radio')).toHaveAttribute('id', 'custom-radio');
  });

  it('should render without label', () => {
    render(<Radio name="test" value="value1" />);
    
    const radio = screen.getByRole('radio');
    expect(radio).toHaveAttribute('name', 'test');
    expect(radio).toHaveAttribute('value', 'value1');
  });

  it('should apply invalid styling when invalid prop is true', () => {
    render(<Radio invalid data-testid="invalid-radio" />);
    
    expect(screen.getByTestId('invalid-radio')).toBeInTheDocument();
  });

  it('should pass through additional props', () => {
    render(<Radio name="options" value="opt1" aria-describedby="help" />);
    
    const radio = screen.getByRole('radio');
    expect(radio).toHaveAttribute('name', 'options');
    expect(radio).toHaveAttribute('value', 'opt1');
    expect(radio).toHaveAttribute('aria-describedby', 'help');
  });

  it('should apply custom className', () => {
    render(<Radio className="custom-radio" />);
    
    expect(screen.getByRole('radio')).toHaveClass('custom-radio');
  });
});
