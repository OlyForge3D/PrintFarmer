import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { EmptyState } from '../EmptyState';

describe('EmptyState', () => {
  it('renders title', () => {
    render(<EmptyState title="No items found" />);
    expect(screen.getByText('No items found')).toBeInTheDocument();
  });

  it('renders description when provided', () => {
    render(<EmptyState title="Empty" description="Try adding some items" />);
    expect(screen.getByText('Try adding some items')).toBeInTheDocument();
  });

  it('does not render description when omitted', () => {
    const { container } = render(<EmptyState title="Empty" />);
    expect(container.querySelectorAll('p')).toHaveLength(0);
  });

  it('renders icon when provided', () => {
    render(
      <EmptyState
        title="Empty"
        icon={<svg data-testid="test-icon" />}
      />
    );
    expect(screen.getByTestId('test-icon')).toBeInTheDocument();
  });

  it('does not render icon wrapper when omitted', () => {
    const { container } = render(<EmptyState title="Empty" />);
    expect(container.querySelector('.opacity-40')).toBeNull();
  });

  it('renders action when provided', () => {
    render(
      <EmptyState
        title="Empty"
        action={<button>Add Item</button>}
      />
    );
    expect(screen.getByRole('button', { name: 'Add Item' })).toBeInTheDocument();
  });

  it('does not render action wrapper when omitted', () => {
    const { container } = render(<EmptyState title="Empty" />);
    expect(container.querySelector('.mt-4')).toBeNull();
  });

  it('applies custom className', () => {
    const { container } = render(<EmptyState title="Empty" className="custom-class" />);
    expect(container.firstChild).toHaveClass('custom-class');
  });

  it('renders all props together', () => {
    render(
      <EmptyState
        title="No printers"
        description="Add a printer to get started"
        icon={<svg data-testid="printer-icon" />}
        action={<button>Add Printer</button>}
      />
    );
    expect(screen.getByText('No printers')).toBeInTheDocument();
    expect(screen.getByText('Add a printer to get started')).toBeInTheDocument();
    expect(screen.getByTestId('printer-icon')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add Printer' })).toBeInTheDocument();
  });

  it('has centered layout classes', () => {
    const { container } = render(<EmptyState title="Test" />);
    const root = container.firstChild as HTMLElement;
    expect(root).toHaveClass('flex', 'flex-col', 'items-center', 'justify-center', 'text-center');
  });
});
