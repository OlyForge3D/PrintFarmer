import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PageTemplate } from '@/common/components/PageTemplate';

describe('PageTemplate heading semantics', () => {
  it('renders the page title as the single level-1 heading', () => {
    render(
      <PageTemplate title="Settings" subtitle="Manage things">
        <p>content</p>
      </PageTemplate>,
    );

    // The page title is the page's main heading and must be the single h1 so the
    // document has a proper, non-duplicated heading hierarchy.
    const h1 = screen.getByRole('heading', { level: 1, name: 'Settings' });
    expect(h1).toBeInTheDocument();
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
  });

  it('exposes the title via aria-label when the header is hidden', () => {
    const { container } = render(
      <PageTemplate title="Hidden Header Page" showHeader={false}>
        <p>content</p>
      </PageTemplate>,
    );

    expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument();
    expect(container.querySelector('[aria-label="Hidden Header Page"]')).not.toBeNull();
  });
});
