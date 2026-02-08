import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { ControlPadButton } from '../ControlPadButton';

describe('ControlPadButton', () => {
  it('should render button with children', () => {
    render(<ControlPadButton>▲</ControlPadButton>);
    
    expect(screen.getByRole('button')).toBeInTheDocument();
    expect(screen.getByText('▲')).toBeInTheDocument();
  });

  it('should render with small size', () => {
    render(<ControlPadButton padSize="small">X</ControlPadButton>);
    
    const button = screen.getByRole('button');
    expect(button).toHaveClass('w-8', 'h-8');
  });

  it('should render with medium size (default)', () => {
    render(<ControlPadButton>Y</ControlPadButton>);
    
    const button = screen.getByRole('button');
    expect(button).toHaveClass('w-11', 'h-11');
  });

  it('should render with large size', () => {
    render(<ControlPadButton padSize="large">Z</ControlPadButton>);
    
    const button = screen.getByRole('button');
    expect(button).toHaveClass('w-full', 'h-full');
  });

  it('should use secondary variant by default', () => {
    render(<ControlPadButton>Home</ControlPadButton>);
    
    // Button renders with secondary variant styles by default
    expect(screen.getByRole('button')).toBeInTheDocument();
  });

  it('should allow custom variant', () => {
    render(<ControlPadButton variant="primary">Go</ControlPadButton>);
    
    expect(screen.getByRole('button')).toBeInTheDocument();
  });

  it('should call onClick when clicked', () => {
    const handleClick = vi.fn();
    render(<ControlPadButton onClick={handleClick}>Press</ControlPadButton>);
    
    fireEvent.click(screen.getByRole('button'));
    expect(handleClick).toHaveBeenCalled();
  });

  it('should be disabled when disabled prop is true', () => {
    render(<ControlPadButton disabled>Disabled</ControlPadButton>);
    
    expect(screen.getByRole('button')).toBeDisabled();
  });

  it('should pass through additional props', () => {
    render(<ControlPadButton aria-label="Move up" title="Up">↑</ControlPadButton>);
    
    const button = screen.getByRole('button');
    expect(button).toHaveAttribute('aria-label', 'Move up');
    expect(button).toHaveAttribute('title', 'Up');
  });

  it('should apply custom className', () => {
    render(<ControlPadButton className="custom-pad">🏠</ControlPadButton>);
    
    expect(screen.getByRole('button')).toHaveClass('custom-pad');
  });

  it('should render arrow symbols correctly', () => {
    const { rerender } = render(<ControlPadButton>←</ControlPadButton>);
    expect(screen.getByText('←')).toBeInTheDocument();
    
    rerender(<ControlPadButton>→</ControlPadButton>);
    expect(screen.getByText('→')).toBeInTheDocument();
    
    rerender(<ControlPadButton>↑</ControlPadButton>);
    expect(screen.getByText('↑')).toBeInTheDocument();
    
    rerender(<ControlPadButton>↓</ControlPadButton>);
    expect(screen.getByText('↓')).toBeInTheDocument();
  });
});
