import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { PageTemplate } from '@/common/components/PageTemplate';
import { resetPageHeaderGuard } from '@/common/components/pageHeaderGuard';

beforeEach(() => {
  resetPageHeaderGuard();
});

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

describe('PageTemplate embedded mode', () => {
  it('renders content only, with no header, background or padding wrapper', () => {
    const { container } = render(
      <PageTemplate title="Embedded Page" subtitle="Should not appear" embedded>
        <p>content</p>
      </PageTemplate>,
    );

    // Embedded pages are mounted inside a shell that already renders page chrome.
    // Rendering any of our own would duplicate the h1 and double the background.
    expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument();
    expect(screen.queryByText('Should not appear')).not.toBeInTheDocument();
    expect(container.querySelector('[data-header-visible]')).toBeNull();
    expect(container.innerHTML).toBe('<p>content</p>');
  });

  it('ignores the parent back link when embedded', () => {
    render(
      <MemoryRouter>
        <PageTemplate title="Embedded Page" parent={{ label: 'Admin Control Center', to: '/admin' }} embedded>
          <p>content</p>
        </PageTemplate>
      </MemoryRouter>,
    );

    expect(screen.queryByRole('link', { name: 'Admin Control Center' })).not.toBeInTheDocument();
  });

  it('keeps the page actions, because the shell has no way to supply them', () => {
    render(
      <PageTemplate title="Webhooks" actions={<button type="button">Add Webhook</button>} embedded>
        <p>content</p>
      </PageTemplate>,
    );

    // Losing the only control that creates a record would be a functional
    // regression, not just a cosmetic one.
    expect(screen.getByRole('button', { name: 'Add Webhook' })).toBeInTheDocument();
  });

  it('renders the actions above the content so they read as a page toolbar', () => {
    const { container } = render(
      <PageTemplate title="Webhooks" actions={<button type="button">Add Webhook</button>} embedded>
        <p>content</p>
      </PageTemplate>,
    );

    const actionsRow = container.querySelector('div');
    expect(actionsRow?.querySelector('button')).not.toBeNull();
    expect(actionsRow?.className).toContain('justify-end');
    expect(actionsRow?.nextElementSibling?.textContent).toBe('content');
  });
});

describe('PageTemplate duplicate header guard', () => {
  beforeEach(() => {
    resetPageHeaderGuard();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    resetPageHeaderGuard();
  });

  it('warns when a page forgets to declare itself embedded inside a shell', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    render(
      <PageTemplate title="Admin Console">
        <PageTemplate title="User Management">
          <p>content</p>
        </PageTemplate>
      </PageTemplate>,
    );

    expect(warn).toHaveBeenCalledTimes(1);
    expect(String(warn.mock.calls[0][0])).toContain('"User Management"');
  });

  it('stays quiet for the shell plus a correctly embedded page', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    render(
      <PageTemplate title="Admin Console">
        <PageTemplate title="User Management" embedded>
          <p>content</p>
        </PageTemplate>
      </PageTemplate>,
    );

    expect(warn).not.toHaveBeenCalled();
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
  });

  it('stays quiet for a headerless template, which owns no h1 to collide with', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    render(
      <PageTemplate title="Admin Console">
        <PageTemplate title="Models" showHeader={false}>
          <p>content</p>
        </PageTemplate>
      </PageTemplate>,
    );

    expect(warn).not.toHaveBeenCalled();
  });
});

describe('PageTemplate parent back link', () => {
  it('links back to the parent surface above the title', () => {
    render(
      <MemoryRouter>
        <PageTemplate title="Users" parent={{ label: 'Admin Control Center', to: '/admin' }}>
          <p>content</p>
        </PageTemplate>
      </MemoryRouter>,
    );

    const link = screen.getByRole('link', { name: 'Admin Control Center' });
    expect(link).toHaveAttribute('href', '/admin');
    // The page still has exactly one h1; the back link is not a heading.
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
  });

  it('omits the back link when no parent is given', () => {
    render(
      <MemoryRouter>
        <PageTemplate title="Users">
          <p>content</p>
        </PageTemplate>
      </MemoryRouter>,
    );

    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('omits the back link when the header is hidden', () => {
    render(
      <MemoryRouter>
        <PageTemplate title="Users" parent={{ label: 'Admin Control Center', to: '/admin' }} showHeader={false}>
          <p>content</p>
        </PageTemplate>
      </MemoryRouter>,
    );

    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });
});
