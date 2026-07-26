import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { AdminLoading } from '../AdminLoading';

describe('AdminLoading', () => {
  it('defaults to the spinner variant', () => {
    render(<AdminLoading />);
    expect(screen.getByTestId('admin-loading-spinner')).toBeInTheDocument();
  });

  it.each(['spinner', 'table', 'form', 'list', 'card-grid'] as const)(
    'renders variant=%s with role=status and aria-busy',
    (variant) => {
      render(<AdminLoading variant={variant} />);
      const region = screen.getByRole('status');
      expect(region).toHaveAttribute('aria-busy', 'true');
      expect(region).toHaveAttribute('aria-live', 'polite');
      expect(region).toHaveAttribute('aria-label');
    },
  );

  it('exposes the custom label to assistive tech', () => {
    render(<AdminLoading variant="table" label="Loading printers" />);
    expect(screen.getByRole('status', { name: 'Loading printers' })).toBeInTheDocument();
  });

  it('table variant honours rows and cols', () => {
    const { container } = render(<AdminLoading variant="table" rows={3} cols={5} />);
    const rowGrids = container.querySelectorAll('[style*="grid-template-columns"]');
    expect(rowGrids).toHaveLength(3);
    const style = (rowGrids[0] as HTMLElement).getAttribute('style') ?? '';
    expect(style).toContain('repeat(5,');
  });

  it('table variant clamps cols to a safe range', () => {
    const { container } = render(<AdminLoading variant="table" rows={1} cols={99} />);
    const style = (container.querySelector('[style*="grid-template-columns"]') as HTMLElement).getAttribute('style') ?? '';
    // clamp should keep it well below 99
    expect(style).not.toContain('repeat(99,');
  });

  it('applies custom className to the outer element', () => {
    const { container } = render(<AdminLoading variant="list" className="my-loader" rows={1} />);
    expect(container.firstChild).toHaveClass('my-loader');
  });
});
