import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { ProgressBar } from '../ProgressBar';

describe('ProgressBar', () => {
  it('should render progressbar with correct value', () => {
    render(<ProgressBar value={50} />);
    
    const progressbar = screen.getByRole('progressbar');
    expect(progressbar).toHaveAttribute('aria-valuenow', '50');
  });

  it('should use the shared theme track and fill tokens', () => {
    render(<ProgressBar value={50} />);

    const track = screen.getByRole('progressbar');
    const fill = track.querySelector('[data-pf-progress-fill]');

    expect(track).toHaveClass('bg-pf-progress-track');
    expect(fill).toHaveClass('bg-pf-progress-fill');
    expect(fill).toHaveStyle({ width: '50%' });
  });

  it('should omit the default fill token when a custom fill class is provided', () => {
    render(<ProgressBar value={50} fillClassName="bg-pf-error" />);

    const track = screen.getByRole('progressbar');
    const fill = track.querySelector('[data-pf-progress-fill]');

    expect(track).toHaveClass('bg-pf-progress-track');
    expect(fill).toHaveClass('bg-pf-error');
    expect(fill).not.toHaveClass('bg-pf-progress-fill');
  });

  it('should map non-100 max values to a percent width while preserving aria value', () => {
    render(<ProgressBar value={25} max={50} showPercent={false} />);

    const track = screen.getByRole('progressbar');
    const fill = track.querySelector('[data-pf-progress-fill]');

    expect(track).toHaveAttribute('aria-valuenow', '25');
    expect(track).toHaveAttribute('aria-valuemax', '50');
    expect(fill).toHaveStyle({ width: '50%' });
  });

  it('should render with label', () => {
    render(<ProgressBar value={25} label="Upload Progress" />);
    
    expect(screen.getByText('Upload Progress')).toBeInTheDocument();
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-label', 'Upload Progress');
  });

  it('should show percentage by default', () => {
    render(<ProgressBar value={75} />);
    
    expect(screen.getByText('75%')).toBeInTheDocument();
  });

  it('should hide percentage when showPercent is false', () => {
    render(<ProgressBar value={75} showPercent={false} />);
    
    expect(screen.queryByText('75%')).not.toBeInTheDocument();
  });

  it('should clamp value to 0-100 range (max)', () => {
    render(<ProgressBar value={150} />);
    
    const progressbar = screen.getByRole('progressbar');
    expect(progressbar).toHaveAttribute('aria-valuenow', '100');
    expect(screen.getByText('100%')).toBeInTheDocument();
  });

  it('should clamp value to 0-100 range (min)', () => {
    render(<ProgressBar value={-10} />);
    
    const progressbar = screen.getByRole('progressbar');
    expect(progressbar).toHaveAttribute('aria-valuenow', '0');
    expect(screen.getByText('0%')).toBeInTheDocument();
  });

  it('should round value to nearest integer', () => {
    render(<ProgressBar value={33.7} />);
    
    expect(screen.getByText('34%')).toBeInTheDocument();
  });

  it('should render with xs size', () => {
    render(<ProgressBar value={50} size="xs" />);
    
    expect(screen.getByRole('progressbar')).toBeInTheDocument();
  });

  it('should render with sm size (default)', () => {
    render(<ProgressBar value={50} size="sm" />);
    
    expect(screen.getByRole('progressbar')).toBeInTheDocument();
  });

  it('should render with md size', () => {
    render(<ProgressBar value={50} size="md" />);
    
    expect(screen.getByRole('progressbar')).toBeInTheDocument();
  });

  it('should have proper aria attributes', () => {
    render(<ProgressBar value={45} label="Download" />);
    
    const progressbar = screen.getByRole('progressbar');
    expect(progressbar).toHaveAttribute('aria-valuenow', '45');
    expect(progressbar).toHaveAttribute('aria-valuemin', '0');
    expect(progressbar).toHaveAttribute('aria-valuemax', '100');
  });

  it('should apply custom className', () => {
    render(<ProgressBar value={50} className="custom-progress" />);
    
    // The outer div should have the custom class
    const outerDiv = screen.getByRole('progressbar').parentElement;
    expect(outerDiv).toHaveClass('custom-progress');
  });

  it('should show both label and percent together', () => {
    render(<ProgressBar value={60} label="Loading" showPercent={true} />);
    
    expect(screen.getByText('Loading')).toBeInTheDocument();
    expect(screen.getByText('60%')).toBeInTheDocument();
  });
});
