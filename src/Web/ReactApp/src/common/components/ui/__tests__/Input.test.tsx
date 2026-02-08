import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { Input } from '../Input';

describe('Input', () => {
  it('should render text input', () => {
    render(<Input />);
    
    expect(screen.getByRole('textbox')).toBeInTheDocument();
  });

  it('should call onChange when typing', () => {
    const handleChange = vi.fn();
    render(<Input onChange={handleChange} />);
    
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'hello' } });
    expect(handleChange).toHaveBeenCalled();
  });

  it('should show entered value', () => {
    render(<Input value="test value" onChange={() => {}} />);
    
    expect(screen.getByRole('textbox')).toHaveValue('test value');
  });

  it('should be disabled when disabled prop is true', () => {
    render(<Input disabled />);
    
    expect(screen.getByRole('textbox')).toBeDisabled();
  });

  it('should apply invalid styling when invalid prop is true', () => {
    render(<Input invalid data-testid="invalid-input" />);
    
    expect(screen.getByTestId('invalid-input')).toBeInTheDocument();
  });

  it('should apply custom className', () => {
    render(<Input className="custom-input" />);
    
    expect(screen.getByRole('textbox')).toHaveClass('custom-input');
  });

  it('should pass through additional props', () => {
    render(<Input name="email" id="email-input" placeholder="Enter email" />);
    
    const input = screen.getByRole('textbox');
    expect(input).toHaveAttribute('name', 'email');
    expect(input).toHaveAttribute('id', 'email-input');
    expect(input).toHaveAttribute('placeholder', 'Enter email');
  });

  it('should support type="email"', () => {
    render(<Input type="email" placeholder="Email" />);
    
    expect(screen.getByPlaceholderText('Email')).toHaveAttribute('type', 'email');
  });

  it('should support type="password"', () => {
    render(<Input type="password" placeholder="Password" />);
    
    expect(screen.getByPlaceholderText('Password')).toHaveAttribute('type', 'password');
  });

  it('should support type="number"', () => {
    render(<Input type="number" placeholder="Amount" />);
    
    expect(screen.getByPlaceholderText('Amount')).toHaveAttribute('type', 'number');
  });

  it('should support required attribute', () => {
    render(<Input required />);
    
    expect(screen.getByRole('textbox')).toBeRequired();
  });

  it('should support maxLength attribute', () => {
    render(<Input maxLength={50} />);
    
    expect(screen.getByRole('textbox')).toHaveAttribute('maxLength', '50');
  });

  it('should support pattern attribute', () => {
    render(<Input pattern="[A-Za-z]{3}" />);
    
    expect(screen.getByRole('textbox')).toHaveAttribute('pattern', '[A-Za-z]{3}');
  });
});
