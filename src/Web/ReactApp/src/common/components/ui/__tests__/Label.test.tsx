import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { Label } from '../Label';

describe('Label', () => {
  it('should render label with children', () => {
    render(<Label>Email Address</Label>);
    
    expect(screen.getByText('Email Address')).toBeInTheDocument();
  });

  it('should render as a label element', () => {
    render(<Label>Username</Label>);
    
    const label = screen.getByText('Username');
    expect(label.tagName).toBe('LABEL');
  });

  it('should show asterisk when required is true', () => {
    render(<Label required>Required Field</Label>);
    
    expect(screen.getByText('*')).toBeInTheDocument();
  });

  it('should not show asterisk when required is false', () => {
    render(<Label required={false}>Optional Field</Label>);
    
    expect(screen.queryByText('*')).not.toBeInTheDocument();
  });

  it('should apply custom className', () => {
    render(<Label className="custom-class">Custom Label</Label>);
    
    const label = screen.getByText('Custom Label');
    expect(label).toHaveClass('custom-class');
  });

  it('should pass htmlFor attribute', () => {
    render(<Label htmlFor="email-input">Email</Label>);
    
    const label = screen.getByText('Email');
    expect(label).toHaveAttribute('for', 'email-input');
  });

  it('should pass through additional HTML attributes', () => {
    render(<Label data-testid="test-label" id="my-label">Test</Label>);
    
    expect(screen.getByTestId('test-label')).toBeInTheDocument();
    expect(screen.getByTestId('test-label')).toHaveAttribute('id', 'my-label');
  });
});
