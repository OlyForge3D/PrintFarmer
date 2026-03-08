import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { EmptyState } from '../../../../common/components/ui/EmptyState';

/**
 * Extended EmptyState tests — design token compliance & accessibility.
 * Basic rendering is covered by __tests__/EmptyState.test.tsx; these
 * verify the component uses pf-* tokens and correct semantic elements.
 */
describe('EmptyState — design tokens', () => {
  it('title uses pf-text-primary token', () => {
    render(<EmptyState title="Nothing here" />);
    const heading = screen.getByText('Nothing here');
    expect(heading.className).toMatch(/pf-text-primary/);
  });

  it('description uses pf-text-secondary token', () => {
    render(<EmptyState title="Empty" description="Try again later" />);
    const desc = screen.getByText('Try again later');
    expect(desc.className).toMatch(/pf-text-secondary/);
  });

  it('icon wrapper uses pf-text-tertiary token', () => {
    render(
      <EmptyState
        title="Empty"
        icon={<svg data-testid="icon" />}
      />,
    );
    const iconWrapper = screen.getByTestId('icon').parentElement!;
    expect(iconWrapper.className).toMatch(/pf-text-tertiary/);
  });

  it('does not use hardcoded gray/slate color classes', () => {
    const { container } = render(
      <EmptyState
        title="Empty"
        description="Details"
        icon={<svg data-testid="icon" />}
        action={<button>Go</button>}
      />,
    );

    const allClasses = Array.from(container.querySelectorAll('*'))
      .map((el) => el.className)
      .join(' ');

    expect(allClasses).not.toMatch(/\bgray-\d/);
    expect(allClasses).not.toMatch(/\bslate-\d/);
  });
});

describe('EmptyState — accessibility', () => {
  it('renders title as an h3 heading', () => {
    render(<EmptyState title="No data" />);
    const heading = screen.getByRole('heading', { level: 3 });
    expect(heading).toHaveTextContent('No data');
  });

  it('description is a paragraph element', () => {
    render(<EmptyState title="Empty" description="Some help text" />);
    const para = screen.getByText('Some help text');
    expect(para.tagName).toBe('P');
  });

  it('action buttons remain accessible', () => {
    render(
      <EmptyState
        title="Empty"
        action={<button aria-label="Add item">Add</button>}
      />,
    );
    expect(screen.getByRole('button', { name: 'Add item' })).toBeInTheDocument();
  });

  it('icon is presentational (no implicit role)', () => {
    render(
      <EmptyState
        title="Empty"
        icon={<svg data-testid="decorative-icon" aria-hidden="true" />}
      />,
    );
    const icon = screen.getByTestId('decorative-icon');
    expect(icon).toHaveAttribute('aria-hidden', 'true');
  });
});
