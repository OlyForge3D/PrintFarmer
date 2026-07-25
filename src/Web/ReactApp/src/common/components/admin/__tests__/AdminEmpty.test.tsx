import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { AdminEmpty } from '../AdminEmpty';

describe('AdminEmpty', () => {
  it('renders the title', () => {
    render(<AdminEmpty title="No printers configured" />);
    expect(screen.getByRole('heading', { level: 3, name: 'No printers configured' })).toBeInTheDocument();
  });

  it('renders description when provided', () => {
    render(<AdminEmpty title="Empty" description="Add one to get started" />);
    expect(screen.getByText('Add one to get started')).toBeInTheDocument();
  });

  it('omits description paragraph when not provided', () => {
    const { container } = render(<AdminEmpty title="Empty" />);
    expect(container.querySelectorAll('p')).toHaveLength(0);
  });

  it('renders icon when provided', () => {
    render(<AdminEmpty title="Empty" icon={<svg data-testid="ico" />} />);
    expect(screen.getByTestId('ico')).toBeInTheDocument();
  });

  it('renders primary and secondary actions side by side', () => {
    render(
      <AdminEmpty
        title="Empty"
        action={<button data-testid="primary" />}
        secondaryAction={<a data-testid="secondary" href="/x">docs</a>}
      />,
    );
    expect(screen.getByTestId('primary')).toBeInTheDocument();
    expect(screen.getByTestId('secondary')).toBeInTheDocument();
  });

  it('exposes role=status for polite live-region semantics', () => {
    render(<AdminEmpty title="Empty" />);
    expect(screen.getByRole('status')).toBeInTheDocument();
  });

  it('applies compact spacing when size=compact', () => {
    const { container } = render(<AdminEmpty title="Empty" size="compact" />);
    expect(container.firstChild).toHaveClass('py-8');
  });
});
