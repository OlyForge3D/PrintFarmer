import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { Slider } from '../Slider';

describe('Slider', () => {
  it('should render slider with correct value', () => {
    render(<Slider value={50} onChange={() => {}} />);
    
    const slider = screen.getByRole('slider');
    expect(slider).toHaveValue('50');
  });

  it('should render with default min/max/step', () => {
    render(<Slider value={25} onChange={() => {}} />);
    
    const slider = screen.getByRole('slider');
    expect(slider).toHaveAttribute('min', '0');
    expect(slider).toHaveAttribute('max', '100');
    expect(slider).toHaveAttribute('step', '1');
  });

  it('should render with custom min/max/step', () => {
    render(<Slider value={5} onChange={() => {}} min={0} max={10} step={0.5} />);
    
    const slider = screen.getByRole('slider');
    expect(slider).toHaveAttribute('min', '0');
    expect(slider).toHaveAttribute('max', '10');
    expect(slider).toHaveAttribute('step', '0.5');
  });

  it('should call onChange when value changes', () => {
    const handleChange = vi.fn();
    render(<Slider value={50} onChange={handleChange} />);
    
    const slider = screen.getByRole('slider');
    fireEvent.change(slider, { target: { value: '75' } });
    
    expect(handleChange).toHaveBeenCalledWith(75);
  });

  it('should be disabled when disabled prop is true', () => {
    render(<Slider value={50} onChange={() => {}} disabled />);
    
    expect(screen.getByRole('slider')).toBeDisabled();
  });

  it('should apply aria-label', () => {
    render(<Slider value={50} onChange={() => {}} aria-label="Volume" />);
    
    expect(screen.getByRole('slider')).toHaveAttribute('aria-label', 'Volume');
  });

  it('should apply aria-labelledby', () => {
    render(<Slider value={50} onChange={() => {}} aria-labelledby="label-id" />);
    
    expect(screen.getByRole('slider')).toHaveAttribute('aria-labelledby', 'label-id');
  });

  it('should apply custom className', () => {
    render(<Slider value={50} onChange={() => {}} className="custom-slider" />);
    
    const slider = screen.getByRole('slider');
    expect(slider).toHaveClass('custom-slider');
  });

  it('should handle decimal step values', () => {
    const handleChange = vi.fn();
    render(<Slider value={0.5} onChange={handleChange} min={0} max={1} step={0.1} />);
    
    const slider = screen.getByRole('slider');
    fireEvent.change(slider, { target: { value: '0.7' } });
    
    expect(handleChange).toHaveBeenCalledWith(0.7);
  });

  it('should handle negative values', () => {
    render(<Slider value={-5} onChange={() => {}} min={-10} max={10} />);
    
    const slider = screen.getByRole('slider');
    expect(slider).toHaveValue('-5');
    expect(slider).toHaveAttribute('min', '-10');
    expect(slider).toHaveAttribute('max', '10');
  });

  it('should render as range input type', () => {
    render(<Slider value={50} onChange={() => {}} />);
    
    const slider = screen.getByRole('slider');
    expect(slider).toHaveAttribute('type', 'range');
  });
});
